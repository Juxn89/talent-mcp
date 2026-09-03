namespace Talent.Mcp.E2E;

using System.Net.Http.Json;
using System.Security.Cryptography;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.AspNetCore;
using ModelContextProtocol.Client;
using ModelContextProtocol.Extensions.Tasks;
using ModelContextProtocol.Protocol;
using Talent.Infrastructure;
using Talent.Infrastructure.DependencyInjection;
using Talent.Infrastructure.Seeding;
using Talent.Mcp.Server.Authentication;
using Talent.Mcp.Tools;
using Talent.Mcp.Toolkit.Tasks;
using Testcontainers.Keycloak;
using Testcontainers.PostgreSql;
using Xunit;

/// <summary>
/// The real <c>Talent.Mcp.Server</c> composition, started against a real Postgres and listening on a
/// real loopback socket for the lifetime of the test class.
/// <para>
/// Deliberately not <c>WebApplicationFactory&lt;Program&gt;</c>. That helper intercepts
/// <c>WebApplicationBuilder.Build()</c> via reflection on the entry point, but this host reads its
/// connection string and signing key from configuration <em>before</em> <c>Build()</c> is ever called
/// (<see cref="TalentInfrastructureServiceCollectionExtensions.CreateAndPrepareTaskStoreAsync"/> needs a
/// concrete task store instance to hand to <c>WithTasks(...)</c> ahead of the service provider existing —
/// see ADR-0003) — by the time a factory's <c>ConfigureAppConfiguration</c> callback could inject a test
/// connection string, the host would already have tried to read the real one. Building the same six
/// calls <c>Program.cs</c> makes, by hand, against configuration this fixture controls from the start, is
/// simpler than fighting that ordering.
/// </para>
/// <para>
/// Not the in-memory pipe transport <c>Talent.Mcp.Tests</c> uses, either. That transport is what let the
/// <c>McpServer.ClientCapabilities</c>-is-null-under-real-HTTP bug (see
/// <c>docs/verification/sdk-2.2.0-tool-surface-behaviour.md</c>) stay hidden behind 135 green tests during
/// F2 — it populates a session-shaped property no real Streamable HTTP request does. A real socket is the
/// only transport that has ever caught that class of bug here, so it is the one this level uses.
/// </para>
/// </summary>
public sealed class RealServerFixture : IAsyncLifetime
{
    /// <summary>
    /// Pinned to the same image tag as <c>deploy/compose.yaml</c> and <c>Talent.Infrastructure.Tests</c>.
    /// </summary>
    private readonly PostgreSqlContainer container = new PostgreSqlBuilder("postgres:18.6-alpine")
        .WithDatabase("talent_e2e")
        .WithUsername("talent")
        .WithPassword("talent")
        .Build();

    /// <summary>
    /// Pinned to the same image tag as <c>deploy/compose.yaml</c>, importing the same realm — so what
    /// this suite proves against Keycloak is the configuration that actually ships, not a stand-in.
    /// </summary>
    private readonly KeycloakContainer keycloakContainer = new KeycloakBuilder("quay.io/keycloak/keycloak:26.7.2")
        .WithRealm(RealmConfigurationFilePath)
        .Build();

    private readonly HttpClient httpClient = new();

    private WebApplication? app;
    private PostgresMcpTaskStore? taskStore;

    /// <summary>The <c>/mcp</c> endpoint of the running host.</summary>
    public Uri McpEndpoint { get; private set; } = null!;

    /// <summary>
    /// The realm's issuer, e.g. <c>http://127.0.0.1:32812/realms/talent</c> — the same value both this
    /// fixture's resource-server wiring and <see cref="MintTokenAsync"/> resolve Keycloak through.
    /// </summary>
    public Uri Authority { get; private set; } = null!;

    /// <summary>
    /// Repo-relative path to <c>deploy/keycloak/realm.json</c>, resolved from this file's own location
    /// at compile time rather than from the working directory <c>dotnet test</c> happens to run from.
    /// </summary>
    private static string RealmConfigurationFilePath { get; } = ResolveRealmConfigurationFilePath();

