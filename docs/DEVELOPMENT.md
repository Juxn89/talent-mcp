# Development Guide

Local development workflow, debugging, and contributing.

---

## Setup

**Prerequisites:**
- .NET 10.0.400 (pinned in `global.json`)
- Docker & Docker Compose (for E2E tests)
- PostgreSQL client tools (optional, for manual DB inspection)
- A code editor (VS Code, Rider, Visual Studio)

**Initial setup:**

```bash
git clone https://github.com/juxn89/talent-mcp.git
cd talent-mcp

# Restore packages
dotnet restore

# Build (must succeed with 0 warnings)
dotnet build

# Install git hooks (enforces style before commit)
# (If your repo uses pre-commit hooks — check if they exist)
```

---

## Running Locally

### HTTP Server

```bash
# Requires Keycloak and Postgres running
docker compose -f deploy/compose.yaml up -d

# Start the HTTP host
dotnet run --project src/Talent.Mcp.Server
```

Server runs on `http://localhost:5000`. All calls require OAuth bearer token.

**Test a call:**

```bash
# Get a token (hardcoded for local testing)
TOKEN=<bearer_token_from_keycloak>

curl -X POST http://localhost:5000/tools/call \
  -H "Authorization: Bearer $TOKEN" \
  -H "Content-Type: application/json" \
  -d '{
    "jsonrpc": "2.0",
    "id": 1,
    "method": "tools/call",
    "params": {
      "name": "search_jobs",
      "arguments": {"query": "backend", "limit": 5}
    }
  }'
```

### Stdio Host

```bash
# For Claude Code / Claude Desktop testing
dotnet run --project src/Talent.Mcp.Server.Stdio
```

Reads MCP JSON-RPC from stdin, writes to stdout. Use with Claude's stdio transport or test clients.

---

## Testing

### All tests (recommended)

```bash
# Start the infrastructure first
docker compose -f deploy/compose.yaml up -d

# Run all 5 test levels
dotnet test
```

### Individual levels

```bash
# Architecture (no Docker needed)
dotnet test tests/Talent.Architecture.Tests

# Domain pure functions (no Docker)
dotnet test tests/Talent.Domain.Tests

# Infrastructure (Testcontainers Postgres)
dotnet test tests/Talent.Infrastructure.Tests

# Tools contract (in-memory transport)
dotnet test tests/Talent.Mcp.Tests

# Protocol conformance (compose stack)
docker compose -f deploy/compose.yaml up -d
dotnet test tests/Talent.Mcp.Conformance

# E2E (full stack)
dotnet test tests/Talent.Mcp.E2E
```

### Watch mode (auto-run on file change)

```bash
dotnet watch --project tests/Talent.Domain.Tests
```

### Run specific test

```bash
dotnet test tests/Talent.Domain.Tests -k "ScoringTests"
```

---

## Debugging

### Visual Studio / Rider

1. Set breakpoints in the code
2. Press **F5** (Debug) or click the debug button
3. Choose project to debug (usually HTTP or Stdio host)
4. Run and hit breakpoints normally

### VS Code

1. Install the C# Dev Kit extension
2. Create `.vscode/launch.json` if it doesn't exist
3. Add a debug configuration:
   ```json
   {
     "version": "0.2.0",
     "configurations": [
       {
         "name": ".NET Core Launch (web)",
         "type": "coreclr",
         "request": "launch",
         "preLaunchTask": "build",
         "program": "${workspaceFolder}/src/Talent.Mcp.Server/bin/Debug/net10.0/Talent.Mcp.Server.dll",
         "args": [],
         "cwd": "${workspaceFolder}",
         "stopAtEntry": false,
         "serverReadyAction": {
           "pattern": "\\bNow listening on:\\s+(https?://\\S+)",
           "uriFormat": "{0}",
           "action": "openExternally"
         }
       }
     ]
   }
   ```
4. Press **F5** to debug

### Logs

**HTTP host logs:**
- Written to console
- Also available in Jaeger (traces) and Grafana (metrics)

**Stdio host logs:**
- Written to `stderr` (so MCP JSON-RPC on stdout stays clean)
- Redirect to a file: `talent-mcp 2>debug.log`

**Database logs:**
- PostgreSQL logs in `docker compose logs postgres`
- EF Core: Enable `LogLevel.Debug` in `appsettings.Development.json`

### Trace inspection

Once running, open Jaeger:
```bash
open http://localhost:16686
```

Select service `Talent.Mcp.Server` and browse traces. Full stack is captured: client request → server processing → database queries.

---

## Code Style & Conventions

Enforced by `.editorconfig` and Roslyn analyzers (severity: **error**).

