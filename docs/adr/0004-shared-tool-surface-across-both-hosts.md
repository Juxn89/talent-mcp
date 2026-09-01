# ADR-0004 · One tool surface, two hosts — and the stdio host does reach Postgres

| | |
|---|---|
| **Status** | Accepted |
| **Date** | 1 Sep 2026 |
| **Phase** | F2 (MCP tools) |
| **Closes** | The open question in [ADR-0002 · Scope limit](./0002-native-aot-and-explicit-tool-registration.md#scope-limit-of-this-experiment), item 1 |
| **Constrains** | F6 — the AOT target is now known to include EF Core |

## Context

ADR-0002 resolved the plan's stated AOT risk (reflection-based tool discovery) and then flagged
something the plan had not asked: **does `Talent.Mcp.Server.Stdio` need EF Core at all?** It
deliberately deferred the answer — *"Decide this with the code in front of you, not now."* The code
now exists: four of the six tools read from Postgres through `IJobRepository` /
`ICandidateRepository`, and only `extract_skills` and the pure scorer are free of it.

Two decisions were entangled and are separated here.

## Decision 1 — both hosts serve the same six tools

The stdio host is not a reduced build. `tools/list` returns the same six names, in the same order,
over stdio and over Streamable HTTP.

The alternative was tempting: serve only the two deterministic tools (`extract_skills`,
`score_candidate_fit` against caller-supplied data) over stdio, which needs no database and makes
Native AOT plausible. It was rejected because **the tool surface is the product**. A conformance
suite that asserts six tools by name — the test ADR-0002 argued for, because a silently empty tool
set is this project's worst failure mode — would have to fork per host, and "one server, two
transports" would become "one server and a demo".

## Decision 2 — the stdio host reaches Postgres directly, and therefore takes EF Core

It composes the same `Talent.Infrastructure` adapters as the HTTP host and reads the connection
string from configuration. Not a thin client proxying to the HTTP host: that would need a running
HTTP server plus OAuth to answer a `dotnet tool` invocation, which is a worse experience than
requiring a connection string, and it would make the stdio host untestable without the whole stack.

**Consequence, stated plainly: Native AOT for the stdio host now depends on EF Core compiled
models.** ADR-0002 already identified that as the real gate. This ADR does not resolve it — it
records that the gate was walked into deliberately, with the product reason above, rather than
avoided by shrinking the deliverable. F6 measures compiled-models AOT, and if it does not hold, the
honest output is a trimmed self-contained build with a documented finding. The plan's F6 wording
(*"Native AOT (o el hallazgo documentado)"*) already allows for that, and the plan's cut order lists
AOT first if scope has to give.

What is **not** conceded: trim-clean. Both hosts stay free of `IL2026`/`IL3050`, and explicit
`WithTools<T>()` registration is unchanged and permanent.

## Decision 3 — the tools live in a shared library, `Talent.Mcp.Tools`

A seventh project, which `AGENTS.md`'s layout did not list and which is being added with the reason
recorded rather than silently.

Decisions 1 and 2 make both hosts serve identical tools, so the tool types have to live somewhere
both can reference. The two alternatives were worse:

- **Stdio host references the HTTP host.** A console executable referencing an ASP.NET Core
  executable, which drags the entire web stack into the process where cold start is the whole point.
- **Duplicate the tool types per host.** Guarantees divergence, and there is no test that would
  catch two copies drifting apart.

`Talent.Mcp.Tools` references `Talent.Application`, `Talent.Mcp.Toolkit` and the `ModelContextProtocol`
hosting package — **not** `ModelContextProtocol.AspNetCore`, and **not** `Talent.Infrastructure`. So:

- the tools depend on use cases and ports, never on EF Core, and an architecture test enforces it;
- registration order lives in one place, `TalentTools.AddTalentTools(...)`, which is what makes
  deterministic tool ordering identical across hosts rather than identical by coincidence;
- only the two hosts' composition roots reference `Talent.Infrastructure`, which is what a
  composition root is for.

The `WithTools<T>()`-one-call-per-type rule from ADR-0002 is unchanged; the calls simply sit inside
that shared extension method instead of being copy-pasted into two `Program.cs` files.

## Consequences

- `AGENTS.md`'s architecture layout gains `src/Talent.Mcp.Tools/` and its tool-surface section is
  updated to say both hosts serve all six.
- The architecture suite gains a rule: `Talent.Mcp.Tools` must not depend on `Talent.Infrastructure`.
  Without it, "the tools go through ports" is an intention rather than a constraint.
- F6's AOT experiment must be run against the stdio host's *real* dependency graph — EF Core, Npgsql
  and OpenTelemetry included. ADR-0002's minimal-graph numbers are a floor, not a prediction.
- The stdio host needs a connection string to answer anything but `extract_skills`. Its README
  section in F5 has to say so, and say what a missing one looks like.
