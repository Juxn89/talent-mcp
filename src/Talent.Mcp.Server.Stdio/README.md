# talent-mcp

An **MCP server for the recruitment domain**, as a `dotnet tool` over stdio — for Claude Code, Claude
Desktop, or any MCP client that launches a process.

Built against the **2026-07-28 Model Context Protocol revision**. **No tool calls an LLM**: scoring
and skill normalization are deterministic pure functions, so it runs with no API keys and at zero
cost.

```bash
dotnet tool install --global Talent.Mcp.Server   # installs the `talent-mcp` command
```

## The tools

| Tool | What it does |
|---|---|
| `search_jobs` | Job search, paginated with a signed handle |
| `get_job` | One posting, cacheable, region-routable |
| `extract_skills` | Normalizes free text against a skill taxonomy |
| `score_candidate_fit` | Explainable 0–100 score with a per-component breakdown |
| `reject_candidate` | Destructive, gated on MRTR confirmation |
| `bulk_score_shortlist` | Long-running, on the Tasks extension |

## Configuration

It needs a PostgreSQL database. It is **not** a thin client — it reaches Postgres through the same
adapters as the HTTP host, so only `extract_skills` works without one.

| Environment variable | Purpose |
|---|---|
| `ConnectionStrings__Talent` | Postgres, Npgsql keyword format (not a URI) |
| `Talent__HandleSigningKey` | Base64, **at least 32 bytes**. Signs pagination and confirmation handles |
| `Talent__Database__MigrateAndSeedOnStartup` | Apply migrations and seed data at startup. Off unless set |

The signing key has no default on purpose: a fallback literal would make every deployment that forgot
to set one forgeable while looking perfectly healthy.

Point it at a fresh database once with `Talent__Database__MigrateAndSeedOnStartup=true` to get a
schema and realistic seed data, then turn it off — it is off by default because a schema change
should be a step somebody chose to run.

### Claude Code / Claude Desktop

```json
{
  "mcpServers": {
    "talent": {
      "command": "talent-mcp",
      "env": {
        "ConnectionStrings__Talent": "Host=localhost;Database=talent;Username=talent;Password=talent",
        "Talent__HandleSigningKey": "<base64, 32+ bytes>"
      }
    }
  }
}
```

## Notes

Cold start is about **320 ms** to a served request, measured in CI on the installed tool rather than
on a publish directory. It logs to **stderr**, never stdout — stdout is the JSON-RPC transport.

Full setup, the compose stack with Keycloak and OpenTelemetry, and the reasoning behind the design
are at [Juxn89/talent-mcp](https://github.com/Juxn89/talent-mcp). Apache-2.0.
