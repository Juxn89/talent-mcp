# Verification · What SDK 2.2.0 actually does with a tool surface

| | |
|---|---|
| **Date** | 1 Sep 2026 |
| **Phase** | F2 (MCP tools) |
| **Method** | A real MCP server and a real MCP client over paired in-memory pipes, plus a reflection dump of the pinned 2.2.0 assemblies from the local NuGet cache |
| **Why** | Eight assumptions the plan, `AGENTS.md` or ADR-0002 stated turned out to be wrong. Each is corrected at its source and pinned by a test |
| **Updated** | 2 Sep 2026 — findings 5 to 8, from building `reject_candidate` and its MRTR exchange |

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

## 5. `IsMrtrSupported` is **not** a sufficient guard for asking the user

**Claimed** (the plan, and `AGENTS.md`'s tool table): `reject_candidate` must handle *"the degraded path
when `server.IsMrtrSupported` is `false`"*.

**Observed.** That condition is not the one that matters. A client declaring protocol `2025-11-25` has
`IsMrtrSupported` **true**, and raising an elicitation for it fails inside the SDK with:

```
System.InvalidOperationException: Client does not support elicitation requests.
```

which reaches the caller as `isError: true` carrying `"An error occurred invoking 'reject_candidate'."`
— the message stripped, no indication of what to do. Guarding on `IsMrtrSupported` alone therefore
produces the worst of both worlds: the degraded path exists in the code and never runs, and the client
that needed it gets an opaque failure.

The two properties answer different questions:

| Property | Question it answers |
|---|---|
| `McpServer.IsMrtrSupported` | Is the MRTR mechanism available on this connection? |
| `McpServer.ClientCapabilities?.Elicitation` | Is there anyone at the other end who can be asked? |

A confirmation needs **both**. `RejectCandidateTool.CanAskTheUser` checks both, and a test drives a
`2025-11-25` client to prove the degraded path is reached rather than merely present.

---

## 6. The SDK client drives the MRTR round-trip itself

Not an error, but it changes how MRTR can be tested and is worth knowing before writing a conformance
assertion.

Register `McpClientHandlers.ElicitationHandler` and the client does the whole exchange: it intercepts
the `input_required` result, calls the handler, and re-sends `tools/call` with `inputResponses` and the
server's `requestState`. `CallToolAsync` returns only the final result, with
`"resultType": "complete"`. So:

- A tool test **cannot observe the first leg** through the SDK client, and cannot make the second leg
  disagree with the first. Tests that need to — for instance, proving a client cannot swap the rejection
  reason between confirming and writing — build the retry by hand
  (`ToolHarness.CallRawAsync`), minting the `requestState` with the same codec the server uses.
- A conformance test that wants to see the raw `input_required` result has to go around the SDK client,
  over HTTP.

One asymmetry the server cannot do anything about: a **2026-07-28 client with no elicitation handler
still declares the elicitation capability**, so `CanAskTheUser` is true, the server asks, and the client
then throws locally with *"Server sent an elicitation input request, but no ElicitationHandler is
registered."* From the server's side that client is indistinguishable from one that can ask.

---

## 7. Only `McpException` keeps its message; everything else is stripped

**Observed.** A thrown `McpException` reaches the client as `isError: true` with the full text, prefixed
`"An error occurred invoking '<tool>': "`. Any other exception — including the SDK's own
argument-binding failure for a missing required parameter — becomes `"An error occurred invoking
'<tool>'."` and nothing more.

**Why it is worth writing down.** It made a test pass for the wrong reason. A hand-built MRTR retry that
omitted the required `candidateId` argument was rejected by the SDK binder *before the tool body ran*,
so a test asserting only `Assert.True(result.IsError)` on an expired confirmation went green without
ever reaching the expiry check. Both hand-built retries now send the argument and assert on the message
text. The general rule: **for a negative test, assert the message, not just the flag** — `isError` alone
cannot tell "refused for the reason under test" from "never got that far".

---

## 8. `McpServer.ClientCapabilities` is **null on every request** under stateless HTTP

The most consequential finding of the increment, and one only a live host could produce.

**Observed.** With `reject_candidate` gated on `ClientCapabilities?.Elicitation` (the fix from finding
5), the in-memory stream transport behaved perfectly and the **HTTP host took the degraded path for
every single call** — including calls whose body declared
`_meta/io.modelcontextprotocol/clientCapabilities: {"elicitation": {}}`. A temporary diagnostic in the
error message said it plainly:

```
DIAG mrtr=True caps=null
```

`IsMrtrSupported` true, `ClientCapabilities` null, capabilities declared in the body and ignored.

**Why.** `ClientCapabilities` is the pre-2026 session-level notion, populated by the `initialize`
handshake — which SEP-2575 removed. Under `SessionMode.Stateless` there is no session to hold it, so
the property is never set, and the client's self-description arrives as **per-request metadata**
instead. `AGENTS.md` already lists `McpMetaKeys.clientCapabilities` among the six constant groups for
exactly this reason; what was missing was code that read it.

**Why it mattered so much.** The failure was invisible from the tool tests: the stream transport does
populate the property, so 135 tests passed while the shipped HTTP host had MRTR permanently disabled.
That is the divergence ADR-0004 exists to prevent, arriving through the SDK rather than through the
tool surface — a reminder that "both hosts share the tool code" does not mean both hosts behave
identically.

**Fixed** by `Talent.Mcp.Toolkit.McpClientCapabilityReader`, which consults the session property when
the transport populates it and the request's own `_meta` when it does not. It lives in the toolkit
because it is pure protocol plumbing with no recruitment concept in it — the same reason
`McpTraceContext` is there.

**Verified end to end over HTTP**, against real Postgres:

| Request | Result |
|---|---|
| No capabilities declared | `isError`, message naming `confirmed: true` and the reason requirement |
| No capabilities + `confirmed: true` | written, `confirmation: "ClientAsserted"` |
| `{"elicitation":{}}` declared | `resultType: "input_required"` with `requestState` and an `inputRequests.confirm_rejection` elicitation |
| Retry with `inputResponses` **and a different `reason` argument** | `resultType: "complete"`, and the row in Postgres carries the **original** reason |

That last row is the invariant the whole design rests on: what the user approved is what got written.

**Still a gap, and named as one.** The `_meta` branch is covered by unit tests over the reader, but no
automated test exercises it through the HTTP host — the run above was manual. The conformance suite in
the next increment is where that belongs, because it is the suite that already speaks raw HTTP.

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
