// stdio host for the recruitment tool surface — the process a client such as Claude Code launches
// once per session, published as the `talent-mcp` dotnet tool in F5.
//
// It serves the SAME tools as the HTTP host and reaches Postgres through the same adapters
// (ADR-0004). It is not a reduced build and not a thin proxy.
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Talent.Infrastructure.DependencyInjection;
using Talent.Mcp.Tools;
using Talent.Mcp.Toolkit.Tracing;
using Talent.Mcp.Server.Stdio;

// First statement, so the elapsed clock starts as close to process entry as managed code allows.
// Inert unless TALENT_STARTUP_TRACE is set. See StartupTrace for why the database work is marked
// separately rather than skipped.
StartupTrace.Mark("entry");

var builder = Host.CreateApplicationBuilder(args);

// MANDATORY, not a style choice. Host.CreateApplicationBuilder installs a console logger that writes
// to stdout — the same stream the JSON-RPC transport owns — so the first "response" a client reads is
// a log line and the session never recovers. Measured while running the ADR-0002 spike.
// This is also why the revision deprecated the MCP Logging API for stdio hosts in favour of stderr.
builder.Logging.ClearProviders();
builder.Logging.AddConsole(options => options.LogToStandardErrorThreshold = LogLevel.Trace);

// F4: traces + metrics only, same shared sources the HTTP host registers — no AddTalentOtlpLogging
// here. OTel's own logging exporter would reintroduce exactly the stdout-corruption risk the comment
// above exists to avoid, so this host's logs stay on stderr, never OTLP.
builder.Services.AddTalentTelemetry(builder.Configuration, serviceName: "talent-mcp-stdio");

builder.Services.AddTalentInfrastructure(builder.Configuration);
StartupTrace.Mark("services-registered");

// Built and schema-prepared before the service provider exists — WithTasks needs a concrete instance,
// not a DI factory — and started only after it. See ADR-0003 and the matching comment in the HTTP
// host's Program.cs.
StartupTrace.Mark("taskstore-prepare-start");
var taskStore = await TalentInfrastructureServiceCollectionExtensions
    .CreateAndPrepareTaskStoreAsync(builder.Configuration)
    .ConfigureAwait(false);
StartupTrace.Mark("taskstore-prepared");
builder.Services.AddSingleton(taskStore);

builder.Services
    .AddMcpServer(options => options.ServerInfo = TalentServerInfo.Value)
    .WithStdioServerTransport()
    .AddTalentTools(taskStore);

var host = builder.Build();

// Same opt-in as the HTTP host, and off by default for an extra reason here: this process is
// launched once per client session on a path F6 measured at ~315 ms to serving, and a migration
// check on every launch would tax every session forever. Useful when pointing `talent-mcp` at a
// fresh database; not something to leave on against a shared one.
await host.Services.MigrateAndSeedAsync(builder.Configuration).ConfigureAwait(false);

await taskStore.StartAsync().ConfigureAwait(false);

// Everything blocking is done; from here the transport is serving. The second database interaction
// -- StartAsync opening its own connection and awaiting the first LISTEN -- sits between the previous
// marker and this one, so it is attributable rather than folded into "MCP wiring".
StartupTrace.MarkReady("ready");

try
{
    await host.RunAsync().ConfigureAwait(false);
}
finally
{
    await taskStore.DisposeAsync().ConfigureAwait(false);
}
