# Claude Integration Guide

Set up Talent.Mcp as an MCP server in Claude Code or Claude Desktop.

---

## Claude Code (Web + CLI)

### Installation

1. Install the dotnet tool:
   ```bash
   dotnet tool install --global Talent.Mcp.Server
   ```

2. Verify it works:
   ```bash
   talent-mcp --version
   ```

### Configuration

Claude Code supports MCP servers via settings or CLI. Choose one:

#### Option A: Settings UI (Recommended)

1. Open Claude Code
2. Go to **Settings** → **MCP Servers** (or **Model Context Protocol**)
3. Click **Add Server**
4. Enter:
   - **Name:** `talent-mcp`
   - **Command:** `talent-mcp`
   - **Args:** (leave empty)
5. Click **Save**

#### Option B: settings.json

Edit `~/.claude/settings.json`:

```json
{
  "mcpServers": {
    "talent-mcp": {
      "command": "talent-mcp",
      "args": [],
      "env": {
        "KEYCLOAK_ISSUER": "http://localhost:8080/realms/talent"
      }
    }
  }
}
```

### Verification

1. Restart Claude Code
2. In the chat, try asking Claude to use a tool:
   > "Search for backend jobs near San Francisco with Python skills"
3. Claude should show the `search_jobs` tool in the response

If tools don't appear:
- Check Claude Code's MCP Server status (bottom of settings)
- Verify `talent-mcp` command runs: `talent-mcp` in terminal
- Check logs in Claude Code's developer console (DevTools)

---

## Claude Desktop (macOS / Windows)

### Installation

1. Install the dotnet tool:
   ```bash
   dotnet tool install --global Talent.Mcp.Server
   ```

2. Verify it works:
   ```bash
   talent-mcp --version
   ```

### Configuration

Edit the Claude Desktop config file:

**macOS:**
```bash
open ~/Library/Application\ Support/Claude/claude_desktop_config.json
```

**Windows:**
```powershell
notepad "$env:APPDATA\Claude\claude_desktop_config.json"
```

If the file doesn't exist, create it. Add (or merge into existing):

```json
{
  "mcpServers": {
    "talent-mcp": {
      "command": "talent-mcp",
      "env": {
        "KEYCLOAK_ISSUER": "http://localhost:8080/realms/talent"
      }
    }
  }
}
```

### Verification

1. Quit and restart Claude Desktop
2. In the chat, try asking Claude to use a tool:
   > "Get the details for job ID 42"
3. Claude should show the `get_job` tool
4. If tools don't appear, check the **MCP Server Status** in the settings panel

---

## Environment Variables

The `talent-mcp` command reads configuration from the environment. Key variables:

| Variable | Default | Purpose |
|---|---|---|
| `KEYCLOAK_ISSUER` | `http://localhost:8080/realms/talent` | OAuth 2.1 issuer endpoint |
| `KEYCLOAK_REALM` | `talent` | Keycloak realm name |
| `ASPNETCORE_ENVIRONMENT` | `Development` | Environment (Development, Production) |
| `OTEL_EXPORTER_OTLP_ENDPOINT` | `http://localhost:4317` | OpenTelemetry collector (optional) |

Set them before running `talent-mcp`:

**macOS/Linux:**
```bash
export KEYCLOAK_ISSUER=http://keycloak.example.com/realms/talent
talent-mcp
```

**Windows PowerShell:**
```powershell
$env:KEYCLOAK_ISSUER = "http://keycloak.example.com/realms/talent"
talent-mcp
```

Or in the config file (`env` section in `claude_desktop_config.json`).

---

## OAuth Setup

The Talent.Mcp server requires OAuth tokens for tool calls. Claude handles this automatically:

1. **First call to a tool** → Claude initiates OAuth authorization code flow
2. **Browser redirect** → You approve access in your browser (Keycloak login)
3. **Token returned** → Claude caches the token
4. **Tool executes** → Token is included in the request

**Default scopes requested:**
- `openid` — Identity
- `profile` — Profile info
- `email` — Email address
- `talent.jobs.read` — Job and skill operations
- `talent.candidates.read` — Candidate scoring
- `talent.candidates.write` — Bulk operations
- `talent.candidates.reject` — Destructive operations

The server enforces scopes per tool, so attempting an operation without the required scope returns a 403 error.

**For local testing:**
- Keycloak must be running: `docker compose -f deploy/compose.yaml up -d`
- Default client: `talent-mcp-client` (pre-configured in realm.json)
- Default user: `recruiter` / `recruiter` (for manual testing)

---

## Troubleshooting

| Problem | Solution |
|---|---|
| "talent-mcp command not found" | Install with `dotnet tool install --global Talent.Mcp.Server` |
| Tools don't appear in Claude | Restart Claude; check MCP Server status in settings |
| "OAuth token expired" | Claude will re-authenticate automatically on the next request |
| "401 Unauthorized" | Keycloak not running or wrong issuer URL; check `KEYCLOAK_ISSUER` |
| "Connection refused" | Verify `talent-mcp` runs: `talent-mcp` in terminal (should wait for input) |
| Logs not visible | Enable debug mode in Claude settings (if available) or check OS system logs |

---

## Next Steps

- **[INSTALLATION.md](./INSTALLATION.md)** — Other installation paths
- **[DEVELOPMENT.md](./DEVELOPMENT.md)** — Local dev and debugging
- **[OAUTH_SETUP.md](./OAUTH_SETUP.md)** — Detailed OAuth flow walkthrough
