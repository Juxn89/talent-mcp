# AI Agent Guidelines — Talent.Mcp (A2 · Recruitment Domain MCP Server)

> **This file governs this repo only.** The portfolio's `AGENTS.md` does not apply here (it is
> Next.js/Vercel). Source of truth for scope and phases:
> [`docs/plans/a2-talent-mcp.md`](./docs/plans/a2-talent-mcp.md).
> When this file and the plan disagree, **the plan wins** and this file gets corrected.

## Overview

**Talent.Mcp** is an MCP server in C# for the recruitment domain, built against the
**2026-07-28 Model Context Protocol revision**. It exposes six typed tools (job search, job read,
skill extraction, candidate-fit scoring, candidate rejection, bulk shortlist scoring) over
**stateless** Streamable HTTP with OAuth 2.1 + PKCE S256 authorization.

A2 **owns the recruitment domain** (jobs, candidates, skills) in PostgreSQL with realistic seeds.
A1 (RAG matching) later reuses this schema and container by adding pgvector — so the domain model,
the scoring functions and the seeds are built to be consumed by another repo, not just by this one.

**No tool calls an LLM.** Scoring and skill normalization are deterministic pure functions. The
server runs with no API keys and at zero cost; the LLM belongs to A1.

---

## 🎯 Core Principles (from the projects catalog)

1. ✅ **Everything dockerized** — one `docker compose up` starts Postgres, Keycloak, the server and the observability stack, seeds included
2. ✅ **Open source, no paid licenses** — MCP SDK is Apache-2.0; every third-party dependency sits behind an abstraction
3. ✅ **Clean Architecture verified in CI** — Domain ← Application ← Infrastructure/Presentation; `Talent.Architecture.Tests` (ArchUnitNET) enforces the dependency rule and breaks the PR
4. ✅ **No magic strings/numbers** — protocol versions, meta keys, OAuth scopes, tool names and error codes live in typed constant classes; tunables in `IOptions<TalentOptions>`
5. ✅ **E2E without mocks** — real Postgres, real Keycloak, real MCP protocol over HTTP
6. ✅ **Verify before applying** — check the protocol spec, the package id and the version against the source, and record the date verified
7. ✅ **Own AGENTS.md + CLAUDE.md** — this file
8. ✅ **Published artifacts** — NuGet library `Talent.Mcp.Toolkit`, dotnet tool `Talent.Mcp.Server` (command `talent-mcp`), Docker image `ghcr.io/juxn89/talent-mcp`
9. ✅ **No public demo** — the proof is `docker compose up`, versioned benchmarks and green CI

---

## 🚫 Deprecated — DO NOT USE

The 2026-07-28 revision removed or deprecated the following. Using any of them is a **defect**, not a
style preference. Several emit SDK analyzer diagnostics which, with `TreatWarningsAsErrors=true`,
fail the build:

Diagnostic ids and messages below were read from `src/Common/Obsoletions.cs` at tag `v2.2.0` on
27 Aug 2026 — see [the changelog review](./docs/verification/sdk-2.0.0-to-2.2.0-review.md).

