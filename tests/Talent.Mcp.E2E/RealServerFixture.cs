namespace Talent.Mcp.E2E;

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

    private WebApplication? app;
    private PostgresMcpTaskStore? taskStore;

    /// <summary>The <c>/mcp</c> endpoint of the running host.</summary>
    public Uri McpEndpoint { get; private set; } = null!;

    /// <summary>
    /// Starts Postgres, migrates and seeds it, then starts the real HTTP host against it.
    /// </summary>
    public async Task InitializeAsync()
    {
        await this.container.StartAsync().ConfigureAwait(false);

        var builder = WebApplication.CreateBuilder();

        // Bound to loopback on an OS-assigned port — parallel test runs must not collide, and nothing
        // here needs a fixed port.
        builder.WebHost.UseUrls("http://127.0.0.1:0");

        // Same two configuration keys Program.cs requires in production
        // (TalentInfrastructureServiceCollectionExtensions.ConnectionStringName / SigningKeyPath), set
        // here instead of read from the environment. DefaultPageSize is lowered so the 12 seeded jobs
        // span three pages of 5 — pagination that never crosses a page boundary would not test anything
        // handle-shaped.
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:Talent"] = this.container.GetConnectionString(),
            ["Talent:HandleSigningKey"] = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)),
            ["Talent:DefaultPageSize"] = "5",
        });

        builder.Logging.ClearProviders();
        if (Environment.GetEnvironmentVariable("E2E_LOG") == "1")
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

        // Migrate and seed before accepting requests. TalentSeeder is idempotent, so a shared fixture
        // reused across a test collection would converge rather than duplicate — not exercised here
        // since each test class gets its own container, but worth keeping true regardless.
        await using (var scope = this.app.Services.CreateAsyncScope())
        {
            var context = scope.ServiceProvider.GetRequiredService<TalentDbContext>();
            await TalentSeeder.SeedAsync(context).ConfigureAwait(false);
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
