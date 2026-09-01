# Verification · What SDK 2.2.0 actually does with a tool surface

| | |
|---|---|
| **Date** | 1 Sep 2026 |
| **Phase** | F2 (MCP tools) |
| **Method** | A real MCP server and a real MCP client over paired in-memory pipes, plus a reflection dump of the pinned 2.2.0 assemblies from the local NuGet cache |
| **Why** | Four assumptions the plan, `AGENTS.md` or ADR-0002 stated turned out to be wrong. Each is corrected at its source and pinned by a test |

Everything below was observed, not read from release notes. The probe was a throwaway test that printed
`tools/list` and four tool calls verbatim; the assertions it produced now live in
`tests/Talent.Mcp.Tests/Tools/`.

---

## 1. Registration order is **not** wire order

**Claimed** (ADR-0002, and repeated in `AGENTS.md`): *"Registration order is also how deterministic tool
ordering is achieved."*

**Observed.** Four tools registered in the order search / get / extract / score came back from
`tools/list` as:

```
extract_skills, get_job, score_candidate_fit, search_jobs
```

Alphabetical. `WithTools<T>()` adds to `McpServerOptions.ToolCollection`, a
`McpServerPrimitiveCollection<McpServerTool>`, and the wire order is that collection's enumeration
order — not the sequence of the builder calls.

**Why it matters beyond tidiness.** The revision asks for a deterministic `tools/list` because a stable
order improves an LLM's prompt-cache hit rate. Observing that this run's order happened to be stable is
not the same as a guarantee: it is a concurrent collection, and concurrent-collection enumeration order
is not documented as stable across process restarts. "Stable today" is exactly the failure mode that
would show up later as a mysterious drop in cache hits.

**Fixed** by ordering explicitly in a `AddListToolsFilter`, canonically by `Mcp.ToolNames.All`
(`TalentTools.OrderCanonically`). ADR-0002 corrected; a test asserts the resulting order is *not* the
alphabetical one, so removing the filter fails rather than passing by coincidence.

---

## 2. `UseStructuredContent` defaults to **false**

**Assumed.** A tool method returning a record would land in `structuredContent`.

**Observed.** It does not. With no attribute argument, `extract_skills` and `score_candidate_fit`
returned their payload as a JSON string inside a text content block and `structuredContent` was
`null` — so a client would have to re-parse text that the server had already serialized.

`McpServerToolAttribute.UseStructuredContent` is a `bool` defaulting to `false`
(`McpServerToolCreateOptions.UseStructuredContent` likewise). Setting it to `true` produced structured
content on every tool.

**Fixed** on all tools. `ToolHarness.StructuredOf` throws with this finding in the message if
`structuredContent` is ever absent again, so a new tool that forgets the flag fails loudly.

---

## 3. Nulls are omitted from the payload, not sent as `null`

**Observed.** On the last page, `search_jobs` returned **no `nextPageHandle` property at all** — not
`"nextPageHandle": null`. The serializer omits nulls.

Not a bug, and not worth fighting the SDK's serializer options over. But it means a caller has to infer
"no more pages" from a *missing* property, which for a model is guessing. The `hasMore` boolean on the
response — added before this was measured, for readability — turns out to be load-bearing rather than
redundant, and its comment now says why.

---

## 4. The reserved error-code range is partly the spec's, and 2.2.0 cannot set a custom code anyway

**Claimed** (`AGENTS.md`, constants table): *"`McpErrorCodes` — the reserved range `-32020…-32099` —
never raw numbers in code."*

**Observed.** Two problems.

`ModelContextProtocol.McpErrorCode` already defines values inside that band:

```
UrlElicitationRequired           = -32042
UnsupportedProtocolVersion       = -32022
MissingRequiredClientCapability  = -32021
HeaderMismatch                   = -32020
ResourceNotFound                 = -32002
```

So the band is not free for a server to allocate from; `-32020`, `-32021`, `-32022` and `-32042` are
spoken for.

And more decisively: **`McpException` in 2.2.0 exposes no error-code member.** Its public surface is
`.ctor()`, `.ctor(string)`, `.ctor(string, Exception)` and the inherited `Exception` members — there is
no `ErrorCode` property or code-taking constructor. A tool cannot emit a custom JSON-RPC error code.

