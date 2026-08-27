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

The `deploy/compose.yaml` stack includes:
- **PostgreSQL** — domain data (jobs, candidates, skills), seeded
- **Keycloak** — OAuth 2.1 / OIDC identity provider
- **API** — Talent.Mcp.Server (stateless Streamable HTTP)
- **Observability** — OTel Collector, Jaeger, Prometheus, Grafana

```bash
docker compose -f deploy/compose.yaml up -d
# Keycloak: http://localhost:8080 (admin/admin)
# Grafana:  http://localhost:3000
# Jaeger:   http://localhost:16686
```

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