    private static string ResolveRealmConfigurationFilePath(
        [System.Runtime.CompilerServices.CallerFilePath] string thisFilePath = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(thisFilePath)!, "..", "..", "deploy", "keycloak", "realm.json"));

    /// <summary>
    /// Starts Postgres and Keycloak, migrates and seeds Postgres, then starts the real HTTP host —
    /// wired as an OAuth 2.1 resource server — against both.
    /// </summary>
    public async Task InitializeAsync()
    {
        await Task.WhenAll(
            this.container.StartAsync(),
            this.keycloakContainer.StartAsync()).ConfigureAwait(false);

        this.Authority = new Uri(new Uri(this.keycloakContainer.GetBaseAddress()), "realms/talent");

        var builder = WebApplication.CreateBuilder();

        // Bound to loopback on an OS-assigned port — parallel test runs must not collide, and nothing
        // here needs a fixed port.
        builder.WebHost.UseUrls("http://127.0.0.1:0");

        // Same configuration keys Program.cs requires in production
        // (TalentInfrastructureServiceCollectionExtensions.ConnectionStringName / SigningKeyPath /
        // TalentAuthenticationServiceCollectionExtensions.AuthorityPath), set here instead of read from
        // the environment. DefaultPageSize is lowered so the 12 seeded jobs span three pages of 5 —
        // pagination that never crosses a page boundary would not test anything handle-shaped.
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:Talent"] = this.container.GetConnectionString(),
            ["Talent:HandleSigningKey"] = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)),
            ["Talent:DefaultPageSize"] = "5",
            [TalentAuthenticationServiceCollectionExtensions.AuthorityPath] = this.Authority.ToString(),
        });

        builder.Logging.ClearProviders();
        if (Environment.GetEnvironmentVariable("E2E_LOG") == "1")
        {
            builder.Logging.AddConsole();
        }

        builder.Services.AddTalentInfrastructure(builder.Configuration);

        // requireHttpsMetadata: false — Testcontainers' Keycloak serves plain HTTP on its mapped port,
        // same as the compose stack's dev configuration.
        builder.Services.AddTalentAuthentication(builder.Configuration, requireHttpsMetadata: false);

        this.taskStore = await TalentInfrastructureServiceCollectionExtensions
            .CreateAndPrepareTaskStoreAsync(builder.Configuration)
            .ConfigureAwait(false);
        builder.Services.AddSingleton(this.taskStore);

        builder.Services
            .AddMcpServer(options => options.ServerInfo = TalentServerInfo.Value)
            .WithHttpTransport(options => options.SessionMode = HttpServerSessionMode.Stateless)
            .AddTalentTools(this.taskStore);

        this.app = builder.Build();

        this.app.UseAuthentication();
        this.app.UseAuthorization();

        // Migrate and seed before accepting requests. TalentSeeder is idempotent, so a shared fixture
        // reused across a test collection would converge rather than duplicate — not exercised here
        // since each test class gets its own container, but worth keeping true regardless.
        await using (var scope = this.app.Services.CreateAsyncScope())
        {
            var context = scope.ServiceProvider.GetRequiredService<TalentDbContext>();
            await TalentSeeder.SeedAsync(context).ConfigureAwait(false);
        }

        await this.taskStore.StartAsync().ConfigureAwait(false);

        this.app.MapMcp("/mcp").RequireAuthorization(TalentAuthenticationServiceCollectionExtensions.RequireToolScopePolicy);

        await this.app.StartAsync().ConfigureAwait(false);

        var address = this.app.Services
            .GetRequiredService<IServer>()
            .Features.Get<IServerAddressesFeature>()!
            .Addresses.First();

        this.McpEndpoint = new Uri(new Uri(address), "/mcp");
    }

    /// <summary>
    /// Mints an access token directly against Keycloak's token endpoint using the Resource Owner
    /// Password grant on <c>talent-mcp-client</c>.
    /// <para>
    /// Dev-only and deliberately so — see <c>deploy/keycloak/README.md</c>: this grant exists on the
    /// realm specifically so a suite like this one can get a token without driving a browser through
    /// the authorization_code + PKCE flow. It is what lets the scope-allow and scope-deny tests be about
    /// scopes rather than about browser automation. The full authorization_code + PKCE flow, including
    /// <c>ClientOAuthOptions.AuthorizationCallbackHandler</c> and RFC 9207 issuer validation, is
    /// exercised separately — see <see cref="AuthorizationCodeE2ETests"/>.
    /// </para>
    /// </summary>
    /// <param name="scope">Space-delimited scopes to request, e.g. <c>"openid talent.jobs.read"</c>.</param>
    /// <returns>The bearer access token.</returns>
    public async Task<string> MintTokenAsync(string scope)
    {
        // Not new Uri(this.Authority, "protocol/..."): Authority has no trailing slash ("/realms/
        // talent"), and RFC 3986 relative resolution against a base with no trailing slash replaces
        // the last segment rather than appending — silently landing on ".../realms/protocol/..." and
        // a 404 that has nothing to do with the token request itself. Building the absolute string
        // directly sidesteps that.
        var tokenEndpoint = new Uri($"{this.Authority}/protocol/openid-connect/token");
        var response = await this.httpClient.PostAsync(
            tokenEndpoint,
            new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["grant_type"] = "password",
                ["client_id"] = "talent-mcp-client",
                ["username"] = "recruiter",
                ["password"] = "recruiter",
                ["scope"] = scope,
            })).ConfigureAwait(false);

        response.EnsureSuccessStatusCode();
        var payload = await response.Content.ReadFromJsonAsync<TokenResponse>().ConfigureAwait(false);
        return payload!.AccessToken;
    }

    private sealed record TokenResponse(
        [property: System.Text.Json.Serialization.JsonPropertyName("access_token")] string AccessToken);

    /// <summary>Stops the host and the containers.</summary>
    public async Task DisposeAsync()
    {
        if (this.app is not null)
        {
            await this.app.StopAsync().ConfigureAwait(false);
            await this.app.DisposeAsync().ConfigureAwait(false);
        }

        if (this.taskStore is not null)
        {
            await this.taskStore.DisposeAsync().ConfigureAwait(false);
        }

        this.httpClient.Dispose();
        await this.container.DisposeAsync().ConfigureAwait(false);
        await this.keycloakContainer.DisposeAsync().ConfigureAwait(false);
    }

    /// <summary>
    /// Connects a real MCP client to the running host over its actual HTTP endpoint.
    /// </summary>
    /// <param name="accessToken">
    /// Bearer token to send with every request, typically from <see cref="MintTokenAsync"/>. Omit to
    /// connect without one — every tool call is then denied at the endpoint's authentication
    /// requirement, which is itself a case worth asserting.
    /// </param>
    /// <param name="elicitationHandler">
    /// How the client answers an MRTR elicitation. Leaving it null makes the client not declare the
    /// elicitation capability at all — the same contract as <c>ToolHarness.StartAsync</c> in
    /// <c>Talent.Mcp.Tests</c>.
    /// </param>
    /// <param name="protocolVersion">
    /// Protocol revision the client declares. Set it to an older revision (see
    /// <c>Mcp.ProtocolVersions.Interop</c>) to get the real degraded case the tool surface is built for,
    /// rather than fabricating one.
    /// </param>
    /// <returns>A connected client; the caller disposes it.</returns>
    public async Task<McpClient> CreateClientAsync(
        string? accessToken = null,
        Func<ElicitRequestParams?, CancellationToken, ValueTask<ElicitResult>>? elicitationHandler = null,
        string? protocolVersion = null)
    {
        var transportOptions = new HttpClientTransportOptions { Endpoint = this.McpEndpoint };
        if (accessToken is not null)
        {
            transportOptions.AdditionalHeaders = new Dictionary<string, string>
            {
                ["Authorization"] = $"Bearer {accessToken}",
            };
        }

        var transport = new HttpClientTransport(transportOptions);

        var clientOptions = new McpClientOptions();
        if (elicitationHandler is not null)
        {
            clientOptions.Handlers.ElicitationHandler = elicitationHandler;
        }

        if (protocolVersion is not null)
        {
            clientOptions.ProtocolVersion = protocolVersion;
        }

        return await McpClient.CreateAsync(transport, clientOptions).ConfigureAwait(false);
    }

    /// <summary>Opens a fresh context against the fixture's database, for asserting persisted state directly.</summary>
    /// <returns>A new context; the caller disposes it.</returns>
    public TalentDbContext CreateDbContext() =>
        new(new DbContextOptionsBuilder<TalentDbContext>()
            .UseNpgsql(this.container.GetConnectionString())
            .Options);
}

/// <summary>Marks a test class as owning one dedicated real server + Postgres instance.</summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class RealServerCollection : ICollectionFixture<RealServerFixture>
{
    /// <summary>The collection name.</summary>
    public const string Name = "real-server";
}
