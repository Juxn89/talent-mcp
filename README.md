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
docker compose up -d

# Run tests
dotnet test

# Build the API
dotnet build
```

### Infrastructure

The `docker-compose.yaml` stack includes:
- **PostgreSQL** — domain data (jobs, candidates, skills)
- **Keycloak** — OAuth 2.1 / OIDC identity provider
- **API** — Talent.Mcp.Server (Streamable HTTP)
- **Observability** — OTel Collector, Jaeger, Prometheus, Grafana

```bash
docker compose up -d
# Keycloak: http://localhost:8080 (admin/admin)
# Grafana:  http://localhost:3000
# Jaeger:   http://localhost:16686
```

---

## Architecture

**Clean Architecture** with strict dependency rule:

```
Domain → Application → Infrastructure → Presentation
   ↑         ↑              ↑
   └─────────┴──────────────┘
     (only inward references)
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

| Tool | Input | Output | Purpose |
|---|---|---|---|
| `search_jobs` | Query, filters (location, salary, skills) | Job IDs with relevance | Find matching job postings |
| `score_candidate_fit` | Candidate ID, Job ID | Score (0–100) + reason | Deterministic candidate-to-job matching |
| `extract_skills` | Text (CV, job description) | Normalized skills + confidence | Skill extraction with categorization |
| `list_skill_categories` | (none) | Taxonomy of skill categories | Reference data for UI |

All tools require OAuth 2.1 authorization (Bearer token).

---

## Testing

Five-level pyramid, all gates block merge:

```bash
# 1. Architecture (no Docker)
dotnet test tests/Talent.Architecture.Tests

# 2. Domain (pure, no Docker)
dotnet test tests/Talent.Domain.Tests

# 3. Application (mocks)
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

```bash
dotnet tool install --global Talent.Mcp.Toolkit
talent-mcp --port 8000
```

---

## Configuration

Environment variables (or `appsettings.json`):

| Variable | Default | Purpose |
|---|---|---|
| `ASPNETCORE_URLS` | `http://localhost:5000` | API listen address |
| `DATABASE_CONNECTION_STRING` | `postgres://localhost/talent` | PostgreSQL connection |
| `KEYCLOAK_URL` | `http://localhost:8080` | Keycloak issuer |
| `KEYCLOAK_CLIENT_ID` | `talent-mcp-server` | OAuth client |
| `OTEL_EXPORTER_OTLP_ENDPOINT` | `http://localhost:4317` | OTel Collector |

See `src/Talent.Mcp.Server/appsettings.json` for full defaults.

---

## Documentation

- **[Plan](https://github.com/Juxn89/juangomezb/blob/feat/projects/docs/plans/a2-talent-mcp.md)** — Full architectural plan
- **[AGENTS.md](./AGENTS.md)** — AI agent guidelines for this repo
- **ADRs** — Architecture decisions in `/docs/adr/`

---

## License

Apache License 2.0 — See [LICENSE](./LICENSE)

---

## Next Steps

1. **Setup:**
   ```bash
   docker compose up -d
   dotnet restore
   dotnet build
   ```

2. **Implement Phase F1** (Domain, Application, Architecture Tests)

3. **Iterate:** Each phase is mergeable and testable independently.

See [plan](https://github.com/Juxn89/juangomezb/blob/feat/projects/docs/plans/a2-talent-mcp.md#fases) for details.
