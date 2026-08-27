# Verification · MCP C# SDK changelog review, 2.0.0 → 2.2.0

**Reviewed:** 27 Aug 2026 · **Phase:** F0 · **Outcome:** no breaking changes; four findings that
change what gets written in F2/F3, one of which became [ADR-0001](../adr/0001-streamable-http-session-mode.md).

## Why this review existed

Risk #2 in the plan. The plan's "verified ground" section was written against the SDK's **2.0.0**
release notes. 2.0.0 shipped 28 Jul 2026 — the same day as the protocol revision — and two minor
releases landed after it:

| Release | Date | Nature |
|---|---|---|
| `v2.0.0` | 2026-07-28 | The revision. What the plan was written from. |
| `v2.1.0` | 2026-08-05 | `subscriptions/listen` handler, HTTP transport fallback reliability |
| `v2.2.0` | 2026-08-13 | `HttpServerSessionMode`, `McpHeader` decode fix |

Verified against the GitHub releases API and, for every API claim below, against the source at tag
`v2.2.0` rather than the release note prose.

**Headline: nothing in 2.1.0 or 2.2.0 invalidates the plan's design.** No breaking changes, no
removed API the plan depends on. The plan's core bets — stateless HTTP, signed handles, MRTR for the
destructive tool, Tasks with a Postgres store, per-tool OAuth scopes — all still hold.

## Finding 1 — `HttpServerSessionMode` supersedes the `Stateless` boolean (2.2.0)

Became **[ADR-0001](../adr/0001-streamable-http-session-mode.md)**. Summary: a three-valued enum
replaces the binary choice, and two of the plan's specific claims were wrong:

- `HttpServerTransportOptions.Stateless` is **not** `[Obsolete]` — it is a convenience proxy over the
  new `SessionMode` property. Assigning `false` emits nothing.
- `MCP9006` is carried by five *session-only* properties: `EventStreamStore`,
  `SessionMigrationHandler`, `PerSessionExecutionContext`, `IdleTimeout`, `MaxIdleSessionCount`.

Consequence for the test suite: the plan's "downgrade negotiation against a 2025-11-25 client"
conformance test asserts behaviour this server will not have. See the ADR.

## Finding 2 — `server/discover` has an `initialize` fallback at the HTTP layer (2.1.0, [#1766])

A client with `ProtocolVersion` unset probes `server/discover` and is expected to fall back to the
`initialize` handshake for down-level servers. In 2.0.0 that fallback was keyed **only** on
`McpProtocolException` plus the probe timeout, so it fired only when the server answered with a
JSON-RPC error. A server that rejects the session-less probe at the HTTP layer instead — a `404`,
as a hosted Datadog server does — surfaced `HttpRequestException`, which nothing in `ConnectAsync`
caught, and the connection failed outright. That was a **regression from 1.4.x**, fixed in 2.1.0 by
treating HTTP 404 from the probe as evidence of an initialize-handshake server.

Consequence: `server/discover` is still mandatory to implement and the conformance test still
asserts its shape. But the test must not assert that a client **fails** when discover is absent —
as of 2.1.0 a correctly-behaving client falls back instead. Assert the response shape, not the
client's failure.

## Finding 3 — 2.2.0 is a floor, not a preference

Two fixes in the 2.0.0–2.2.0 window land directly on paths this project uses:

- **`McpHeaderEncoder.DecodeValue` threw on a degenerate base64 wrapper** ([#1805], 2.2.0). `get_job`
  is specified to promote region routing to a header via `[McpHeader("Region")]`, which is exactly
  this code path.
- **HTTP status codes were not preserved across target frameworks** ([#1767], 2.1.0), fixed in
  `HttpResponseMessageExtensions` and `AutoDetectingClientSessionTransport`. The E2E suite asserts a
  `401` for an absent token and a denial for a missing scope, so status-code fidelity is load-bearing.

Consequence: pin `>= 2.2.0`, and treat a downgrade as a regression rather than a lateral move. Do
not "simplify" the pin to a 2.0.x or 2.1.x line.

## Finding 4 — `subscriptions/listen` is new surface, and out of scope (2.1.0, [#1775])

SEP-2575 added an opt-in typed server handler: `McpServerHandlers.SubscriptionsListenHandler` with a
`WithSubscriptionsListenHandler(...)` builder method. When set it fully replaces built-in
`subscriptions/listen` handling — it owns the stream, sends the acknowledgement, tags notifications
with the subscription id, and receives no automatic `*/list_changed` fan-out. Protocol-version
gating (2026-07-28+) is enforced by the SDK, and no capabilities are auto-advertised.

**A2 does not use it.** None of the six tools is a subscription, and adding one would expand the
conformance surface for no additional demonstration. Recorded here so a later session recognises it
as a deliberate omission rather than an oversight.

One detail from that PR is worth keeping even though the feature is unused: under stateless
Streamable HTTP, **the held-open POST is the only solicited server-to-client channel**. That is the
constraint behind `bulk_score_shortlist` being polled through the Tasks extension.

## Finding 5 — the deprecation diagnostics, verified exactly

Read from `src/Common/Obsoletions.cs` at `v2.2.0`. The plan named `MCP9005`, `MCP9006` and `MCP9007`;
this is the full set, and `MCP9002` does not exist in 2.2.0:

| Id | Applies to | Message gist |
|---|---|---|
| `MCP9001` | `EnumSchema`, `LegacyTitledEnumSchema` | Deprecated as of 2025-11-25. See SEP-1330 |
| `MCP9003` | `RequestContextParams` ctor | Use the overload that accepts a parameters argument |
| `MCP9004` | `EnableLegacySse` | Legacy SSE has no built-in request backpressure. Use Streamable HTTP |
| `MCP9005` | Roots, Sampling, Logging | Deprecated as of 2026-07-28. See SEP-2577 |
| `MCP9006` | The five session-only HTTP options | Back-compat escape hatch; set `SessionMode = Stateless` |
| `MCP9007` | `AuthorizationRedirectDelegate` | Cannot provide the RFC 9207 issuer. Use `AuthorizationCallbackHandler` |

**`MCP9001` was not on anyone's radar and is the one most likely to bite.** The domain has a
`SkillCategory` enum and `extract_skills` normalizes against a taxonomy, so tool input schemas will
carry enum-typed parameters — the exact place `EnumSchema` would get reached for. With
`TreatWarningsAsErrors=true` it fails the build rather than warning, which is the desired outcome,
but only if nobody suppresses it.

## Still open in F0 after this review

- Native AOT vs `WithToolsFromAssembly()` reflection-based discovery (risk #1)
- `deploy/compose.yaml` skeleton with Postgres and Keycloak starting

Neither is affected by the findings above.

[#1766]: https://github.com/modelcontextprotocol/csharp-sdk/pull/1766
[#1767]: https://github.com/modelcontextprotocol/csharp-sdk/pull/1767
[#1775]: https://github.com/modelcontextprotocol/csharp-sdk/pull/1775
[#1805]: https://github.com/modelcontextprotocol/csharp-sdk/pull/1805