| Do not use | Use instead | Note |
|---|---|---|
| `Mcp-Session-Id` header, `initialize` handshake | **Server-minted signed handles** passed as ordinary tool arguments | Sessions are gone (SEP-2567, SEP-2575) |
| The session-only HTTP options: `EventStreamStore`, `SessionMigrationHandler`, `PerSessionExecutionContext`, `IdleTimeout`, `MaxIdleSessionCount` | Nothing — this server has no sessions | These five carry `MCP9006`. **Note:** the `Stateless` boolean is *not* obsolete and assigning `false` emits nothing; see the session-mode rule below |
| Server-initiated requests | **MRTR** — throw `InputRequiredException`, client retries with `inputResponses` | Under stateless HTTP there is no server→client request channel at all |
| **Roots** | — | Deprecated (`MCP9005`, SEP-2577) |
| **Sampling** | — | Deprecated (`MCP9005`, SEP-2577) |
| **MCP Logging API** | `stderr` in stdio hosts, **OpenTelemetry** in HTTP hosts | Deprecated (`MCP9005`, SEP-2577). Per-request level arrives in `_meta` |
| **HTTP+SSE transport** / `EnableLegacySse` | Streamable HTTP | `MCP9004` — legacy SSE has no built-in request backpressure |
| `EnumSchema`, `LegacyTitledEnumSchema` | The current schema generation path | `MCP9001` (SEP-1330). **Easy to hit here:** `SkillCategory` and the skill taxonomy mean tool input schemas carry enum-typed parameters |
| `WithToolsFromAssembly()` and the non-generic `WithTools(...)` | The generic **`WithTools<T>()`**, one call per tool type | `IL2026`. Trimming silently removes reflection-discovered tools — `tools/list` answers `-32601 Method not available` with no crash and no error log. Measured, see [ADR-0002](./docs/adr/0002-native-aot-and-explicit-tool-registration.md) |
| `RequestContextParams` parameterless ctor | The overload taking a parameters argument | `MCP9003` |
| **Dynamic Client Registration (DCR)** | **Client ID Metadata Documents** | ADR required in F3 |
| Old client OAuth callback delegate | `ClientOAuthOptions.AuthorizationCallbackHandler` | `MCP9007` — the old delegate cannot supply the RFC 9207 issuer |
| `OpenTelemetry.Exporter.Jaeger` | `OpenTelemetry.Exporter.OpenTelemetryProtocol` | Abandoned package (last release 1.5.1); Jaeger ingests OTLP directly |
| `InMemoryMcpTaskStore` in production paths | `PostgresMcpTaskStore` from `Talent.Mcp.Toolkit` | In-memory is fine in unit tests only; a restart must not lose in-flight tasks |

`MCP9002` does not exist in 2.2.0. All of the above fail the build, not warn, because of
`TreatWarningsAsErrors=true` — do not suppress them.

### Session mode — set it explicitly

Per [ADR-0001](./docs/adr/0001-streamable-http-session-mode.md). SDK 2.2.0 replaced the boolean with
a three-valued enum; `Stateless` is the default but is set explicitly anyway, so the single most
load-bearing fact about this server is readable at the call site:

```csharp
builder.Services.AddMcpServer()
    .WithHttpTransport(options => options.SessionMode = HttpServerSessionMode.Stateless)
    .WithTools<SearchJobsTool>()
    .WithTools<GetJobTool>();       // … one call per tool type — see the next rule
```

Do not use the `Stateless` boolean proxy, and do not select `Stateful` (it refuses 2026-07-28
requests with `-32022`) or `StatefulForInitializeClients` (a hybrid, deliberately declined — the ADR
records why and when to revisit).

### Tool registration — explicit, non-static, with pinned names

Per [ADR-0002](./docs/adr/0002-native-aot-and-explicit-tool-registration.md), which settled the
reflection-vs-trimming question by measurement. These three rules are permanent and hold whether or
not Native AOT ever ships — the ADR's scope limit explains why AOT itself is still open (EF Core is
not AOT-compatible, so the real gate is the data-access dependency, decided in F1/F2).

Three rules, all load-bearing:

1. **Register with `WithTools<T>()`, one call per tool type.** Never `WithToolsFromAssembly()`.
   Registration order is also how deterministic tool ordering is achieved.
2. **Tool types must be non-static.** `WithTools<T>()` rejects static classes
   (`CS0718: static types cannot be used as type arguments`), so the SDK docs' `public static class`
   shape does not work. Use `sealed class` with static tool methods.
3. **Always pin the wire name in the attribute.** The SDK derives it from the method name,
   snake-cased — `ExtractSkills()` publishes as `extract_skills` with no attribute argument. Relying
   on that means a C# rename silently changes the protocol contract.

```csharp
[McpServerToolType]
public sealed class ExtractSkillsTool                       // NOT static
{
    [McpServerTool(Name = Mcp.ToolNames.ExtractSkills),     // pinned, never derived
     Description("Normalizes free text against the skill taxonomy.")]
    public static ExtractSkillsResult Execute(string text) => /* … */;
}
```

`inputSchema` is **generated by the SDK** from the method signature — do not hand-write it. The
conformance suite asserts its presence, it does not author it.

### Not used, deliberately

- **`subscriptions/listen`** (SEP-2575, new in SDK 2.1.0: `McpServerHandlers.SubscriptionsListenHandler`, `WithSubscriptionsListenHandler(...)`). None of the six tools is a subscription; adding one expands the conformance surface for no extra demonstration. This is an omission by choice, not an oversight.

### 2.2.0 is a floor, not a preference

