# AI Agent Guidelines — Talent.Mcp (A2 · Recruitment Domain MCP Server)

## Overview

**Talent.Mcp** is an MCP server in C# for the recruitment domain, built against the **2026-07-28 Model Context Protocol revision**. It exposes typed tools (job search, candidate-fit scoring, skill extraction) over Streamable HTTP with stateless OAuth 2.1 + PKCE authorization. Published as a NuGet package (`Talent.Mcp.Toolkit`) and a dotnet tool.

**Plan:** [`docs/plans/a2-talent-mcp.md`](https://github.com/Juxn89/juangomezb/blob/feat/projects/docs/plans/a2-talent-mcp.md)

---

## 🎯 Core Principles (from catalog guidelines)

1. ✅ **Everything dockerized** — `docker compose up` starts Postgres, Keycloak, API, observability
2. ✅ **Open source, no paid licenses** — SDK is Apache-2.0, all third-party behind abstractions
3. ✅ **Clean Architecture verified in CI** — Domain → Application → Infrastructure → Presentation; ArchUnitNET enforces the dependency rule
4. ✅ **No magic strings/numbers** — ModelContextProtocol constants, OAuth scopes, tool names in typed enums/classes
5. ✅ **E2E without mocks** — real Postgres, real Keycloak, real MCP protocol over HTTP
6. ✅ **Verify before applying** — check protocol spec, versions, and capabilities before coding
7. ✅ **Own AGENTS.md + CLAUDE.md** — this file governs this repo only
8. ✅ **Published artifacts** — NuGet `Talent.Mcp.Toolkit`, dotnet tool `talent-mcp`, Docker image `ghcr.io/juxn89/talent-mcp`

---

## 📐 Architecture (Clean Architecture)

```
/src
  Talent.Domain/               ZERO framework deps. Entities, enums (SkillCategory, ScoreReason), scoring rules.
  Talent.Application/          Ports/interfaces (IJobRepository, ICandidateRepository, IHandleCodec, IShortlistScorer)
  Talent.Infrastructure/       Adapters: EF Core/Npgsql, Keycloak client, OTel exporter
  Talent.Mcp.Server/           Presentation: ASP.NET Core Streamable HTTP endpoint
  Talent.Mcp.Server.Stdio/     Presentation: stdio host → dotnet tool
  Talent.Mcp.Toolkit/          Technical library (protocol primitives) — no domain knowledge

/tests
  Talent.Architecture.Tests/   Dependency rule verification (ArchUnitNET)
  Talent.Domain.Tests/         Pure scoring, no infrastructure
  Talent.Mcp.Tests/            Tools against in-memory transport
  Talent.Mcp.Conformance/      Protocol conformance per 2026-07-28 (discover, MRTR, cache headers)
  Talent.Mcp.E2E/              Real compose: client → HTTP → OAuth → Postgres

/bench
  Talent.Mcp.Bench/            BenchmarkDotNet + cold-start measurement

/deploy
  compose.yaml                 Postgres, Keycloak, API, OTel stack
  keycloak/realm.json          OAuth 2.1 client config (PKCE S256 mandatory)
  otel/collector.yaml
  grafana/dashboards/

/docs
  adr/                         Architecture Decision Records with trade-offs
```

---

## 🔧 Stack (Verified 25 Aug 2026)

| Component | Version | License | Notes |
|---|---|---|---|
| .NET | 10.0+ | MIT | `global.json` pins the version |
| C# | 14 | MIT | Latest language features (records, required, primary ctors) |
| **MCP SDK** | **2.2.0** | **Apache-2.0** | Implements 2026-07-28 with downgrade compatibility |
| **MCP.AspNetCore** | 2.2.0 | Apache-2.0 | Streamable HTTP transport, stateless by default |
| **EF Core** | 9.0+ | MIT | Postgres provider (Npgsql) for domain data |
| **Npgsql** | 9.0+ | PostgreSQL | Async, type-safe Postgres driver |
| **Keycloak** | 25.0+ | Apache-2.0 | OAuth 2.1 / OIDC identity provider (Docker) |
| **OpenTelemetry** | 1.9+ | Apache-2.0 | Tracing → Jaeger, logs → Loki, metrics → Prometheus |
| **ArchUnitNET** | 4.11+ | Apache-2.0 | Architecture rule verification |
| **xUnit** | 2.8+ | Apache-2.0 | Test framework |
| **Testcontainers** | 3.8+ | MIT | Docker containers for tests (Postgres, Keycloak) |

**Avoided:** MassTransit (commercial since Jan 2026), AutoMapper (now open-core), Wolverine (commercial roadmap).

---

## 📋 Constantes (Guideline #4: No Magic Strings)

### MCP Protocol & Capabilities

```csharp
// src/Talent.Mcp.Server/Constants/MCP.cs
namespace Talent.Mcp.Server.Constants
{
    public static class MCP
    {
        public const string ProtocolVersion = "2026-07-28";
        public const string ServerName = "talent-mcp";
        public const string ServerVersion = "1.0.0";  // SemVer, read from .csproj
        
        public static class ToolNames
        {
            public const string SearchJobs = "search_jobs";
            public const string ScoreCandidateFit = "score_candidate_fit";
            public const string ExtractSkills = "extract_skills";
            public const string ListSkillCategories = "list_skill_categories";
        }
    }
}
```

### OAuth 2.1 + PKCE

```csharp
// src/Talent.Mcp.Server/Constants/OAuth.cs
public static class OAuth
{
    public const string Issuer = "http://localhost:8080/realms/talent";
    public const string ClientId = "talent-mcp-server";
    public const string Scope = "openid profile email";
    public const string CodeChallengeMethod = "S256";  // PKCE mandatory per 2026-07-28
}
```

### Database Schema

```csharp
// src/Talent.Domain/Constants/Schema.cs
public static class JobSchema
{
    public const int MaxTitleLength = 255;
    public const int MinSalaryRange = 0;
    public const int MaxExperienceYears = 50;
}

public static class SkillCategories
{
    public const string Backend = "backend";
    public const string Frontend = "frontend";
    public const string DevOps = "devops";
    public const string DataEngineering = "data-engineering";
    // etc.
}
```

**Enforcement:** ESLint-equivalent in C# via `.editorconfig` with Roslyn analyzers — severity `error` blocks merge.

---

## 🧪 Test Pyramid (Guideline #8)

Five levels, all block merge:

1. **Architecture** (no Docker)
   - Domain never references Npgsql, SDK, ASP.NET
   - Application only references Domain
   - Tools invoked only through interfaces

2. **Domain** (pure functions, no Docker)
   - Scoring logic, skill normalization
   - Input validation (invariants)
   - Zero external calls

3. **Application** (against fake ports)
   - UseCases with injected IJobRepository, IShortlistScorer
   - Happy path + error degradation (repo down → fallback)

4. **MCP** (Testcontainers — real Postgres, real Keycloak)
   - Protocol conformance: `server/discover` response shape
   - MRTR flow: client sends request → server asks for input → client retries with response
   - Cache headers on `tools/list`, `resources/list`
   - Deterministic tool order
   - OAuth flow: authorization → token exchange → secured tool call

5. **E2E** (real compose)
   - API up, Postgres seeded, Keycloak configured
   - OAuth login → tool call against real data
   - Trace propagation in headers

All runnable locally without a cluster or remote service.

---

## 🚀 Verification Gates (Guideline #9)

**Before deciding on a version/API/capability:**
- Cross-check against official sources (GitHub releases, NuGet, protocol spec)
- Note the date verified in the plan/code
- Example: MCP SDK 2.2.0 implements 2026-07-28 with backward compatibility — verified 25 Aug 2026 against https://github.com/modelcontextprotocol/spec/releases

**Before shipping code:**
- `dotnet build` (no warnings) ✅
- `dotnet test` (all five levels pass) ✅
- `docker compose up -d` + E2E test against real stack ✅
- `dotnet pack` (NuGet package valid) ✅
- CI in GitHub Actions runs the above ✅

Nothing ships without all gates green.

---

## 📦 Publishing (One-time setup)

### NuGet

1. Create NuGet API key at https://www.nuget.org/account/ApiKeys
2. Store as GitHub Secret: `NUGET_API_KEY`
3. CI pipeline (GitHub Actions, trigger on tag `v*`):
   ```yaml
   - run: dotnet pack -c Release
   - run: dotnet nuget push bin/Release/*.nupkg -k ${{ secrets.NUGET_API_KEY }} -s https://api.nuget.org/v3/index.json
   ```

### Docker Image (GHCR)

1. GitHub Personal Access Token (repo + read:packages scope)
2. Store as `GHCR_PAT` secret
3. CI pipeline:
   ```yaml
   - run: docker build -t ghcr.io/juxn89/talent-mcp:latest .
   - run: echo ${{ secrets.GHCR_PAT }} | docker login ghcr.io -u juxn89 --password-stdin
   - run: docker push ghcr.io/juxn89/talent-mcp:latest
   ```

---

## 📝 Naming Conventions

- **Classes:** PascalCase (e.g., `SearchJobsHandler`)
- **Methods:** PascalCase (e.g., `ExecuteAsync`)
- **Constants:** UPPER_SNAKE_CASE (e.g., `MAX_TITLE_LENGTH`)
- **Enums:** PascalCase members (e.g., `SkillCategory.Backend`)
- **Interfaces:** I-prefix (e.g., `IJobRepository`)
- **Files:** PascalCase matching class name
- **Namespaces:** `Talent.{Domain,Application,Infrastructure,Mcp.*}`

---

## 🔒 Security

- **OAuth 2.1** with PKCE S256 (mandatory, not negotiable)
- **HTTPS only** in production (environment var `ASPNETCORE_HTTPS_PORT`)
- **No API keys in code** — all secrets via environment / `IConfiguration`
- **CORS** restricted to localhost in dev, explicit origins in prod
- **Input validation** at Presentation layer (HTTP) and Domain layer (business rules)
- **Rate limiting** per client (IP + OAuth sub) — simple in-memory for now, Redis if needed

---

## 📊 Observability

All using OpenTelemetry (no vendor lock-in):

- **Traces:** Jaeger (localhost:16686 in compose)
- **Logs:** Loki (via OTel Collector)
- **Metrics:** Prometheus (localhost:9090 in compose)

Every tool execution emitted as a span with:
- `tool.name`, `tool.input`, `tool.output_tokens`
- `db.query_time`, `cache.hit`, `oauth.token_refresh`

Dashboards in Grafana (localhost:3000) versioned as code.

---

## 🔀 Build, Test, Deploy

```bash
# Development
dotnet restore                              # NuGet packages
dotnet build                                # compile + analyzers
dotnet test                                 # five test levels
docker compose up -d                        # local stack

# Publishing
git tag v1.0.0
git push origin v1.0.0
# → GitHub Actions auto-publishes to NuGet + GHCR

# Running the tool locally
dotnet tool install --local Talent.Mcp.Toolkit
# or
docker run ghcr.io/juxn89/talent-mcp:latest
```

---

## ⚠️ Common Pitfalls

1. ❌ **Using `Task` without `ConfigureAwait(false)`** — causes deadlock in console/tool context
2. ❌ **Magic strings in MCP tool names** — use `MCP.ToolNames.SearchJobs`, never `"search_jobs"`
3. ❌ **Skipping OAuth in local tests** — real Keycloak in Testcontainers teaches you actual auth flow
4. ❌ **Stateful server** — MCP 2026-07-28 is stateless; handles and round-trip state via client
5. ❌ **Forgetting `inputSchema` on tools** — required in 2026-07-28, client needs it to build UI
6. ❌ **Publishing NuGet without SemVer** — breaks downstream (A1) who depends on `Talent.Domain`

---

## 📚 References

- [MCP Spec 2026-07-28](https://spec.modelcontextprotocol.io/)
- [MCP SDK C# 2.2.0](https://www.nuget.org/packages/ModelContextProtocol)
- [.NET 10 Minimal APIs](https://learn.microsoft.com/en-us/aspnet/core/fundamentals/minimal-apis)
- [OAuth 2.1 PKCE](https://www.rfc-editor.org/rfc/rfc7636)
- [OpenTelemetry .NET](https://opentelemetry.io/docs/zero-code/net/getting-started/)
- [ArchUnitNET](https://archunitnet.readthedocs.io/)
