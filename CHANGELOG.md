# Changelog

All notable changes to this project are documented here.

The format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and this project
adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

Two packages ship from this repository and **version independently** since F6:

| Package | Kind |
|---|---|
| [`Talent.Mcp.Toolkit`](https://www.nuget.org/packages/Talent.Mcp.Toolkit) | Reusable MCP protocol primitives (library) |
| [`Talent.Mcp.Server`](https://www.nuget.org/packages/Talent.Mcp.Server) | The `talent-mcp` stdio host (dotnet tool) |

Entries below cover both unless a package is named.

## [Unreleased]

### Added

- **Benchmarks.** `bench/Talent.Mcp.Bench` (BenchmarkDotNet) measures the two deterministic domain
  functions: `CandidateFitScorer.Score` — one pair and a 500-candidate shortlist, the ceiling
  `bulk_score_shortlist` works to — and `SkillNormalizer.Extract` across three CV lengths, since its
  cost is proportional to alias count times text length rather than flat.
- **Trim and AOT analyzers** on `Talent.Domain`, `Talent.Application`, `Talent.Mcp.Tools` and
  `Talent.Mcp.Toolkit`. With the repo-wide `TreatWarningsAsErrors`, a newly introduced
  reflection-based serialization site is now a build error. This closes the consequence left open in
  [ADR-0002](docs/adr/0002-native-aot-and-explicit-tool-registration.md).
- `TrimPostureRules` asserts the four assemblies still declare `IsTrimmable`, so deleting the
  property from a `.csproj` fails a test instead of silently disarming the analyzers.
- `TaskStoreSerializationTests` pins the `jsonb` wire format of `InputRequest` and `InputResponse`
  across the source-generation change.
- `HandleCodecTests` pins a golden handle vector — fixed key, fixed clock, fixed payload, exact
  base64url — to guard the still-pending `HandleCodec` API change.

### Changed

- `PostgresMcpTaskStore` and `ToolExecutionTelemetry` now serialize through source-generated
  contexts (`McpTasksJsonContext`, `McpJsonUtilities.GetTypeInfo<T>`) instead of reflection.
  Byte-identical output, verified by test — existing rows need no migration.
- **Package versions are decoupled.** `Talent.Mcp.Toolkit` and `Talent.Mcp.Server` each carry their
  own `<Version>`, so a breaking change in the library no longer forces an unearned major on the
  tool.
- CI's infrastructure-test Postgres sidecar moves from `16-alpine` to `18.6-alpine`, matching
  `deploy/compose.yaml`. Those tests exist to catch provider-specific mapping behaviour, so running
  them against a different major defeated their purpose.

### Fixed

- **The version was declared twice with different values** — `1.0.0` in `Directory.Build.props` and
  `1.0.1` in `Directory.Packages.props`. The latter is imported second and won, so the former was
  dead and **v1.0.2 through v1.0.5 were all published stamped `1.0.1`**. Assembly version properties
  now live in one place, and `AssemblyVersion`/`FileVersion` are derived from `Version` rather than
  pinned separately, so they cannot drift again.
- `deploy/compose.yaml` no longer opens by describing itself as "F0 scope … nothing else" while
  defining the full observability stack, and its commented-out server placeholder — which pointed at
  a `deploy/Dockerfile` that has never existed — is removed.

## [1.0.5] — 2026-09-07

### Fixed

- Dockerfile builds on `mcr.microsoft.com/dotnet/sdk:10.0` and runs on `aspnet:10.0-alpine`.

## [1.0.4] — 2026-09-07

### Fixed

- Docker metadata tags: dropped invalid `ref`/`sha` entries that produced unusable image tags.

## [1.0.3] — 2026-09-07

Re-tag of the v1.0.2 commit after a failed publish run. **No code difference from 1.0.2.**

## [1.0.2] — 2026-09-07

### Changed

- NuGet publishing switched from OIDC Trusted Publishing to an API key.

## [1.0.1] — 2026-09-07

### Added

- NuGet Trusted Publishing via OIDC, removing the need for a stored publish secret.

## [1.0.0] — 2026-09-07

First release. Phases F0 through F5 of
[the plan](docs/plans/a2-talent-mcp.md): the recruitment domain, the six-tool MCP surface on the
2026-07-28 revision, OAuth 2.1 with Keycloak, the OpenTelemetry stack, and the packaging and CI that
publish all three artifacts.

### Added

- **Six MCP tools** over stateless Streamable HTTP and stdio, serving an identical surface from both
  hosts ([ADR-0004](docs/adr/0004-shared-tool-surface-across-both-hosts.md)): `search_jobs`,
  `get_job`, `extract_skills`, `score_candidate_fit`, `reject_candidate`, `bulk_score_shortlist`.
- **Handle-based pagination** replacing sessions, which the 2026-07-28 revision removed. Handles are
  signed and TTL-bounded so a client cannot forge or extend one.
- **MRTR** on `reject_candidate` for destructive confirmation, including the degraded path when the
  client cannot elicit.
- **The Tasks extension** on `bulk_score_shortlist`, backed by `PostgresMcpTaskStore` so in-flight
  work survives a restart. Cross-node input delivery uses `LISTEN`/`NOTIFY` with a sweep fallback
  ([ADR-0003](docs/adr/0003-cross-node-task-input-responses.md)).
- **OAuth 2.1 + PKCE S256** with per-tool scopes distinguishing read, write and destructive, against
  a versioned Keycloak realm ([ADR-0005](docs/adr/0005-client-registration-pre-registration-not-dcr-or-cimd.md)).
- **OpenTelemetry** traces and metrics with client context extracted from `_meta`, exported to a
  Collector, Jaeger, Prometheus, Loki and Grafana — all versioned in `deploy/`
  ([ADR-0006](docs/adr/0006-observability-instrumentation.md)).
- **A five-level test pyramid**, all of which gate the merge: architecture rules, pure domain, EF
  mapping against real Postgres, tools over the in-memory transport, protocol conformance, and E2E
  against the real stack.

### Notes

- Sessions are not used: `SessionMode.Stateless` is set explicitly
  ([ADR-0001](docs/adr/0001-streamable-http-session-mode.md)).
- Tools are registered with explicit `WithTools<T>()` calls, never by assembly scan — trimming
  silently empties a reflection-discovered tool set, and the failure is a `-32601` with no crash and
  no error log ([ADR-0002](docs/adr/0002-native-aot-and-explicit-tool-registration.md)).

[Unreleased]: https://github.com/Juxn89/talent-mcp/compare/v1.0.5...HEAD
[1.0.5]: https://github.com/Juxn89/talent-mcp/compare/v1.0.4...v1.0.5
[1.0.4]: https://github.com/Juxn89/talent-mcp/compare/v1.0.3...v1.0.4
[1.0.3]: https://github.com/Juxn89/talent-mcp/compare/v1.0.2...v1.0.3
[1.0.2]: https://github.com/Juxn89/talent-mcp/compare/v1.0.1...v1.0.2
[1.0.1]: https://github.com/Juxn89/talent-mcp/compare/v1.0.0...v1.0.1
[1.0.0]: https://github.com/Juxn89/talent-mcp/releases/tag/v1.0.0