Do not downgrade the SDK pin to a 2.0.x or 2.1.x line. Two fixes in that window land on paths this
project uses: `McpHeaderEncoder.DecodeValue` threw on a degenerate base64 wrapper before 2.2.0 (and
`get_job` uses `[McpHeader("Region")]`), and HTTP status codes were not preserved across target
frameworks before 2.1.0 (and the E2E suite asserts `401` and scope denial).

Also breaking in this revision, and easy to get wrong:

- `inputSchema` is **mandatory** when deserializing tools — every tool must declare one explicitly.
- Non-object returns are emitted **raw** in `structuredContent` (`72`, not `{"result": 72}`).
- `tools/list`, `prompts/list`, `resources/list`, `resources/read` and `resources/templates/list`
  must return `ttlMs` and `cacheScope`.
- Tools **should** be returned in a deterministic order (improves LLM prompt cache hits) — and a
  conformance test asserts the order is stable across calls.
- PKCE **S256 is mandatory**: the SDK fails if the authorization server metadata does not declare
  `code_challenge_methods_supported: ["S256"]`.
- `iss` validation per RFC 9207; credentials indexed by issuer.
- Tasks moved to `ModelContextProtocol.Extensions.Tasks` and is **not wire-compatible** with v1.3–1.4.

---

## 📐 Architecture (Clean Architecture)

```
/src
  Talent.Domain/               ZERO framework deps (no EF Core, no MCP SDK, no ASP.NET).
                               Entities, enums (SkillCategory, ScoreReason), scoring + skill
                               normalization as pure functions.
  Talent.Application/          Use cases + ports: IJobRepository, ICandidateRepository,
                               IHandleCodec, IShortlistScorer. References Domain only.
  Talent.Infrastructure/       Adapters: EF Core/Npgsql, migrations, seeds, Keycloak client,
                               OTel exporters.
  Talent.Mcp.Server/           Presentation: ASP.NET Core Streamable HTTP (stateless) → GHCR image
  Talent.Mcp.Server.Stdio/     Presentation: stdio host → dotnet tool `talent-mcp`
  Talent.Mcp.Toolkit/          Domain-agnostic technical library (protocol primitives) → NuGet

/tests
  Talent.Architecture.Tests/   Dependency rule (ArchUnitNET). Written in F1, before there is code to violate it.
  Talent.Domain.Tests/         Pure scoring and normalization, table-driven, no Docker
  Talent.Infrastructure.Tests/ EF mapping and repositories against real Postgres (Testcontainers).
                               A SIXTH project the plan does not list, added deliberately: the plan has
                               no home for "does the mapping actually work against Postgres", and the
                               answer only exists in the real provider (text[], ILIKE, array
                               containment, owned-type flattening). Finding a broken mapping in the E2E
                               suite costs far more than a container start here.
  Talent.Mcp.Tests/            Tools over the in-memory transport
  Talent.Mcp.Conformance/      Protocol conformance per 2026-07-28 (discover, MRTR, cache fields, negotiation)
  Talent.Mcp.E2E/              Real compose: MCP client → HTTP → OAuth → Postgres

/bench
  Talent.Mcp.Bench/            BenchmarkDotNet + cold-start measurement

/deploy
  compose.yaml                 Postgres, Keycloak, server, OTel Collector, Jaeger, Prometheus, Grafana
  keycloak/realm.json          OAuth 2.1 client config (PKCE S256 declared)
  otel/collector.yaml
  grafana/dashboards/          Versioned as code

/docs
  plans/a2-talent-mcp.md       The plan — source of truth
  adr/                         Architecture Decision Records with trade-offs
  verification/                Dated verification records (Principle #6): what was checked, against
                               what source, on what date. Grep here before re-verifying something.
```

**The domain defends itself:** scoring and skill normalization are pure functions over
`Talent.Domain` with no repository or `DbContext` in the way. That is why their tests run in
milliseconds without Docker, and why A1 can reuse them as-is for its eval harness.

**Never** put EF Core (or any persistence) in `Talent.Domain`. An earlier revision of the plan did
exactly that; it violated the dependency rule and was corrected. `Talent.Architecture.Tests` exists
to stop it coming back.

---

## 🔧 Stack (package ids and versions verified against api.nuget.org on 27 Aug 2026)