| Rule | Enforcement |
|---|---|
| Constants `PascalCase` (not `UPPER_SNAKE`) | Error (CA1802) |
| Async methods suffixed `Async` | Warning → Error |
| No magic strings for tool names | Use `Mcp.ToolNames.*` constants |
| No bare `ConfigureAwait()` in library code | Error (CA1707) |
| Warnings are errors | `TreatWarningsAsErrors=true` |

**Pre-commit check:**

Before committing, `dotnet build` must succeed with **0 warnings, 0 errors**.

```bash
dotnet build
# If this fails, fix all errors/warnings before git commit
```

---

## File Structure

```
src/
  Talent.Domain/               Pure functions, entities, enums
  Talent.Application/          Use cases, ports (interfaces)
  Talent.Infrastructure/       EF Core, Postgres, Keycloak, OTel
  Talent.Mcp.Tools/            The six MCP tool implementations
  Talent.Mcp.Server/           ASP.NET Core HTTP host
  Talent.Mcp.Server.Stdio/     stdio host (dotnet tool)
  Talent.Mcp.Toolkit/          NuGet library (protocol primitives)

tests/
  Talent.Architecture.Tests/   Dependency rule (ArchUnitNET)
  Talent.Domain.Tests/         Scoring & normalization (pure)
  Talent.Infrastructure.Tests/ EF mapping (Testcontainers)
  Talent.Mcp.Tests/            Tool contracts (in-memory)
  Talent.Mcp.Conformance/      Protocol compliance
  Talent.Mcp.E2E/              Full stack (compose + OAuth)

deploy/
  compose.yaml                 Dev stack (Postgres, Keycloak, observability)
  keycloak/realm.json          OAuth realm config
  otel/collector.yaml          OpenTelemetry Collector config
  grafana/dashboards/          Dashboard definitions
```

---

## Adding a New Tool

1. Create a tool class in `src/Talent.Mcp.Tools/`:

   ```csharp
   [McpServerToolType]
   public sealed class MyToolImplementation
   {
       [McpServerTool(Name = Mcp.ToolNames.MyTool), Description("Does something")]
       public static MyResult Execute(string input)
       {
           // implementation
       }
   }
   ```

2. Add the tool name constant in `src/Common/Constants/Mcp.cs`:

   ```csharp
   public const string MyTool = "my_tool";
   ```

3. Register in the composition root (`src/Talent.Mcp.Server/Program.cs`):

   ```csharp
   .WithTools<MyToolImplementation>()
   ```

4. Add tests in `tests/Talent.Mcp.Tests/`

5. Add E2E test in `tests/Talent.Mcp.E2E/`

6. Build: `dotnet build` (must pass with 0 warnings)

7. Test: `dotnet test`

---

## Modifying the Domain

The Domain layer is the heart — changes here need careful testing.

**Rules:**
- Domain has ZERO framework dependencies
- Pure functions only (no side effects, no I/O)
- All business rules live here

**Before committing domain changes:**

```bash
dotnet test tests/Talent.Domain.Tests      # Must all pass
dotnet test tests/Talent.Architecture.Tests # Dependency rule must hold
```

---

## Database Migrations

EF Core migrations are committed to source control and applied during `dotnet test`.

**Add a migration:**

```bash
dotnet ef migrations add MyMigrationName \
  --project src/Talent.Infrastructure \
  --startup-project src/Talent.Mcp.Server
```

This generates a migration file in `src/Talent.Infrastructure/Migrations/`.

**Apply manually (if needed):**

```bash
dotnet ef database update \
  --project src/Talent.Infrastructure \
  --startup-project src/Talent.Mcp.Server
```

**Important:** Never hand-edit migration files. If a migration is wrong, create a new migration to fix it.

---

## Troubleshooting

| Issue | Solution |
|---|---|
| Build fails with warnings | Check `.editorconfig` rules; Roslyn analyzers catch style violations |
| Tests fail with "Connection refused" | Start compose: `docker compose -f deploy/compose.yaml up -d` |
| "Entity type not registered" | EF Core model likely out of sync; check migrations |
| Keycloak login hangs | Keycloak container stopped; restart: `docker compose restart keycloak` |
| OTel exports failing silently | Check `OTEL_EXPORTER_OTLP_ENDPOINT` is reachable; view logs in `docker compose logs otel-collector` |
| dotnet command slow | First run is slow; subsequent runs use cache |

---

## Next Steps

- **[INSTALLATION.md](./INSTALLATION.md)** — Different installation paths
- **[CLAUDE_INTEGRATION.md](./CLAUDE_INTEGRATION.md)** — Claude Code/Desktop setup
- **[OAUTH_SETUP.md](./OAUTH_SETUP.md)** — OAuth flow details (when needed)
