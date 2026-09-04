// Streamable HTTP host for the recruitment tool surface.
//
// Four decisions are visible at the call site on purpose, because they are the ones a reader of this
// file most needs to know and the ones cheapest to get wrong:
//
//   * SessionMode.Stateless      — ADR-0001. The 2026-07-28 revision removed sessions outright, and
//                                  state travels in signed handles passed as ordinary tool arguments.
//   * AddTalentTools()           — ADR-0002/ADR-0004. Explicit WithTools<T>() per type, never an
//                                  assembly scan, and the same registration both hosts use.
//   * The task store lifecycle  — ADR-0003. Built and schema-prepared before the service provider
//                                  exists (WithTasks needs a concrete instance, not a DI factory), and
//                                  started only after it — starting the cross-node listener is this
//                                  host's decision, not a side effect of construction.
//   * AddTalentAuthentication()  — F3. This host is an OAuth 2.1 resource server; the stdio host is not
//                                  (it has no HTTP surface to protect), which is why this wiring lives
//                                  here and not in Talent.Mcp.Tools.
using ModelContextProtocol.AspNetCore;
using OpenTelemetry.Trace;
using Talent.Infrastructure.DependencyInjection;
using Talent.Mcp.Server.Authentication;
using Talent.Mcp.Tools;
using Talent.Mcp.Toolkit.Tracing;

var builder = WebApplication.CreateBuilder(args);

// F4: traces + metrics on Talent.Mcp.Toolkit's shared sources, OTLP-exported when
// Talent:Otel:Endpoint is configured (the compose Collector in dev; unset and silently skipped in the
// unit/conformance/E2E fixtures, none of which run a Collector). ASP.NET Core request instrumentation
// and OTLP log export (→ Collector → Loki) are added here because they are HTTP-only — the stdio host
// calls AddTalentTelemetry too, but neither of these, since it has no HTTP surface and must keep
// logging to stderr instead (pitfall #11).
builder.Services.AddTalentTelemetry(
    builder.Configuration,
    serviceName: "talent-mcp-http",
    configureTracing: static tracing => tracing.AddAspNetCoreInstrumentation());
builder.Logging.AddTalentOtlpLogging(builder.Configuration, serviceName: "talent-mcp-http");

// The composition root, and the only place in the process that knows a database exists. Throws at
// startup when the connection string, the handle signing key or the tunables are missing or wrong.
builder.Services.AddTalentInfrastructure(builder.Configuration);

// Fails fast when no issuer is configured — see AddTalentAuthentication's own reasoning for why there
// is no fallback. Plain HTTP is fine talking to the compose/dev Keycloak; production sets ASPNETCORE_
// ENVIRONMENT so this flips to requiring HTTPS metadata.
builder.Services.AddTalentAuthentication(
    builder.Configuration,
    requireHttpsMetadata: !builder.Environment.IsDevelopment());

var taskStore = await TalentInfrastructureServiceCollectionExtensions
    .CreateAndPrepareTaskStoreAsync(builder.Configuration)
    .ConfigureAwait(false);
builder.Services.AddSingleton(taskStore);

builder.Services
    .AddMcpServer(options => options.ServerInfo = TalentServerInfo.Value)
    .WithHttpTransport(options => options.SessionMode = HttpServerSessionMode.Stateless)
    .AddTalentTools(taskStore);

var app = builder.Build();

app.UseAuthentication();
app.UseAuthorization();

// Start listening for cross-node input-response and bulk-shortlist notifications only now — after the
// app exists, before it starts accepting requests. A store that has not started still persists task
// state correctly (ADR-0003); it just has higher latency recovering from a missed notification.
await taskStore.StartAsync().ConfigureAwait(false);

// The MCP endpoint. GET and DELETE answer 405 under Stateless — there is no stream to resume and no
// session to terminate, and the conformance suite asserts exactly that. RequireAuthorization applies
// the per-tool scope policy: a missing/invalid token answers 401, a valid token missing the scope a
// called tool needs answers 403 — see ToolScopeAuthorizationHandler.
app.MapMcp("/mcp").RequireAuthorization(TalentAuthenticationServiceCollectionExtensions.RequireToolScopePolicy);

// Liveness only: it must not touch Postgres. A health probe that fails when the database is briefly
// unreachable makes the orchestrator restart a process that would have recovered on its own.
app.MapGet("/health", () => Results.Ok(new { status = "ok" }));

try
{
    await app.RunAsync().ConfigureAwait(false);
}
finally
{
    // Disposed explicitly rather than left to the container: AddSingleton(instance) registers an
    // externally-owned object, and this repo does not rely on undocumented framework disposal
    // behaviour for one — see PostgresMcpTaskStore's own "StartAsync is explicit" reasoning (ADR-0003).
    await taskStore.DisposeAsync().ConfigureAwait(false);
}