| Component | Package id | Version | License |
|---|---|---|---|
| .NET SDK | — | 10.0.400 (`global.json`, `rollForward: latestFeature`) | MIT |
| C# | — | latest (`LangVersion`) | MIT |
| **MCP SDK** (stdio, hosting/DI, attribute discovery) | `ModelContextProtocol` | **2.2.0** | **Apache-2.0** |
| MCP low-level client/server | `ModelContextProtocol.Core` | 2.2.0 | Apache-2.0 |
| MCP Streamable HTTP | `ModelContextProtocol.AspNetCore` | 2.2.0 | Apache-2.0 |
| MCP Tasks extension | `ModelContextProtocol.Extensions.Tasks` | 2.2.0 | Apache-2.0 |
| EF Core | `Microsoft.EntityFrameworkCore` | 10.0.11 | MIT |
| Postgres provider | `Npgsql.EntityFrameworkCore.PostgreSQL` | 10.0.3 | PostgreSQL |
| JWT bearer auth | `Microsoft.AspNetCore.Authentication.JwtBearer` | 10.0.11 | MIT |
| OpenTelemetry | `OpenTelemetry` + `.Extensions.Hosting` + `.Exporter.OpenTelemetryProtocol` + `.Instrumentation.AspNetCore` | 1.18.0 | Apache-2.0 |
| Architecture rules | `TngTech.ArchUnitNET.xUnit` | **0.13.4** (never reached 4.x) | Apache-2.0 |
| Test framework | `xunit` | 2.9.3 | Apache-2.0 |
| Test runner | `xunit.runner.visualstudio` | 3.1.5 (last v2-compatible; 4.0.0 is v3-only) | Apache-2.0 |
| Test SDK | `Microsoft.NET.Test.Sdk` | 18.9.0 | MIT |
| Containers for tests | `Testcontainers` + `.PostgreSql` + `.Keycloak` | 4.14.0 | MIT |
| Benchmarks | `BenchmarkDotNet` | 0.15.8 | MIT |
| Keycloak (Docker) | — | 25.0+ | Apache-2.0 |

Versions are pinned centrally in **`Directory.Packages.props`** (Central Package Management) via
`<PackageVersion Include="…" Version="…" />`. Individual `.csproj` files reference packages
**without** a `Version` attribute:

```xml
<PackageReference Include="ModelContextProtocol.AspNetCore" />
```

**Do not move version pinning into `Directory.Build.props` as `PackageReference Update=`.** That
file is imported *before* the project body, so the project's own `Include=` items do not exist yet
and there is nothing to update — restore fails with `NU1015`. An earlier revision of this repo had
exactly that bug. `Directory.Build.props` holds build **properties** only.

**Two package ids that do not exist** and were wrong in an earlier revision of this file:
`ModelContextProtocol.Server` and `ModelContextProtocol.Server.AspNetCore`. Use the table above.

**xUnit stays on v2** because `TngTech.ArchUnitNET.xUnit` 0.13.4 depends on `xunit.assert` 2.4.1.
Moving to `xunit.v3` (4.0.0) breaks the ArchUnitNET assertion adapter — revisit only if ArchUnitNET
ships a v3 adapter.

**Avoided:** MassTransit (commercial since Jan 2026), AutoMapper (open-core), Wolverine (commercial roadmap).

---

## 📋 Constants (Principle #4: no loose literals)

Six constant groups, fixed from the first commit. An MCP server is fertile ground for magic strings.

| Group | What it encloses |
|---|---|
| `ToolNames` | `search_jobs`, `get_job`, `extract_skills`, `score_candidate_fit`, `reject_candidate`, `bulk_score_shortlist` — used by the server, the tests and the demo client |
| `McpMetaKeys` | `io.modelcontextprotocol/protocolVersion`, `clientCapabilities`, `logLevel`, `traceparent`, `tracestate`, `baggage` |
| `OAuthScopes` | `talent.jobs.read`, `talent.candidates.read`, `talent.candidates.write`, `talent.candidates.reject` |
| `ProtocolVersions` | the supported revision (`2026-07-28`) and the downgrade-interop ones (`2025-11-25`, …) |
| `McpErrorCodes` | the reserved range `-32020…-32099` — never raw numbers in code |
| `TalentOptions` | cache TTLs, handle TTLs, page size, retries, timeouts → bound via `IOptions<T>` |

### MCP protocol identity

