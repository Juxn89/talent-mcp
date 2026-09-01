// Streamable HTTP host for the recruitment tool surface.
//
// Two decisions are visible at the call site on purpose, because they are the two that a reader of
// this file most needs to know and the two that are cheapest to get wrong:
//
//   * SessionMode.Stateless      — ADR-0001. The 2026-07-28 revision removed sessions outright, and
//                                  state travels in signed handles passed as ordinary tool arguments.
//   * AddTalentTools()           — ADR-0002/ADR-0004. Explicit WithTools<T>() per type, never an
//                                  assembly scan, and the same registration both hosts use.
using ModelContextProtocol.AspNetCore;
using Talent.Infrastructure.DependencyInjection;
using Talent.Mcp.Tools;

var builder = WebApplication.CreateBuilder(args);

// The composition root, and the only place in the process that knows a database exists. Throws at
// startup when the connection string, the handle signing key or the tunables are missing or wrong.
builder.Services.AddTalentInfrastructure(builder.Configuration);

builder.Services
    .AddMcpServer(options => options.ServerInfo = TalentServerInfo.Value)
    .WithHttpTransport(options => options.SessionMode = HttpServerSessionMode.Stateless)
    .AddTalentTools();

var app = builder.Build();

// The MCP endpoint. GET and DELETE answer 405 under Stateless — there is no stream to resume and no
// session to terminate, and the conformance suite asserts exactly that.
app.MapMcp("/mcp");

// Liveness only: it must not touch Postgres. A health probe that fails when the database is briefly
// unreachable makes the orchestrator restart a process that would have recovered on its own.
app.MapGet("/health", () => Results.Ok(new { status = "ok" }));

await app.RunAsync().ConfigureAwait(false);
