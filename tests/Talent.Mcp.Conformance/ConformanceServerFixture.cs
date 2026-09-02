namespace Talent.Mcp.Conformance;

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
using Talent.Mcp.Tools;
using Talent.Mcp.Toolkit.Tasks;
using Testcontainers.PostgreSql;
using Xunit;

/// <summary>
/// The real <c>Talent.Mcp.Server</c> composition, started against a real Postgres and listening on a
/// real loopback socket, for asserting Streamable HTTP transport behaviour rather than business
/// outcomes.
/// <para>
/// Deliberately its own fixture rather than a shared one with <c>Talent.Mcp.E2E</c>'s
/// <c>RealServerFixture</c> — same reasoning as that file gives for not sharing with
/// <c>Talent.Mcp.Tests</c>: each test level owns the host it asserts against, so a change made for one
/// level's convenience cannot silently alter what another level is measuring. The construction is
/// necessarily near-identical (same ADR-0003 ordering constraint on the task store), but this fixture
/// also exposes a raw <see cref="HttpClient"/>, which <c>RealServerFixture</c> has no reason to.
/// </para>
/// </summary>
public sealed class ConformanceServerFixture : IAsyncLifetime
{
    /// <summary>Pinned to the same image tag as the other Testcontainers-based test projects.</summary>
    private readonly PostgreSqlContainer container = new PostgreSqlBuilder("postgres:18.6-alpine")
        .WithDatabase("talent_conformance")
        .WithUsername("talent")
        .WithPassword("talent")
        .Build();

    private WebApplication? app;
    private PostgresMcpTaskStore? taskStore;

    /// <summary>The <c>/mcp</c> endpoint of the running host.</summary>
    public Uri McpEndpoint { get; private set; } = null!;

    /// <summary>Starts Postgres, migrates it, then starts the real HTTP host against it.</summary>
    public async Task InitializeAsync()
    {
        await this.container.StartAsync().ConfigureAwait(false);

        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");

        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:Talent"] = this.container.GetConnectionString(),
            ["Talent:HandleSigningKey"] = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)),
        });

        builder.Logging.ClearProviders();
        if (Environment.GetEnvironmentVariable("CONFORMANCE_LOG") == "1")
        {
            builder.Logging.AddConsole();
        }

        builder.Services.AddTalentInfrastructure(builder.Configuration);

        this.taskStore = await TalentInfrastructureServiceCollectionExtensions
            .CreateAndPrepareTaskStoreAsync(builder.Configuration)
            .ConfigureAwait(false);
        builder.Services.AddSingleton(this.taskStore);

        builder.Services
            .AddMcpServer(options => options.ServerInfo = TalentServerInfo.Value)
            .WithHttpTransport(options => options.SessionMode = HttpServerSessionMode.Stateless)
            .AddTalentTools(this.taskStore);

        this.app = builder.Build();

        // No seed data: every conformance assertion is about transport and protocol shape, not
        // recruitment content, and reject_candidate's confirmation request is raised before the
        // candidate is looked up — so the MRTR shape test needs no real candidate to exist either. The
        // schema still has to be migrated, or the task store's own Postgres wiring has nothing to attach
        // to.
        await using (var scope = this.app.Services.CreateAsyncScope())
        {
            var context = scope.ServiceProvider.GetRequiredService<TalentDbContext>();
            await context.Database.MigrateAsync().ConfigureAwait(false);
        }

        await this.taskStore.StartAsync().ConfigureAwait(false);

        this.app.MapMcp("/mcp");

        await this.app.StartAsync().ConfigureAwait(false);

        var address = this.app.Services
            .GetRequiredService<IServer>()
            .Features.Get<IServerAddressesFeature>()!
            .Addresses.First();

        this.McpEndpoint = new Uri(new Uri(address), "/mcp");
    }

    /// <summary>Stops the host and the container.</summary>
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

        await this.container.DisposeAsync().ConfigureAwait(false);
    }

    /// <summary>
    /// Connects a real MCP client to the running host over its actual HTTP endpoint.
    /// </summary>
    /// <param name="elicitationHandler">
    /// How the client answers an MRTR elicitation. Leaving it null makes the client not declare the
    /// elicitation capability at all.
    /// </param>
    /// <param name="protocolVersion">
    /// Protocol revision the client declares. Set it to an older revision (see
    /// <c>Mcp.ProtocolVersions.Interop</c>) for the downgrade-negotiation tests.
    /// </param>
    /// <returns>A connected client; the caller disposes it.</returns>
    public async Task<McpClient> CreateClientAsync(
        Func<ElicitRequestParams?, CancellationToken, ValueTask<ElicitResult>>? elicitationHandler = null,
        string? protocolVersion = null)
    {
        var transport = new HttpClientTransport(new HttpClientTransportOptions { Endpoint = this.McpEndpoint });

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

    /// <summary>
    /// A plain <see cref="HttpClient"/> against the running host, for requests the SDK client will not
    /// build by hand — a GET or DELETE on <c>/mcp</c>, or a <c>tools/call</c> missing a header the SDK
    /// always sets.
    /// </summary>
    /// <returns>A new client; the caller disposes it.</returns>
    public HttpClient CreateHttpClient() => new();
}

/// <summary>Marks a test class as owning one dedicated real server + Postgres instance.</summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class ConformanceServerCollection : ICollectionFixture<ConformanceServerFixture>
{
    /// <summary>The collection name.</summary>
    public const string Name = "conformance-server";
}
