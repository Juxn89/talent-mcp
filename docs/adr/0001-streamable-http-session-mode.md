# ADR-0001 · Streamable HTTP session mode: explicit `Stateless`

| | |
|---|---|
| **Status** | Accepted |
| **Date** | 27 Aug 2026 |
| **Phase** | F0 (risk spike) |
| **Supersedes** | — |

## Context

The 2026-07-28 revision removed sessions from Streamable HTTP: SEP-2567 removed the
`Mcp-Session-Id` header and SEP-2575 removed the `initialize` handshake. State between calls is
carried by server-minted handles passed as ordinary tool arguments.

The plan was written against the SDK's **2.0.0** release notes, and asserted that the server-side
knob is a boolean — `HttpServerTransportOptions.Stateless`, `true` by default, with `false` emitting
`MCP9006`. The F0 task "verify the 2.0.0 → 2.2.0 changelog" was created precisely because two minor
releases landed after those notes: **2.1.0** (5 Aug 2026) and **2.2.0** (13 Aug 2026).

That review was performed on 27 Aug 2026 and the boolean is no longer the whole story.

**SDK 2.2.0 introduced `HttpServerSessionMode`** ([#1796], closing [#1777]), a three-valued enum on
`HttpServerTransportOptions`. The old boolean survives as a convenience proxy over it:

```csharp
// src/ModelContextProtocol.AspNetCore/HttpServerTransportOptions.cs @ v2.2.0
public HttpServerSessionMode SessionMode { get; set; } = HttpServerSessionMode.Stateless;

public bool Stateless
{
    get => SessionMode is HttpServerSessionMode.Stateless;
    set => SessionMode = value ? HttpServerSessionMode.Stateless : HttpServerSessionMode.Stateful;
}
```

| Mode | `initialize` clients (2025-11-25 and earlier) | 2026-07-28 and later clients |
|---|---|---|
| `Stateless` (default) | Served statelessly | Served statelessly |
| `Stateful` | Full session with `Mcp-Session-Id` | **Refused** with `-32022 UnsupportedProtocolVersion` to force a downgrade |
| `StatefulForInitializeClients` | Full session with `Mcp-Session-Id`, GET/DELETE available | Served statelessly, GET/DELETE → `405` |

Two corrections to what the plan assumed, both verified in source:

1. **`Stateless` is not `[Obsolete]`.** Assigning `false` compiles clean and emits nothing.
   `MCP9006` is carried by five *session-only* properties instead: `EventStreamStore`,
   `SessionMigrationHandler`, `PerSessionExecutionContext`, `IdleTimeout` and `MaxIdleSessionCount`.
2. The `MCP9006` message itself prescribes the new API:
   > "Stateful Streamable HTTP mode is a back-compat-only escape hatch for 2025-11-25 protocol
   > revision clients and earlier. Set `HttpServerTransportOptions.SessionMode =
   > HttpServerSessionMode.Stateless` (the default as of the 2026-07-28 protocol revision) for new
   > code. See SEP-2567."

## Decision

**Set `SessionMode = HttpServerSessionMode.Stateless` explicitly**, in both the HTTP host and the
conformance/E2E test fixtures. Do not rely on the default, and do not use the `Stateless` boolean
proxy.

```csharp
builder.Services.AddMcpServer()
    .WithHttpTransport(options =>
    {
        options.SessionMode = HttpServerSessionMode.Stateless;
    })
    .WithToolsFromAssembly();
```

Explicit rather than defaulted for two reasons: the intent is the single most load-bearing fact
about this server's design, so it should be readable at the call site; and a future SDK default
cannot silently move the server into a session era the rest of the codebase is not built for.

## Alternatives considered

**`Stateful`** — rejected outright. It refuses 2026-07-28 requests with `-32022` to force a
downgrade, which is the exact inverse of this project's purpose.

**`StatefulForInitializeClients`** — rejected for now, and this is the genuinely arguable one. The
hybrid mode is the correct answer for a production service migrating a real client population
progressively, and it is a more impressive thing to demonstrate. It is declined because it would
require the conformance and E2E suites to cover **two session eras on one endpoint** — session
minting and expiry, GET/DELETE stream endpoints, and the OAuth surface for both paths — which is a
disproportionate share of a 2-day risk spike and of the plan's ~4-5 week budget. The plan's own cut
order already signals that breadth of protocol surface is not where the remaining budget goes.

Revisit if A1 or the demo client turns out to need a session-era client. The option is recorded
here so the choice is known rather than rediscovered.

## Consequences

**The interaction model is fixed, not merely preferred.** Under `Stateless`, per the SDK's own
documentation of the enum: `McpSession.SessionId` is `null`, GET/DELETE/`/sse` are unavailable,
unsolicited server-to-client messages and all server-to-client requests are unsupported, and client
sampling, elicitation and roots are disabled. **MRTR is the only mechanism available** for asking
the client for input. This confirms rather than changes the plan's design for `reject_candidate`.

**The build enforces this ADR.** The five session-only properties are `[Obsolete]` with `MCP9006`,
and `TreatWarningsAsErrors=true` turns any use of them into a build failure. No extra guard needed.

**One conformance test must be rewritten before it is written.** The plan specifies "downgrade
negotiation against a 2025-11-25 client". Under `Stateless` there is no downgrade — that client is
served statelessly like any other. The test must assert the actual observable behaviour: a
2025-11-25 client is served, **no `Mcp-Session-Id` is minted or echoed**, and GET/DELETE return
`405 Method Not Allowed`. Asserting a `-32022` would be asserting `Stateful` behaviour we
deliberately do not have.

**`bulk_score_shortlist` cannot push progress out-of-band.** With no server-initiated messages, the
held-open POST is the only solicited server-to-client channel. Progress is observed by polling the
Tasks extension, which is what the plan already specified — now for a verified reason.

## Verification

- Changelog reviewed: [v2.1.0] (5 Aug 2026), [v2.2.0] (13 Aug 2026), against the [v2.0.0] notes the plan was written from.
- API surface read directly at tag `v2.2.0`:
  - `src/ModelContextProtocol.AspNetCore/HttpServerSessionMode.cs`
  - `src/ModelContextProtocol.AspNetCore/HttpServerTransportOptions.cs`
  - `src/Common/Obsoletions.cs` — `LegacyStatefulHttp_DiagnosticId = "MCP9006"` and its message
- A conformance test asserting the observable stateless behaviour above closes the loop in F2.

[#1777]: https://github.com/modelcontextprotocol/csharp-sdk/issues/1777
[#1796]: https://github.com/modelcontextprotocol/csharp-sdk/pull/1796
[v2.0.0]: https://github.com/modelcontextprotocol/csharp-sdk/releases/tag/v2.0.0
[v2.1.0]: https://github.com/modelcontextprotocol/csharp-sdk/releases/tag/v2.1.0
[v2.2.0]: https://github.com/modelcontextprotocol/csharp-sdk/releases/tag/v2.2.0
