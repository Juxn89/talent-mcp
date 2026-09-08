# Talent.Mcp

[![CI](https://github.com/Juxn89/talent-mcp/actions/workflows/ci.yml/badge.svg)](https://github.com/Juxn89/talent-mcp/actions/workflows/ci.yml)
[![NuGet — Talent.Mcp.Toolkit](https://img.shields.io/nuget/v/Talent.Mcp.Toolkit?label=Talent.Mcp.Toolkit)](https://www.nuget.org/packages/Talent.Mcp.Toolkit)
[![NuGet — talent-mcp](https://img.shields.io/nuget/v/Talent.Mcp.Server?label=talent-mcp%20tool)](https://www.nuget.org/packages/Talent.Mcp.Server)
[![License](https://img.shields.io/badge/license-Apache--2.0-blue.svg)](./LICENSE)

An MCP server in C# for the recruitment domain, built against the **2026-07-28 Model Context
Protocol revision** — the one that removed sessions. Six typed tools over stateless Streamable HTTP
with OAuth 2.1 + PKCE, and the same six over stdio as a `dotnet tool`.

**No tool calls an LLM.** Scoring and skill normalization are deterministic pure functions, so the
server runs with no API keys, at zero cost, and its results are reproducible enough to be a test
oracle.

```bash
git clone https://github.com/Juxn89/talent-mcp.git && cd talent-mcp
docker compose -f deploy/compose.yaml up -d --wait

# The hosts do not migrate the domain schema at startup — only the task store's own tables.
dotnet ef database update --project src/Talent.Infrastructure --startup-project src/Talent.Mcp.Server

dotnet run --project src/Talent.Mcp.Server
```

That brings up Postgres, Keycloak with the realm imported, and the full OpenTelemetry stack:

| | |
|---|---|
| Keycloak | <http://localhost:8080> — `admin` / `admin` |
| Realm discovery | <http://localhost:8080/realms/talent/.well-known/openid-configuration> |
| Grafana | <http://localhost:3000> — dashboards provisioned from `deploy/grafana/` |
| Jaeger | <http://localhost:16686> |
| Prometheus | <http://localhost:9090> |
| Loki | <http://localhost:3100> |
| OTLP intake | `localhost:4317` (gRPC), `localhost:4318` (HTTP) |
| Postgres | `localhost:5432` — `talent` / `talent`, databases `talent` and `keycloak` |
| Keycloak realm user | `recruiter` / `recruiter` |

Every credential above is a dev-only default and every one is overridable from the environment.
Image tags are pinned to exact patch versions, because `latest` makes a green CI run
unreproducible three weeks later.

> **The domain tables start empty.** `deploy/postgres/init/` only creates Keycloak's database, and
> `TalentSeeder` — which does migrate and seed realistic jobs and candidates — is currently wired
> into the test fixtures only (`Talent.Mcp.E2E/RealServerFixture`, `Talent.Infrastructure.Tests`).
> So `search_jobs` against a freshly composed stack returns nothing until you seed it yourself.
> The plan calls for `docker compose up` to include seeds; closing that gap needs a seeding entry
> point on the hosts and is tracked as follow-up work rather than quietly implied here.

```bash
docker compose -f deploy/compose.yaml down     # add -v to drop the volume and force a realm re-import
```

The MCP server is deliberately **not** a compose service: the conformance and E2E suites build it
in-process so they can assert against the same composition the tests exercise.

---

## The tools

Each one exists to demonstrate a specific capability of the revision. None is decorative.

| Tool | Scope | What it demonstrates |
|---|---|---|
| `search_jobs` | `talent.jobs.read` | Pagination by **signed handle** — the pattern that replaces sessions |
| `get_job` | `talent.jobs.read` | Cacheable result with `ttlMs`/`cacheScope`, plus region routing promoted to a header via `[McpHeader("Region")]` |
| `extract_skills` | `talent.jobs.read` | Taxonomy normalization, deterministic and LLM-free |
| `score_candidate_fit` | `talent.candidates.read` | Explainable score with a per-component breakdown |
| `reject_candidate` | `talent.candidates.reject` | A destructive operation gated on **MRTR** confirmation, including the degraded path |
| `bulk_score_shortlist` | `talent.candidates.write` | Long-running work on the **Tasks extension**, backed by Postgres so it survives a restart |

Scopes are enforced **per tool** — read, write and destructive are not interchangeable — and the
E2E suite asserts that a token missing the required scope is denied.

---

## The flow, end to end

What makes this revision different, in the order you hit it. Request bodies below are the real wire
shapes; run the commands against your own stack for live values.

**1. Get a token.** Every tool call needs one, and the scope has to match the tool.

```bash
TOKEN=$(curl -s -X POST http://localhost:8080/realms/talent/protocol/openid-connect/token \
  -d grant_type=password -d client_id=talent-mcp-client \
  -d username=recruiter -d password=recruiter \
  -d scope='talent.jobs.read talent.candidates.read' | jq -r .access_token)
```

**2. Search, and page with a handle — not a session.** There is no `Mcp-Session-Id` and no
`initialize`. State between calls travels as a signed, TTL-bounded handle passed back as an ordinary
tool argument.

```bash
curl -s -X POST http://localhost:5000/mcp \
  -H "Authorization: Bearer $TOKEN" \
  -H 'MCP-Protocol-Version: 2026-07-28' \
  -H 'Mcp-Method: tools/call' -H 'Mcp-Name: search_jobs' \
  -H 'Content-Type: application/json' \
  -d '{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{
        "name":"search_jobs",
        "arguments":{"query":"dotnet","pageSize":2},
        "_meta":{"io.modelcontextprotocol/protocolVersion":"2026-07-28",
                 "io.modelcontextprotocol/clientCapabilities":{}}}}'
```

The result carries `nextPageHandle`, which you pass straight back as an argument to get page two:

```jsonc
{ "jobs": [ /* … */ ], "totalMatches": 7, "hasMore": true,
  "nextPageHandle": "AAAAAGqQKRi4yPyKLkvv9…" }
```

> `hasMore` is an explicit field on purpose. The serializer omits nulls, so on the last page
> `nextPageHandle` is **absent**, not `null` — a client that treats "no more data" as the absence of
> another field is reading a serialization detail as a protocol signal.

**3. Reject a candidate — and get told no.** The first call does not delete anything. It raises
`InputRequiredException`, which reaches the client as an `input_required` result carrying a
`requestState` handle:

```bash
# … "name":"reject_candidate", "arguments":{"candidateId":"…","reason":"Withdrew"}
```

The client re-sends the *original* request with `inputResponses` filled in, and only then does the
rejection happen. That round trip is MRTR, and it is the only mechanism the revision leaves for a
server to ask a question — server-initiated requests are gone.

If the client cannot elicit, the tool takes a degraded path instead of failing. The guard is
`IsMrtrSupported && ClientCapabilities?.Elicitation is not null`: the first asks whether the
mechanism exists, the second whether anyone is there to answer.

**4. Watch it in Jaeger.** Client context arrives in `_meta` as `traceparent`/`tracestate`/`baggage`,
so one trace spans client → server → Postgres. Open <http://localhost:16686> and pick
`talent-mcp-server`.

---

## Architecture

Clean Architecture, with the dependency rule verified in CI rather than agreed to in a document.

```
Presentation ──┐
               ├──▶ Application ──▶ Domain
Infrastructure ┘

Dependencies point inward only. Domain references nothing.
```

| Project | Role |
|---|---|
| `Talent.Domain` | Entities and the pure scoring and normalization functions. Zero framework dependencies |
| `Talent.Application` | Use cases and ports (`IJobRepository`, `ICandidateRepository`, `IHandleCodec`) |
| `Talent.Infrastructure` | Adapters: EF Core/Npgsql, migrations, seeds, Keycloak, OTel exporters |
| `Talent.Mcp.Tools` | The six tool types and their wire contracts, shared verbatim by both hosts |
| `Talent.Mcp.Server` | ASP.NET Core Streamable HTTP host → the GHCR image |
| `Talent.Mcp.Server.Stdio` | stdio host → the `talent-mcp` dotnet tool |
| `Talent.Mcp.Toolkit` | Domain-agnostic protocol primitives → NuGet |

`Talent.Mcp.Tools` references neither `Talent.Infrastructure` nor `ModelContextProtocol.AspNetCore`,
which is what lets the stdio host serve the identical surface without dragging the web stack into a
process where cold start is the whole point. `Talent.Architecture.Tests` fails the build if that
changes.

---

## Testing

Five levels with distinct jobs. **All five gate the merge** — they do not merely report.

```bash
# No Docker needed
dotnet test tests/Talent.Architecture.Tests    # the dependency rule
dotnet test tests/Talent.Domain.Tests          # pure scoring and normalization
dotnet test tests/Talent.Mcp.Tests             # tools over the in-memory transport

# Needs the stack
docker compose -f deploy/compose.yaml up -d --wait
dotnet test tests/Talent.Infrastructure.Tests  # EF mapping against real Postgres
dotnet test tests/Talent.Mcp.Conformance       # protocol conformance for 2026-07-28
dotnet test tests/Talent.Mcp.E2E               # real client → HTTP → OAuth → Postgres

dotnet test                                    # everything
```

The conformance suite carries the most signal. It asserts that `tools/list` returns all six tools
**by name** — not merely that the server starts. A tool set silently emptied by trimming answers
`-32601` with no crash and no error log, which is this project's worst failure mode and the reason
tools are registered explicitly rather than by assembly scan.

---

## Benchmarks

```bash
dotnet run --project bench/Talent.Mcp.Bench -c Release
```

Both targets are pure functions over `Talent.Domain` — no repository, no `DbContext`, no Docker —
which is what makes the numbers reproducible rather than a description of one machine's Postgres.

`CandidateFitScorer.Score` is measured for a single pair and across a 500-candidate shortlist, the
ceiling `bulk_score_shortlist` works to. `SkillNormalizer.Extract` is measured across three CV
lengths, because its cost is proportional to alias count times text length rather than flat.

---

## Configuration

Settings bind from `appsettings.json`, environment variables, or any other `IConfiguration` source.
Environment variables use `__` for the `:` separator.

| Setting | Environment variable | Purpose |
|---|---|---|
| `ConnectionStrings:Talent` | `ConnectionStrings__Talent` | Postgres, in Npgsql keyword format (not a URI) |
| `Talent:HandleSigningKey` | `Talent__HandleSigningKey` | Base64, **at least 32 bytes**. Signs pagination and confirmation handles |
| `Talent:Auth:Authority` | `Talent__Auth__Authority` | OIDC issuer, e.g. `http://localhost:8080/realms/talent` |
| `Talent:Otel:Endpoint` | `Talent__Otel__Endpoint` | OTLP collector, e.g. `http://localhost:4317` |
| `Talent:DefaultPageSize` | `Talent__DefaultPageSize` | Default page size (20) |
| `Talent:MaxPageSize` | `Talent__MaxPageSize` | Page size ceiling (100) |

The connection string, signing key and issuer are **not** in `appsettings.json` on purpose:
production supplies all three from the environment and the host refuses to start without them. Dev
values live in `appsettings.Development.json`, where the signing key is a labelled placeholder — it
decodes to an ASCII string. Never deploy with it; anyone who can read the file can forge a handle.

The stdio host needs a connection string too. It is not a thin client: it reaches Postgres through
the same adapters as the HTTP host, so only `extract_skills` works without one.

---

## Install

**As a `dotnet tool` (stdio, for Claude Code and Claude Desktop):**

```bash
dotnet tool install --global Talent.Mcp.Server   # provides the `talent-mcp` command
talent-mcp
```

Note the package id is `Talent.Mcp.Server` and the command is `talent-mcp`.
[`Talent.Mcp.Toolkit`](https://www.nuget.org/packages/Talent.Mcp.Toolkit) is a **library**, not a
tool — it is the reusable protocol layer: signed handles, a Postgres `IMcpTaskStore`, cache policies,
and OTel context extraction from `_meta`.

See [docs/CLAUDE_INTEGRATION.md](./docs/CLAUDE_INTEGRATION.md) for client configuration,
[docs/INSTALLATION.md](./docs/INSTALLATION.md) for every setup path, and
[docs/DEVELOPMENT.md](./docs/DEVELOPMENT.md) for the local workflow.

**As a container:**

```bash
docker run -p 5000:5000 ghcr.io/juxn89/talent-mcp:latest
```

**Releasing** is tag-driven — `git tag v1.2.3 && git push origin v1.2.3` publishes both packages, the
image, and a GitHub release. `Talent.Mcp.Toolkit` and `Talent.Mcp.Server` version independently, so a
breaking change in the library does not force an unearned major on the tool.

---

## Decisions

The reasoning, including what was tried and rejected:

- [ADR-0001 · Streamable HTTP session mode](./docs/adr/0001-streamable-http-session-mode.md) — why `Stateless` is set explicitly
- [ADR-0002 · Native AOT and explicit tool registration](./docs/adr/0002-native-aot-and-explicit-tool-registration.md) — trimming silently empties a reflection-discovered tool set, measured
- [ADR-0003 · Cross-node task input responses](./docs/adr/0003-cross-node-task-input-responses.md) — `LISTEN`/`NOTIFY` plus a sweep, because a stateless server answers the follow-up on a different node
- [ADR-0004 · One tool surface, two hosts](./docs/adr/0004-shared-tool-surface-across-both-hosts.md) — and why the stdio host takes EF Core deliberately
- [ADR-0005 · Pre-registration, not DCR or CIMD](./docs/adr/0005-client-registration-pre-registration-not-dcr-or-cimd.md)
- [ADR-0006 · Observability instrumentation](./docs/adr/0006-observability-instrumentation.md) — why `AddCallToolFilter` never fires for a registered tool
- [ADR-0007 · Trim-clean over Native AOT](./docs/adr/0007-trim-clean-over-native-aot.md)

Dated verification records live in [`docs/verification/`](./docs/verification/) — what was checked,
against which source, on what date. Grep there before re-verifying something.

The full plan, which is the source of truth for scope and phases, is
[`docs/plans/a2-talent-mcp.md`](./docs/plans/a2-talent-mcp.md). Contributor and agent guidelines are
in [`AGENTS.md`](./AGENTS.md). Release history is in [`CHANGELOG.md`](./CHANGELOG.md).

---

## License

[Apache 2.0](./LICENSE)
