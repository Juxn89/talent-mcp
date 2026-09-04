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

// Built and schema-prepared before the service provider exists — WithTasks needs a concrete instance,
// not a DI factory — and started only after it. See ADR-0003 and the matching comment in the HTTP
// host's Program.cs.
var taskStore = await TalentInfrastructureServiceCollectionExtensions
    .CreateAndPrepareTaskStoreAsync(builder.Configuration)
    .ConfigureAwait(false);
builder.Services.AddSingleton(taskStore);

builder.Services
    .AddMcpServer(options => options.ServerInfo = TalentServerInfo.Value)
    .WithStdioServerTransport()
    .AddTalentTools(taskStore);

var host = builder.Build();
await taskStore.StartAsync().ConfigureAwait(false);

try
{
    await host.RunAsync().ConfigureAwait(false);
}
finally
{
    await taskStore.DisposeAsync().ConfigureAwait(false);
}