**Which turns out to be the right shape anyway.** Tool failures belong in the tool result, not in the
transport: throwing `McpException` produces `isError: true` with the message preserved (prefixed
`"An error occurred invoking '<tool>': "`), which is what a model can actually see and recover from. A
JSON-RPC error code is for protocol-level failures — which is precisely what the SDK's own five codes
are.

**Fixed** by dropping the planned `McpErrorCodes` constant class rather than writing one nothing can
use, and correcting `AGENTS.md`. Tools throw `McpException` with an actionable message; tests assert
`IsError` and the message text.

---

## Confirmed as documented

Three things worked exactly as `AGENTS.md` says, and are pinned by tests so they stay that way:

- **`inputSchema` is generated by the SDK** from the method signature, and injected dependencies are
  *not* in it. The tool methods take `SearchJobsUseCase` as their first parameter and the schema
  contains only the caller's arguments — no `search`, no `cancellationToken`.
- **Enum parameters generate named strings**, not ordinals:
  `{"type":"string","enum":["Unspecified","OnSite","Hybrid","Remote"]}`. This is the domain that was
  going to reach for enums in tool inputs (`MCP9001`/SEP-1330 obsoleted the old enum-schema types), and
  the current generation path handles it with no converter configuration.
- **`[McpHeader("Region")]` is declared, not hidden.** The promoted parameter stays in the schema with
  `"x-mcp-header": "Region"`, so a client can discover that the value travels as a header. Worth
  pinning: if it were dropped from the schema, the routing knob would be undiscoverable.

And the cache fields land where they should: `tools/list` came back with `"ttlMs": 900000` and
`"cacheScope": "public"` from `CachePolicies.ToolsList` applied through the filter.

---

## Open, and deliberately left for the conformance work

**`server/discover` does not carry this server's cache policy.** Called over real HTTP it answered:

```json
{"supportedVersions":["2026-07-28"],"capabilities":{"logging":{},"tools":{}},
 "ttlMs":0,"cacheScope":"private","resultType":"complete", "_meta":{...}}
```

The fields are present, so the revision's requirement is met — but the values are the SDK's defaults,
not `CachePolicies.ServerDiscover` (one hour, public). `IMcpRequestFilterBuilder` has no
`AddServerDiscoverFilter`, so the `AddListToolsFilter` trick does not apply. Left alone rather than
worked around: whether a discover response *should* be cached for an hour is a real question — it
carries capabilities, and `ttlMs: 0` is a defensible answer — and it belongs with the conformance suite
that asserts the discover shape, not with the tool work.

**Two request headers the revision requires, which the raw-HTTP smoke test found the hard way.** A
`tools/call` over Streamable HTTP needs `MCP-Protocol-Version`, `Mcp-Method: tools/call` and
`Mcp-Name: <tool>`, *plus* `_meta/io.modelcontextprotocol/protocolVersion` and
`_meta/io.modelcontextprotocol/clientCapabilities` in the body. Omitting a header answers `-32020`
`HeaderMismatch`; omitting a `_meta` field answers `-32602`. The SDK client supplies all of it, so this
only bites hand-written requests — which is exactly what a conformance test is.

---

## Reproduction

The probe is not committed — the assertions it produced are, which is the durable form. To redo it:

1. `ToolHarness.StartAsync()` (`tests/Talent.Mcp.Tests/Tools/ToolHarness.cs`) gives a real client and
   server over two `System.IO.Pipelines.Pipe`s. The SDK ships no in-memory transport;
   `WithStreamServerTransport` plus `StreamClientTransport` crossed over two pipes is the substitute.
2. Print `tool.ProtocolTool.InputSchema.GetRawText()` for each tool, and
   `result.StructuredContent?.GetRawText()` for a few calls.
3. For the assembly surface, reflect over the DLLs in a scratch project's output rather than reading
   GitHub: `Directory.GetFiles(AppContext.BaseDirectory, "ModelContextProtocol*.dll")` →
   `Assembly.LoadFrom` → `GetExportedTypes()`. That reports the pinned 2.2.0, where the `main` branch
   would not.