```csharp
// src/Talent.Mcp.Server/Constants/Mcp.cs
namespace Talent.Mcp.Server.Constants;

public static class Mcp
{
    public const string ServerName = "talent-mcp";

    public static class ProtocolVersions
    {
        /// <summary>The revision this server implements.</summary>
        public const string Supported = "2026-07-28";

        /// <summary>Revisions accepted through downgrade negotiation.</summary>
        public static readonly string[] Interop = ["2025-11-25"];
    }

    public static class ToolNames
    {
        public const string SearchJobs = "search_jobs";
        public const string GetJob = "get_job";
        public const string ExtractSkills = "extract_skills";
        public const string ScoreCandidateFit = "score_candidate_fit";
        public const string RejectCandidate = "reject_candidate";
        public const string BulkScoreShortlist = "bulk_score_shortlist";
    }
}
```

`ServerVersion` is **not** a constant — read it from the assembly informational version so it cannot
drift from the `.csproj`.

### OAuth 2.1 + PKCE

```csharp
// src/Talent.Mcp.Server/Constants/OAuth.cs
public static class OAuth
{
    public const string ClientId = "talent-mcp-server";
    public const string CodeChallengeMethod = "S256";  // mandatory per 2026-07-28

    public static class Scopes
    {
        public const string JobsRead = "talent.jobs.read";
        public const string CandidatesRead = "talent.candidates.read";
        public const string CandidatesWrite = "talent.candidates.write";
        public const string CandidatesReject = "talent.candidates.reject";
    }
}
```

The **issuer is configuration, not a constant** (`IOptions`), because it differs between compose,
Testcontainers and production. Dev default: `http://localhost:8080/realms/talent`.

Scopes are enforced **per tool** — read vs write vs destructive — not one blanket scope for the
server. `openid profile email` are the client's OIDC scopes and grant no tool access.

### Domain invariants

```csharp
// src/Talent.Domain/Constants/JobSchema.cs
public static class JobSchema
{
    public const int MaxTitleLength = 255;
    public const int MinSalaryRange = 0;
    public const int MaxExperienceYears = 50;
}
```

**Enforcement:** Roslyn analyzers via `.editorconfig`, plus `TreatWarningsAsErrors=true` in
`Directory.Build.props` — a naming or analyzer violation fails the build, it does not warn.

---

## 🛠 Tool surface

Each tool exists to demonstrate a specific capability of the revision. Do not add tools that
demonstrate nothing, and do not drop one because it is inconvenient.

| Tool | Required scope | What it demonstrates |
|---|---|---|
| `search_jobs` | `talent.jobs.read` | Pagination with a **signed handle** instead of a session — the pattern the spec now requires |
| `get_job` | `talent.jobs.read` | Cacheable result with `ttlMs`/`cacheScope`, plus region routing promoted to a header via `[McpHeader("Region")]` |
| `extract_skills` | `talent.jobs.read` | Normalization against the taxonomy, **deterministic** (no LLM): testable and free |
| `score_candidate_fit` | `talent.candidates.read` | Explainable score with a per-component breakdown (skill overlap, seniority distance, location). Deterministic → basis for A1's eval harness |
| `reject_candidate` | `talent.candidates.reject` | Destructive operation requiring confirmation via **MRTR** (`InputRequiredException` + `requestState`), including the degraded path when `server.IsMrtrSupported` is `false` |
| `bulk_score_shortlist` | `talent.candidates.write` | Long-running via the **Tasks extension** with the Postgres store — survives a container restart |

The scope-per-tool mapping above is the working assumption; it is **ratified in F3** when the
Keycloak realm is written, and the E2E test asserts that a token missing the required scope is denied.

### `Talent.Mcp.Toolkit` — why publishing a library is justified

Not a wrapper around the SDK. These are the pieces the SDK does not ship and the new revision makes
necessary:

- `PostgresMcpTaskStore : IMcpTaskStore` — the SDK only ships `InMemoryMcpTaskStore`; without persistence a restart loses in-flight tasks. **The hard part is not persistence, it is the `InputResponseReceived` event**: the SDK subscribes to it in the process running the task and blocks, while `ResolveInputRequestsAsync` is called by whichever process handles the client's follow-up request — a different one, since this server is stateless. Solved with Postgres `LISTEN`/`NOTIFY` plus a sweep fallback, per [ADR-0003](./docs/adr/0003-cross-node-task-input-responses.md). Uses raw Npgsql, not EF Core, so the NuGet package does not impose EF on consumers.
- `HandleCodec` — minted, **signed** and TTL-bounded opaque handles replacing sessions (pagination cursors, in-progress shortlists). Signed so a client cannot forge one.
- `ttlMs` / `cacheScope` policies per primitive, and deterministic tool ordering.
- OTel context extraction from `_meta` (`traceparent`/`tracestate`/`baggage`) into an `Activity`.

