# Verification · Npgsql's ActivitySource, and traces that reach Postgres

| | |
|---|---|
| **Date** | 21 Oct 2026 |
| **Issue** | [#13](https://github.com/Juxn89/talent-mcp/issues/13) |
| **Method** | A unit test against the tracer provider `AddTalentTelemetry` builds, a conformance test over real Streamable HTTP and a real Postgres (Testcontainers), and the full compose stack queried through Jaeger's API |
| **Why** | The README and AGENTS.md said a trace spans client → server → Postgres. Measured on 20 Oct 2026 across 200 traces in Jaeger, the largest had 2 spans, `POST /mcp/` and `tool.<name>`: no database span at all |

---

## 1. Why the database never appeared

**Claimed.** Client trace context arrives in `_meta`, so one trace spans client → server → Postgres.

**Observed.** Only the HTTP host's ASP.NET Core instrumentation and `TalentActivitySource` were
registered with the tracer provider. Npgsql does trace itself, but an `ActivitySource` with no
subscriber returns `null` from `StartActivity`, so its spans were never created, let alone exported.

## 2. The source name, at the pinned version

**Claimed** (from Npgsql's documentation): Npgsql 8.0 and later emit spans on a built-in
`ActivitySource` named `Npgsql`, and `Npgsql.OpenTelemetry`'s `AddNpgsql()` only registers that name.

**Observed** against `Npgsql` 10.0.3, the version pinned in `Directory.Packages.props`:

- `ObservabilityConformanceTests.A_tool_that_reads_Postgres_carries_the_trace_into_the_database`
  listens to `Npgsql` and `Talent.Mcp` during a real `search_jobs` call: Npgsql's spans arrive in the
  same trace, each with the tool span as its parent.
- With the compose stack, one `search_jobs` call produced a 5-span trace in Jaeger:
  `POST /mcp/` → `tool.search_jobs` → `CONNECT talent` and the `postgresql` command spans.

**Fixed.** `AddTalentTelemetry` registers `ExternalActivitySources.Npgsql` next to
`TalentActivitySource.Name`, for both hosts. No new package: the source is Npgsql's own.
`TelemetryRegistrationTests` builds the provider with no other listener present, which is the only way
to test the registration itself — a test-side `ActivityListener` would enable the source on its own and
pass either way.

## Open

- EF Core's own spans (query compilation, `SaveChanges`) are not registered, and no EF Core
  instrumentation package was evaluated. Npgsql's command spans already show every round trip to the
  database, which is what the trace was missing.
