# Installation Guide

Three paths to get Talent.Mcp running. Choose based on your use case.

## 1. Docker Compose (Recommended for Getting Started)

Complete stack with Postgres, Keycloak, observability, and the MCP server.

**Prerequisites:** Docker, Docker Compose

**Steps:**

```bash
git clone https://github.com/juxn89/talent-mcp.git
cd talent-mcp
docker compose -f deploy/compose.yaml up -d
```

**What's running:**

| Service | URL | Credentials | Purpose |
|---|---|---|---|
| Talent.Mcp | http://localhost:5000 | (OAuth required) | MCP server |
| Keycloak | http://localhost:8080 | admin / admin | OAuth provider |
| PostgreSQL | localhost:5432 | talent / talent | Database |
| Jaeger | http://localhost:16686 | — | Trace visualization |
| Prometheus | http://localhost:9090 | — | Metrics collection |
| Grafana | http://localhost:3000 | admin / admin | Dashboards |

**Test the server:**

```bash
# Get an OAuth token (replace with actual token flow)
TOKEN=$(curl -s -X POST http://localhost:8080/realms/talent/protocol/openid-connect/token \
  -d "client_id=talent-mcp-client" \
  -d "client_secret=<secret>" \
  -d "grant_type=client_credentials" \
  -d "scope=talent.jobs.read" \
  | jq -r '.access_token')

# Call a tool
curl http://localhost:5000/tools/list \
  -H "Authorization: Bearer $TOKEN"
```

**Teardown:**

```bash
docker compose -f deploy/compose.yaml down
# Add -v to delete the Postgres volume (clears all data)
docker compose -f deploy/compose.yaml down -v
```

---

## 2. dotnet tool (For Claude Code / Claude Desktop)

Install as a command-line tool for use with Claude's MCP client.

**Prerequisites:** .NET 10.0+

**Steps:**

```bash
# Install globally
dotnet tool install --global Talent.Mcp.Server

# Run (starts stdio host)
talent-mcp

# Or update if already installed
dotnet tool update --global Talent.Mcp.Server
```

**Usage in Claude:**

See [CLAUDE_INTEGRATION.md](./CLAUDE_INTEGRATION.md) for Claude Code and Claude Desktop configuration.

**Important notes:**

- The stdio host requires a local **Keycloak or OAuth provider** running somewhere accessible
- Default OAuth issuer: `http://localhost:8080/realms/talent` (set via environment or config)
- For local testing, run `docker compose -f deploy/compose.yaml up -d` in another terminal
- Uninstall: `dotnet tool uninstall --global Talent.Mcp.Server`

---

## 3. Local Development

Build and run from source for contributing or customization.

**Prerequisites:**

- .NET 10.0.400 SDK (pinned in `global.json`)
- Docker & Docker Compose (for infrastructure tests)
- PostgreSQL 16+ (optional; Testcontainers will provision one for tests)

**Steps:**

```bash
git clone https://github.com/juxn89/talent-mcp.git
cd talent-mcp

# Restore packages
dotnet restore

# Build
dotnet build

# (Optional) Start the compose stack for E2E testing
docker compose -f deploy/compose.yaml up -d

# Run tests
dotnet test

# Run the HTTP server locally
dotnet run --project src/Talent.Mcp.Server

# Or the stdio host
dotnet run --project src/Talent.Mcp.Server.Stdio
```

**Server endpoints (local dev):**

- HTTP host: `http://localhost:5000` (requires OAuth token in `Authorization` header)
- Stdio host: Reads from stdin, writes to stdout (for Claude integration)

**Environment variables:**

```bash
# OAuth issuer (Keycloak endpoint)
export KEYCLOAK_ISSUER=http://localhost:8080/realms/talent

# Database connection
export DATABASE_URL=Host=localhost;Database=talent;Username=talent;Password=talent

# Observability
export OTEL_EXPORTER_OTLP_ENDPOINT=http://localhost:4317
```

See `src/Talent.Mcp.Server/appsettings.json` for all available options.

---

## Verifying the Installation

**For Docker Compose:**

```bash
# Check all services are healthy
docker compose -f deploy/compose.yaml ps

# Query tools (requires token — see test section above)
curl http://localhost:5000/tools/list -H "Authorization: Bearer <token>"
```

**For dotnet tool:**

```bash
# Verify installation
talent-mcp --version

# Test with Claude Code or Claude Desktop (see integration guide)
```

**For local dev:**

```bash
# All tests pass
dotnet test

# Build succeeds with 0 warnings
dotnet build

# HTTP server starts without errors
dotnet run --project src/Talent.Mcp.Server
```

---

## Troubleshooting

| Issue | Solution |
|---|---|
| Docker daemon not running | Start Docker or Docker Desktop |
| Postgres connection refused | Ensure compose stack is up: `docker compose -f deploy/compose.yaml up -d` |
| Keycloak realm not imported | Delete Postgres volume: `docker compose down -v` then `docker compose up -d` |
| `dotnet` command not found | Install .NET 10.0+ from https://dot.net |
| Tests fail with missing Docker | Make sure Docker daemon is running; Testcontainers requires it |
| Port already in use (5000, 8080, 5432) | Change port mappings in `deploy/compose.yaml` |

---

## Next Steps

- **[CLAUDE_INTEGRATION.md](./CLAUDE_INTEGRATION.md)** — Set up in Claude Code or Claude Desktop
- **[DEVELOPMENT.md](./DEVELOPMENT.md)** — Local dev workflow
- **[OAUTH_SETUP.md](./OAUTH_SETUP.md)** — OAuth flow details and token testing