It must stay **domain-agnostic** — no recruitment concept may leak into it.

---

## 🧪 Test Pyramid

Five levels, each with a distinct job. **All five run in CI and all five block the merge** — they
gate, they do not merely report.

1. **Architecture** (`Talent.Architecture.Tests`, ArchUnitNET — no Docker)
   - `Talent.Domain` references neither EF Core, nor the MCP SDK, nor ASP.NET
   - `Talent.Application` references `Domain` only
   - Presentation does not reach `Infrastructure` without going through a port
   - Written in **F1**, before there is code to violate it. A dependency rule added after the code exists is negotiated, not enforced.

2. **Domain** (`Talent.Domain.Tests` — pure, no Docker, milliseconds)
   - Scoring and skill normalization, table-driven cases
   - Invariant validation

2b. **Infrastructure** (`Talent.Infrastructure.Tests` — Testcontainers, real Postgres)
   - EF mapping round-trips: owned types, `text[]` columns, enums stored as text rather than ordinals
   - Repository filters, and that page ordering is a **total** order — otherwise handle pagination can skip or repeat a row
   - Seeding is idempotent and converges after a partial insert
   - The migrations themselves, applied by the fixture: one that does not apply cleanly fails the whole class

3. **Tools** (`Talent.Mcp.Tests` — in-memory transport)
   - Input/output contract, `inputSchema` present on every tool
   - Actionable errors; foreign or expired handles are rejected
   - Use cases against fake ports: happy path + degradation (repo down → fallback)

4. **Protocol conformance** (`Talent.Mcp.Conformance` — Testcontainers) — the suite with the most signal
   - `server/discover` returns versions, capabilities and identity. Assert the **response shape**, not that a client fails without it: as of SDK 2.1.0 a client treats HTTP 404 from the probe as a down-level server and falls back to `initialize`
   - Full MRTR cycle: first `input_required` → retry with `inputResponses`
   - **`tools/list` returns all six tools, asserted by name.** Not optional: this is the test that catches trimming silently emptying the tool set, which is a `-32601` with no crash and no error log ([ADR-0002](./docs/adr/0002-native-aot-and-explicit-tool-registration.md)). A test that only asserts the server starts is worthless here
   - `ttlMs`/`cacheScope` present on every list response
   - Tool order stable across calls
   - A 2025-11-25 client is **served statelessly** — no `Mcp-Session-Id` minted or echoed, GET/DELETE return `405`. The plan called this "downgrade negotiation"; under `SessionMode.Stateless` there is no downgrade, and asserting `-32022` would assert `Stateful` behaviour this server deliberately does not have ([ADR-0001](./docs/adr/0001-streamable-http-session-mode.md))
   - Keycloak's metadata **contains** `S256` in `code_challenge_methods_supported`, and separately that `talent-mcp-client` rejects a `plain` challenge. Do not assert the list equals `["S256"]`: Keycloak advertises `["plain","S256"]` realm-wide and does not allow removing `plain`, so enforcement is per-client. See [`deploy/keycloak/README.md`](./deploy/keycloak/README.md)

5. **E2E** (`Talent.Mcp.E2E` — real compose, **no mocks**)
   - A real MCP client against Postgres + Keycloak + server, through OAuth
   - Handle pagination across calls, full MRTR, scope denial, and a task that survives a container restart
   - Trace propagation in headers

All five runnable locally with no cluster and no remote service.

---

## 🚀 Verification Gates (Principle #6)

**Before deciding on a version, package id, API or capability:**
- Cross-check against the official source (NuGet registration API, GitHub releases, and for API
  claims **the source at the pinned tag** — release-note prose is not enough)
- Record the date verified next to the claim, and the record itself in `docs/verification/`
- What this has already caught: `ModelContextProtocol.Server` and `ArchUnitNET 4.11` do not exist;
  `PackageReference Update=` in `Directory.Build.props` is inert (`NU1015`); `Stateless = false`
  does **not** emit `MCP9006`; `MCP9001` applies to enum schemas, which this domain will reach for.
  All three of those came from the plan or from an earlier revision of this file.

