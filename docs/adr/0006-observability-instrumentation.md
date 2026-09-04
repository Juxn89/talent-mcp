# ADR-0006 · Observability instrumentation: seam, correlation, and span schema

| | |
|---|---|
| **Status** | Accepted |
| **Date** | 4 Sep 2026 |
| **Phase** | F4 (observability) |
| **Depends on** | [ADR-0002](./0002-native-aot-and-explicit-tool-registration.md) — explicit tool registration is why `AddCallToolFilter` cannot see the six tools; [ADR-0004](./0004-shared-tool-surface-across-both-hosts.md) — the instrumentation registers once and reaches both hosts |

## Context

`Talent.Mcp.Toolkit.Tracing.McpTraceContext` has existed since F1: it parses `traceparent`/
`tracestate`/`baggage` out of `_meta` and starts a parented `Activity`. It had 17 passing unit tests and
**zero production callers** going into F4 — nothing wired it into a real request. Two things had to be
worked out to actually turn a `tools/call` into a span, and both were wrong on the first attempt,
caught by running the existing tool-surface tests in parallel (xUnit's default) rather than only in
isolation.

**`AddCallToolFilter` does not fire for a registered tool.** Its own XML doc says it wraps a call to a
tool "that isn't found in the `McpServerTool` collection." All six tools here are registered via
`WithTools<T>()` (ADR-0002), so this request filter — the `tools/call` analogue of the
`AddListToolsFilter` already used for tool ordering — never sees any of them. This is the same
constraint `ToolScopeAuthorizationHandler` hit for scope enforcement, which is why that code reads the
`Mcp-Name` HTTP header instead — but that trick is ASP.NET-Core-only, and the stdio host has no HTTP
headers at all.

**Message filters are the right seam, but the obvious correlation key is wrong twice.** The lower-level
`WithMessageFilters` API (`AddIncomingFilter`/`AddOutgoingFilter`) wraps every JSON-RPC message
regardless of routing, on both transports — its own XML doc lists "request tracing" as a designed use
case. But `McpMessageHandler` returns a bare `Task`: the incoming pipeline never hands a filter the
response value, because incoming requests and outgoing responses run through two *separate* filter
lists. A span needs both — the incoming filter has the request (`tool.name`, `tool.input`, the `_meta`
to parent from), the outgoing filter has the response (whether it was an error, `tool.output_tokens`)
— so something has to correlate one request to its own response across that gap.

The first attempt keyed a static `ConcurrentDictionary` on `RequestId` alone. A JSON-RPC request id is
scoped to its own client connection — nothing stops two different clients from both sending `id: 1` for
their first call — so concurrent calls from different connections collided. Running
`Talent.Mcp.Tests` in parallel (many independent in-memory server/client pairs, each client numbering
its own requests from 1) turned this from a theoretical risk into a consistent, wrong-span-tagged
failure.

The second attempt paired the id with `MessageContext.Server`, reasoning that each connection gets its
own `McpServer`. A throwaway probe against `WithStreamServerTransport` (there is no public source for
either claim, so this was measured, not read) printed `context.Server.GetHashCode()` from both an
incoming filter and the outgoing filter for the response it produced:

```
[INCOMING] server=37489757 method=tools/call id=2
[OUTGOING] server=7141266  type=JsonRpcResponse id=2
```

Different instances, same logical call. The compound key never matched at all — not a concurrency bug,
a correctness bug that happened to be invisible in a single isolated test because the *incoming* filter
was also (wrongly) responsible for disposing the activity, so the span still got recorded even though
the outgoing filter's lookup silently failed.

The same probe, checking `MessageContext.Items` instead, showed it *does* flow from the incoming
context to the outgoing one for the response it produced — same dictionary instance, same object
identity, on both sides:

```
[INCOMING] items=37489757 id=2 set-marker
[OUTGOING] items=37489757 id=2 marker=hello-2
```

## Decision

**Use `WithMessageFilters`, correlated through `MessageContext.Items`, not a static dictionary keyed by
`RequestId` or `McpServer`.**

Registered once in `TalentTools.AddTalentTools` (`src/Talent.Mcp.Tools/TalentTools.cs`), next to the
existing `AddListToolsFilter` call, so both hosts get it for free and cannot drift apart in what they
instrument (ADR-0004).

Four points that are load-bearing rather than incidental:

**1. The incoming filter owns everything only it can see.** On a `JsonRpcRequest` whose method is
`tools/call`, it deserializes `CallToolRequestParams` via `McpJsonUtilities.DefaultOptions`, starts the
span through `McpTraceContext.StartServerActivity` (parented from `_meta.traceparent` when present),
tags `tool.name` and `tool.input`, and stashes the `Activity` in `context.Items`. Because
`context.Items` is per-request, this needs no static state and cannot collide across connections —
every request gets its own dictionary instance.

**2. The outgoing filter owns everything only it can see.** It reads the activity back out of
`context.Items`, tags `tool.output_tokens` and, when the response was an error (`JsonRpcError`, or a
`CallToolResult` with `IsError: true`), sets `ActivityStatusCode.Error`. It also disposes the activity —
not the incoming filter — because the response is written by a separate pump, not synchronously nested
inside the incoming call: an early version disposed in the incoming filter's `finally` and raced the
outgoing filter under load, passing in isolation and failing consistently under `dotnet test`'s default
parallelism.

**3. `db.query_time` needs an ambient carrier, not a filter parameter.** Neither filter is anywhere near
the repository calls a use case makes. `ToolTelemetryScope` is an `AsyncLocal`-backed accumulator the
incoming filter pushes before calling `next()`; `TimingJobRepository`/`TimingCandidateRepository` in
`Talent.Infrastructure` report elapsed time to `ToolTelemetryScope.Current` around each real EF Core
call, added as decorators in the DI registration so none of the five use cases change. Outside a tool
call (for example `Talent.Infrastructure.Tests`, which calls repositories directly) `Current` is
`null`, so this is a safe no-op rather than a crash.

**4. `cache.hit` and `oauth.token_refresh` are not tagged.** Neither has a real signal: there is no
server-side cache behind `ttlMs`/`cacheScope` (it is a client-facing freshness hint, nothing the server
itself consults), and this server is a pure OAuth 2.1 resource server that never refreshes a token —
confirmed by `AuthorizationCodeE2ETests`, which documents that no refresh flow is demonstrated anywhere
in this project. An absent tag is honest; a hardcoded `false` would misrepresent both as things this
server tracks and simply reports negative. AGENTS.md's Observability section is corrected to match.

Three metrics on the shared `TalentMeter`, for the plan's three named Grafana panels: an
`ObservableGauge` reports `talent.tasks.in_flight` — a direct Postgres count of non-terminal tasks, not
a per-instance counter, because `PostgresMcpTaskStore` can run on several nodes at once (ADR-0003) and
only Postgres knows the true fleet-wide total; the outgoing filter records `talent.tool.duration`
(a `Histogram<double>`) and `talent.tool.errors` (a `Counter<long>`), both tagged by `tool.name`, for
"latency per tool" and "tasa de error." These three are Talent-owned rather than a reuse of the SDK's
own internal GenAI-semconv instrumentation (present in 2.2.0, found only by reflecting the DLL's string
table — `mcp.server.operation.duration` and friends — and undocumented in any public API): a dashboard
this project ships should not rest on an implementation detail that could rename or disappear.

## Alternatives considered

**Detecting the tool name from the `Mcp-Name` HTTP header, the way `ToolScopeAuthorizationHandler`
does.** Rejected: the stdio host has no HTTP headers at all, and one instrumentation seam that only
half-covers the tool surface is worse than the extra step of parsing the JSON-RPC body.

**Instrumenting each of the six tool methods directly**, reading and writing `Activity.Current` inline.
Rejected: six call sites to keep in sync instead of one filter pair, and the exact failure mode ADR-0002
already warns about for tool registration — an easy-to-miss omission on the seventh tool this project
never adds, but a real one for whoever forks this pattern.

**Keying `InFlight` on `RequestId` alone, then on `(McpServer, RequestId)`.** Both measured wrong; see
Context.

## Consequences

- **A connection that drops before its response goes out leaks one `Activity`.** Nothing disposes it,
  so it is never exported. Accepted as a best-effort observability gap, not a correctness concern the
  way a lost MRTR response would be (ADR-0003's sweep exists precisely because that case *is* a
  correctness concern; this one is not).
- **`ToolTelemetryScope` and `TalentActivitySource`/`TalentMeter` live in `Talent.Mcp.Toolkit`**,
  alongside `McpTraceContext`, `HandleCodec` and `CachePolicies` — domain-agnostic primitives a second
  MCP server would also want. `OpenTelemetry`, `OpenTelemetry.Extensions.Hosting` and
  `OpenTelemetry.Exporter.OpenTelemetryProtocol` are now Toolkit dependencies; `Talent.Domain` and
  `Talent.Application` remain unaffected (`ForbiddenAssemblyReferences` only restricts those two).
- **`McpTraceContext`'s public signature changed** from `IReadOnlyDictionary<string, JsonElement>?` to
  `JsonObject?`, matching what `RequestParams.Meta` actually is everywhere else in this codebase
  (`McpClientCapabilityReader` already used `JsonObject`). Safe to change outright, not add an overload
  for, because nothing in `src/` called the old signature — F4 is its first caller.

## Verification

- `tests/Talent.Mcp.Tests/McpTraceContextTests.cs` — updated to the `JsonObject` signature.
- `tests/Talent.Mcp.Tests/Tracing/ToolExecutionTelemetryTests.cs` — over the real in-memory transport,
  not the filter delegates in isolation: a call produces a span with the four documented tags and no
  `cache.hit`/`oauth.token_refresh`; an `IsError` result sets `ActivityStatusCode.Error`; a
  `_meta.traceparent` parents the span. Run five times back to back under `dotnet test`'s default
  parallelism as part of landing this ADR — the exact condition that caught both correlation bugs.
- `tests/Talent.Infrastructure.Tests/TimingRepositoryTests.cs` — against real Postgres: a call reports
  non-zero `db.query_time`, multiple calls accumulate rather than overwrite, and a call outside any
  scope still succeeds.
- `tests/Talent.Mcp.Conformance/ObservabilityConformanceTests.cs` and
  `tests/Talent.Mcp.E2E/TracePropagationE2ETests.cs` — a real HTTP `tools/call` (E2E: through OAuth,
  against the full compose-shaped stack) carrying `_meta.traceparent` produces a correctly parented
  span. Both assert on `_meta`, not an HTTP header — MCP carries trace context in `_meta` under this
  revision, not as a header, which is what the plan's "trace propagation in headers" bullet meant.
