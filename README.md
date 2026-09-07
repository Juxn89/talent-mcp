# Talent.Mcp — MCP Server for Recruitment Domain

An MCP (Model Context Protocol) server in C# for the recruitment domain. Exposes typed tools for job search, candidate-fit scoring, and skill extraction, built against the **2026-07-28 Model Context Protocol revision**.

- **Protocol:** MCP 2026-07-28 with stateless Streamable HTTP
- **Auth:** OAuth 2.1 + PKCE S256
- **Database:** PostgreSQL + Entity Framework Core
- **Published as:** NuGet package (`Talent.Mcp.Toolkit`) + dotnet tool (`talent-mcp`) + Docker image

---

## Quick Start

### Prerequisites

- .NET 10.0+
- Docker & Docker Compose
- PostgreSQL 15+ (via compose)
- Keycloak 25.0+ (via compose)

### Development

```bash
# Clone and setup
git clone https://github.com/Juxn89/talent-mcp.git
cd talent-mcp
dotnet restore

# Start infrastructure
docker compose -f deploy/compose.yaml up -d

# Run tests
dotnet test

# Build the API
dotnet build
```

### Infrastructure

The `deploy/compose.yaml` stack, and where each piece lands:

| Service | Status | Purpose |
|---|---|---|
| **PostgreSQL** 18.6 | ✅ live | Domain data (jobs, candidates, skills) + Keycloak's own database |
| **Keycloak** 26.7.2 | ✅ live | OAuth 2.1 / OIDC provider, realm imported from `deploy/keycloak/realm.json` |
| **Talent.Mcp.Server** | F2 | Stateless Streamable HTTP host |
| **Observability** | F4 | OTel Collector, Jaeger, Prometheus, Grafana |

```bash
docker compose -f deploy/compose.yaml up -d
# Keycloak admin:  http://localhost:8080          (admin / admin)
# Realm discovery: http://localhost:8080/realms/talent/.well-known/openid-configuration
# Keycloak health: http://localhost:9000/health/ready
# Postgres:        localhost:5432                 (talent / talent, databases: talent, keycloak)
# Seed user:       recruiter / recruiter
```

All credentials are dev-only defaults and every one is overridable from the environment. Image tags
are pinned to exact patch versions so a green CI run stays reproducible.

Tear down with `docker compose -f deploy/compose.yaml down`; add `-v` to drop the Postgres volume,
which is also how you force a realm re-import.

---

## Architecture

**Clean Architecture** with strict dependency rule:

```
Presentation ──┐
               ├──▶ Application ──▶ Domain
Infrastructure ┘

Dependencies point inward only. Domain references nothing.
```

- **`Talent.Domain`** — Pure business rules, entities, enums. Zero framework dependencies.
- **`Talent.Application`** — UseCases and ports (interfaces). Depends only on Domain.
- **`Talent.Infrastructure`** — Adapters (EF Core, Keycloak, OTel). Implements ports.
- **`Talent.Mcp.Server`** — ASP.NET Core host. Streamable HTTP transport.
- **`Talent.Mcp.Server.Stdio`** — Stdio transport. Published as dotnet tool.
- **`Talent.Mcp.Toolkit`** — Technical library (protocol primitives). Published to NuGet.

Verified by `ArchUnitNET` on every build.

---

## Testing

All five test levels required for PR merge:

```bash
# Start the stack (required for infrastructure and conformance tests)
docker compose -f deploy/compose.yaml up -d

# Run all tests
dotnet test

# Or individual levels:
dotnet test tests/Talent.Architecture.Tests    # Dependency rule
dotnet test tests/Talent.Domain.Tests           # Pure functions (no Docker)
dotnet test tests/Talent.Infrastructure.Tests   # EF Core / Postgres mapping
dotnet test tests/Talent.Mcp.Tests              # Tool contracts
dotnet test tests/Talent.Mcp.Conformance        # Protocol spec
dotnet test tests/Talent.Mcp.E2E                # Full stack
```

CI enforces all five. Local development can skip the infrastructure tests if Docker is unavailable, but CI will catch issues.

---

## Publishing

### dotnet tool (`talent-mcp`)

```bash
# Installed globally
dotnet tool install --global Talent.Mcp.Server

# Run directly
talent-mcp

# Use in Claude Code/Desktop settings
```

### NuGet Packages

- **`Talent.Mcp.Server`** — dotnet tool with stdio MCP host
- **`Talent.Mcp.Toolkit`** — Reusable protocol primitives (handles, tasks, cache) for downstream projects

### Docker Image

```bash
# Build locally
docker build -t talent-mcp:latest .

# Run
docker run -p 5000:5000 talent-mcp:latest

# From GitHub Container Registry
docker pull ghcr.io/juxn89/talent-mcp:latest
docker run -p 5000:5000 ghcr.io/juxn89/talent-mcp:latest
```

### Release workflow

Tag a version to trigger automatic publishing:

```bash
git tag v1.0.0
git push origin v1.0.0
```

