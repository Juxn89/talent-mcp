# Talent.Mcp.Toolkit

Domain-agnostic building blocks for a **Model Context Protocol** server on the **2026-07-28
revision** — the one that removed sessions.

Not a wrapper around the official SDK. These are the pieces the SDK does not ship and the revision
made necessary.

## What is in it

**`HandleCodec` — signed, TTL-bounded handles.** SEP-2567 removed `Mcp-Session-Id` and SEP-2575
removed the `initialize` handshake, so state between calls travels as ordinary tool arguments. A raw
offset would be client-editable, which makes a pagination cursor an access-control hole rather than a
convenience. Handles are HMAC-signed, expire, and carry a payload-type marker so a handle minted for
one tool cannot be replayed at another — deserialization alone does not catch that, because
`System.Text.Json` is lenient enough to read a shortlist payload as a cursor and hand you offset 0.

```csharp
using var codec = new HandleCodec(signingKey);

var handle = codec.Mint(cursor, MyJsonContext.Default.Cursor, TimeSpan.FromMinutes(10));

if (codec.TryRead(handle, MyJsonContext.Default.Cursor, out var read))
{
    // authentic, unexpired, and minted for this payload type
}
```

The `JsonTypeInfo` overloads are trim- and AOT-safe. Overloads without one still exist but are
`[Obsolete]` and carry `[RequiresUnreferencedCode]`.

**`PostgresMcpTaskStore` — an `IMcpTaskStore` that survives a restart.** The SDK ships only
`InMemoryMcpTaskStore`. The hard part is not persistence: the SDK subscribes to `InputResponseReceived`
in the process running the task and blocks, while `ResolveInputRequestsAsync` is called by whichever
process handles the client's follow-up — a different one, under a stateless server. Solved with
Postgres `LISTEN`/`NOTIFY` plus a sweep fallback. Uses raw Npgsql, not EF Core, so it imposes no ORM
on you.

**Cache policies.** The revision requires `ttlMs` and `cacheScope` on every `*/list` response and on
`resources/read`. `CachePolicies` stamps both in one call, because omitting either is a conformance
failure and two separate properties are two chances to forget.

**OpenTelemetry context from `_meta`.** `McpTraceContext` extracts `traceparent`, `tracestate` and
`baggage` into an `Activity`, so one trace spans client → server → database.

## Install

```bash
dotnet add package Talent.Mcp.Toolkit
```

Targets .NET 10. Apache-2.0.

## Where it comes from

Extracted from [talent-mcp](https://github.com/Juxn89/talent-mcp), an MCP server for the recruitment
domain. Nothing about recruitment leaks in here — an architecture test enforces it, so this package
stays usable for any domain.

The reasoning behind the less obvious decisions, including what was tried and rejected, is in that
repository's [ADRs](https://github.com/Juxn89/talent-mcp/tree/main/docs/adr).