**Before shipping code:**
- `dotnet build` — no warnings (warnings are errors) ✅
- `dotnet test` — all five levels pass ✅
- `docker compose -f deploy/compose.yaml up -d` + E2E against the real stack ✅
- `dotnet pack` — NuGet package valid ✅
- GitHub Actions runs the above on every PR ✅

Nothing ships without all gates green.

---

## 📦 Artifacts & Publishing

| Artifact | Id | Kind |
|---|---|---|
| NuGet library | `Talent.Mcp.Toolkit` | Reusable protocol primitives — **a library, not a tool** |
| dotnet tool | `Talent.Mcp.Server`, command `talent-mcp` | stdio host, for Claude Code / Claude Desktop |
| Docker image | `ghcr.io/juxn89/talent-mcp` | Streamable HTTP host, multi-stage, non-root, healthcheck |

**NuGet ids are the only irreversible thing in the project** — reconfirm availability immediately
before F5, which is when publishing happens.

Secrets as GitHub Actions secrets: `NUGET_API_KEY`, `GHCR_PAT`. CI publishes on tag `v*`:

```yaml
- run: dotnet pack -c Release
- run: dotnet nuget push **/*.nupkg -k ${{ secrets.NUGET_API_KEY }} -s https://api.nuget.org/v3/index.json
```

Installing the tool locally:

```bash
dotnet tool install --global Talent.Mcp.Server   # provides the `talent-mcp` command
talent-mcp                                       # stdio host
```

---

## 📝 Naming Conventions

- **Classes:** PascalCase (`SearchJobsHandler`)
- **Methods:** PascalCase, async suffixed `Async` (`ExecuteAsync`)
- **Constants:** **PascalCase** (`MaxTitleLength`, `SearchJobs`) — enforced at `error` severity by
  `.editorconfig` (`constant_fields_should_be_pascal_case`). `UPPER_SNAKE_CASE` **fails the build**.
- **Enums:** PascalCase members (`SkillCategory.Backend`)
- **Interfaces:** `I` prefix (`IJobRepository`)
- **Files:** PascalCase matching the type name
- **Namespaces:** `Talent.{Domain,Application,Infrastructure,Mcp.*}`, file-scoped
- **MCP tool names on the wire:** `snake_case` (`search_jobs`) — always via `Mcp.ToolNames`, never a literal

---

## 🔒 Security

- **OAuth 2.1** with PKCE S256 — mandatory, not negotiable
- **Scopes per tool**, distinguishing read / write / destructive
- **`iss` validation** per RFC 9207; credentials indexed by issuer
- **Client ID Metadata Documents**, not DCR (deprecated)
- **Signed handles** — a client must not be able to forge or extend one; expired handles are rejected
- **HTTPS only** in production (`ASPNETCORE_HTTPS_PORT`)
- **No secrets in code** — environment / `IConfiguration` only
- **CORS** restricted to localhost in dev, explicit origins in prod
- **Input validation** at both the Presentation layer (HTTP shape) and the Domain layer (business rules)
- **Rate limiting** per client (IP + OAuth `sub`) — in-memory for now

---

## 📊 Observability

OpenTelemetry only — no vendor lock-in, and **not** the deprecated MCP Logging API.

- **Traces:** OTLP → Collector → Jaeger (`localhost:16686`)
- **Logs:** `stderr` in the stdio host; OTLP → Collector → Loki in the HTTP host
- **Metrics:** OTLP → Collector → Prometheus (`localhost:9090`)

Every tool execution is a span carrying `tool.name`, `tool.input`, `tool.output_tokens`,
`db.query_time`, `cache.hit`, `oauth.token_refresh`. Client context is extracted from `_meta`
(`traceparent`/`tracestate`/`baggage`) so a single trace spans client → server → Postgres.

Grafana dashboards (`localhost:3000`) versioned as code in `deploy/grafana/dashboards/`:
latency per tool, error rate, in-flight tasks.

---

## 🔀 Build, Test, Deploy

```bash
dotnet restore
dotnet build                                        # compile + analyzers, warnings are errors
dotnet test                                         # five levels
docker compose -f deploy/compose.yaml up -d         # local stack, seeds included

git tag v1.0.0 && git push origin v1.0.0            # → CI publishes NuGet + GHCR
```

---

## ⚠️ Common Pitfalls