GitHub Actions will:
1. Run all CI tests ✅
2. Publish `Talent.Mcp.Server` to NuGet
3. Publish `Talent.Mcp.Toolkit` to NuGet
4. Build and push Docker image to `ghcr.io/juxn89/talent-mcp`
5. Create a GitHub Release

**Requires secrets:**
- `NUGET_API_KEY` — NuGet publish token
- `GHCR_PAT` — GitHub Personal Access Token with `write:packages` permission

---

## Installation & Configuration

See detailed guides:
- **[INSTALLATION.md](./docs/INSTALLATION.md)** — All setup paths (Docker, tool, local dev)
- **[CLAUDE_INTEGRATION.md](./docs/CLAUDE_INTEGRATION.md)** — Claude Code & Claude Desktop setup
- **[DEVELOPMENT.md](./docs/DEVELOPMENT.md)** — Local development workflow

---

## Tools (MCP Services)

| Tool | Input | Output | Required scope | Protocol capability exercised |
|---|---|---|---|---|
| `search_jobs` | Query, filters (location, salary, skills), page handle | Jobs + next signed handle | `talent.jobs.read` | Handle-based pagination (sessions are gone) |
| `get_job` | Job ID, `Region` header | Job + `ttlMs`/`cacheScope` | `talent.jobs.read` | Cacheable result + `[McpHeader]` promotion |
| `extract_skills` | Text (CV, job description) | Normalized skills + confidence | `talent.jobs.read` | Deterministic taxonomy normalization, no LLM |
| `score_candidate_fit` | Candidate ID, Job ID | Score (0–100) + per-component breakdown | `talent.candidates.read` | Explainable deterministic scoring |
| `reject_candidate` | Candidate ID, reason | Confirmation | `talent.candidates.reject` | MRTR — destructive op requiring `inputResponses` |
| `bulk_score_shortlist` | Shortlist ID | Task handle | `talent.candidates.write` | Tasks extension with Postgres store |

All tools require OAuth 2.1 authorization (Bearer token) and enforce their **own** scope — read,
write and destructive are not interchangeable. No tool calls an LLM: the server runs with no API
keys and at zero cost.

---

## Testing

Five-level pyramid, all gates block merge:

```bash
# 1. Architecture (no Docker)
dotnet test tests/Talent.Architecture.Tests

# 2. Domain (pure, no Docker)
dotnet test tests/Talent.Domain.Tests

# 3. Tools over the in-memory transport
dotnet test tests/Talent.Mcp.Tests

# 4. Protocol Conformance (Testcontainers)
dotnet test tests/Talent.Mcp.Conformance

# 5. End-to-end (real compose)
dotnet test tests/Talent.Mcp.E2E
```

Or run all:
```bash
dotnet test
```

---

## Publishing

### NuGet Package

```bash
dotnet pack src/Talent.Mcp.Toolkit -c Release
# → bin/Release/Talent.Mcp.Toolkit.*.nupkg

# Then (CI does this automatically on tag):
dotnet nuget push bin/Release/Talent.Mcp.Toolkit.*.nupkg \
  -k $NUGET_API_KEY -s https://api.nuget.org/v3/index.json
```

### Docker Image

```bash
docker build -t ghcr.io/juxn89/talent-mcp:latest .
docker push ghcr.io/juxn89/talent-mcp:latest
```

### As a dotnet tool

`Talent.Mcp.Toolkit` is a **library**, not a tool. The installable tool is `Talent.Mcp.Server`,
which exposes the `talent-mcp` command (stdio transport, for Claude Code / Claude Desktop):

```bash
dotnet tool install --global Talent.Mcp.Server
talent-mcp
```

---

## Configuration

Environment variables (or `appsettings.json`):

| Variable | Default | Purpose |
|---|---|---|
| `ASPNETCORE_URLS` | `http://localhost:5000` | API listen address |
| `DATABASE_CONNECTION_STRING` | `Host=localhost;Database=talent;Username=talent;Password=talent` | PostgreSQL connection (Npgsql keyword format, not a URI) |
| `KEYCLOAK_URL` | `http://localhost:8080` | Keycloak issuer |
| `KEYCLOAK_CLIENT_ID` | `talent-mcp-server` | OAuth client |
| `OTEL_EXPORTER_OTLP_ENDPOINT` | `http://localhost:4317` | OTel Collector |

See `src/Talent.Mcp.Server/appsettings.json` for full defaults.

---

## Documentation

- **[Plan](./docs/plans/a2-talent-mcp.md)** — Full architectural plan (source of truth for scope and phases)
- **[AGENTS.md](./AGENTS.md)** — AI agent guidelines for this repo
- **ADRs** — Architecture decisions in `/docs/adr/`

---

## License

Apache License 2.0 — See [LICENSE](./LICENSE)

---

## Next Steps

1. **Setup:**
   ```bash
   docker compose -f deploy/compose.yaml up -d
   dotnet restore
   dotnet build
   ```

2. **Implement Phase F1** (Domain, Application, Architecture Tests)

3. **Iterate:** Each phase is mergeable and testable independently.

See the [plan](./docs/plans/a2-talent-mcp.md#fases) for details.