1. ❌ **Reintroducing EF Core into `Talent.Domain`** — the plan already made this mistake once. The domain knows nobody.
2. ❌ **Magic strings for tool names** — use `Mcp.ToolNames.SearchJobs`, never `"search_jobs"`.
3. ❌ **`UPPER_SNAKE_CASE` constants** — `.editorconfig` fails the build. PascalCase.
4. ❌ **A stateful server** — 2026-07-28 is stateless; carry state in signed handles passed as tool arguments.
5. ❌ **Forgetting `inputSchema`** — mandatory in 2026-07-28; the client needs it to build UI, and deserialization fails without it.
6. ❌ **Wrapping non-object returns** — they go raw into `structuredContent` (`72`, not `{"result": 72}`).
7. ❌ **Skipping OAuth in local tests** — real Keycloak in Testcontainers is how the actual auth flow gets exercised.
8. ❌ **Omitting `ttlMs`/`cacheScope`** on any `*/list` or `resources/read` response.
9. ❌ **Non-deterministic tool order** — hurts LLM prompt cache hits and a conformance test asserts it.
10. ❌ **`ConfigureAwait(false)` omitted in library code** (`Talent.Mcp.Toolkit`, stdio host) — deadlocks in console/tool contexts.
11. ❌ **Logging to stdout in a stdio host** — `Host.CreateApplicationBuilder` installs a console logger on **stdout**, which corrupts the JSON-RPC stream; the first "response" you read is a log line. Do `builder.Logging.ClearProviders(); builder.Logging.AddConsole(o => o.LogToStandardErrorThreshold = LogLevel.Trace);`. This is why stdio hosts log to stderr — the transport breaks otherwise, it is not a preference.
12. ❌ **Publishing NuGet without SemVer** — breaks A1, which depends on this domain.
13. ❌ **Pinning package versions in a `.csproj`, or as `Update=` in `Directory.Build.props`** — versions go in `Directory.Packages.props`. See the stack section.
14. ❌ **Sharing an owned value-object instance between entities** — a `static readonly Location Madrid` referenced by four candidates gives EF Core one owned change-tracking entry with four owners, and `SaveChanges` then writes `NULL` for its columns: `23502: null value in column "city"`, an error that names neither owned types nor sharing. It hides well, because saving each entity alone succeeds and only a batch fails. Expose value-object constants as expression-bodied members (`static Location Madrid => new(...)`), never as cached fields. Records make it worse, not better: value equality means two separately-built instances also compare equal.
15. ❌ **A test-project condition in the root `Directory.Build.props`** — `IsTestProject` is set by `Microsoft.NET.Test.Sdk` from the project body, which is evaluated after the props import, so the condition never fires. Test-only properties go in `tests/Directory.Build.props`, which chains the root file.

---

## 📚 References

**In this repo:**
- [The plan](./docs/plans/a2-talent-mcp.md) — source of truth for scope and phases
- [ADR-0001 · Streamable HTTP session mode](./docs/adr/0001-streamable-http-session-mode.md)
- [ADR-0002 · Native AOT and explicit tool registration](./docs/adr/0002-native-aot-and-explicit-tool-registration.md)
- [ADR-0003 · Cross-node task input responses](./docs/adr/0003-cross-node-task-input-responses.md)
- [Verification · SDK changelog 2.0.0 → 2.2.0](./docs/verification/sdk-2.0.0-to-2.2.0-review.md)

**External:**
- [The 2026-07-28 Specification](https://blog.modelcontextprotocol.io/posts/2026-07-28/)
- [Key Changes — 2026-07-28](https://modelcontextprotocol.io/specification/2026-07-28/changelog)
- [modelcontextprotocol/csharp-sdk](https://github.com/modelcontextprotocol/csharp-sdk)
- [NuGet: ModelContextProtocol 2.2.0](https://www.nuget.org/packages/ModelContextProtocol)
- [SDK docs: Multi Round-Trip Requests (MRTR)](https://csharp.sdk.modelcontextprotocol.io/v2/concepts/mrtr/mrtr.html)
- [Announcing v2.0 of the official MCP C# SDK — .NET Blog](https://devblogs.microsoft.com/dotnet/announcing-v20-of-the-official-mcp-csharp-sdk/)
- [OAuth 2.1 PKCE (RFC 7636)](https://www.rfc-editor.org/rfc/rfc7636) · [RFC 9207 `iss`](https://www.rfc-editor.org/rfc/rfc9207)
- [OpenTelemetry .NET](https://opentelemetry.io/docs/languages/net/)
- [ArchUnitNET](https://archunitnet.readthedocs.io/)
