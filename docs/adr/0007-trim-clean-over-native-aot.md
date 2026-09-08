# ADR-0007 · Trim-clean over Native AOT, and the two sites that keep it from being free

| | |
|---|---|
| **Status** | Accepted |
| **Date** | 8 Sep 2026 |
| **Phase** | F6 (AOT, benchmarks, documentation) |
| **Closes** | The open question in [ADR-0002 · Scope limit](./0002-native-aot-and-explicit-tool-registration.md#scope-limit-of-this-experiment) — whether the AOT goal survives contact with EF Core; and its consequence *"CI should publish trimmed at least once"* |
| **Depends on** | [ADR-0004](./0004-shared-tool-surface-across-both-hosts.md) — the stdio host reaches Postgres, so EF Core is inside the AOT target |

## Context

The plan's decision table said "Native AOT: yes, with a published cold-start benchmark". Two ADRs
have narrowed that since.

[ADR-0002](./0002-native-aot-and-explicit-tool-registration.md) resolved the risk the plan actually
named — reflection-based tool discovery — and then flagged a bigger one it had not: EF Core builds
its model at runtime and is not AOT-compatible, the supported route being compiled models. It
deferred the decision explicitly: *"Decide this with the code in front of you, not now."*

[ADR-0004](./0004-shared-tool-surface-across-both-hosts.md) decided it. Both hosts serve all six
tools and both compose the same EF Core adapters, because the tool surface is the product. It is
headed `Constrains | F6 — the AOT target is now known to include EF Core`, and it pre-authorized the
outcome: *"if it does not hold, the honest output is a trimmed self-contained build with a documented
finding."*

So F6 inherited a question with the answer already fenced in, plus one obligation ADR-0002 left
open: making trim regressions visible at merge time rather than at publish time.

## Decision

**Ship trim-clean. Do not attempt Native AOT with EF Core compiled models in this phase.**

1. `IsAotCompatible=true` on `Talent.Domain`, `Talent.Application`, `Talent.Mcp.Tools` and
   `Talent.Mcp.Toolkit`. One property enables the trim, AOT and single-file analyzers and marks the
   assemblies `IsTrimmable`. With the repo-wide `TreatWarningsAsErrors`, a newly introduced
   reflection-based serialization site becomes a **build error**. That is ADR-0002's open
   consequence, discharged on every build rather than once in a publish job.
2. `Talent.Infrastructure` and both hosts are deliberately **excluded**. EF Core is not trim-clean,
   and `TalentInfrastructureServiceCollectionExtensions.BindOptions` has its own `IL2026`/`IL3050`
   site via `configuration.GetSection(...).Get<TalentOptions>()`. Turning the analyzers on there
   produces a wall of warnings that has to be suppressed wholesale, which converts a gate into
   decoration.
3. CI publishes trimmed self-contained and measures it, alongside a framework-dependent baseline, a
   plain self-contained build and a ReadyToRun build.

### Why not compiled models

Not because it is impossible — because the cost is not bounded by anything this phase can see.
Compiled models are the supported route for EF Core, but Npgsql, OpenTelemetry and the OAuth stack
are each separately unverified for AOT here, and ADR-0002 already recorded the lesson that matters:
static analysis and runtime **disagreed in direction** — the configuration that warned was the one
that worked, and the one that failed failed silently. Chasing a native binary through four
unverified dependencies is an open-ended commitment inside a 2–3 day phase whose cut order lists AOT
first.

ReadyToRun is measured alongside because it is the realistic alternative: no trimming, no reflection
risk, and still a large share of the startup win on a process launched once per session. If the
numbers say so, **R2R is the recommendation and AOT stays closed.**

## What the analyzers actually found

Ten sites, not the eight that had been written down by hand — the inventory had already missed
`ToolExecutionTelemetry.cs:68` and `:138`, both calling
`Deserialize<T>(JsonNode, JsonSerializerOptions)`.

The method matters more than the list: **enumerate from the analyzer output, never from a written
inventory.** `Talent.Domain`, `Talent.Application` and `Talent.Mcp.Tools` came back completely clean;
every site was in the toolkit.

| Site | Disposition |
|---|---|
| `Tasks/PostgresMcpTaskStore.cs` ×5 | Fixed — `McpTasksJsonContext.Default.InputRequest` / `.InputResponse` |
| `Tracing/ToolExecutionTelemetry.cs` ×3 | Fixed — `McpJsonUtilities.DefaultOptions.GetTypeInfo<T>()` |
| `HandleCodec.cs:90`, `:176` | **Deferred**, suppressed narrowly at the call site |

Both fixes use SDK surface that is public and carries no trim annotations — verified against the
2.2.0 assemblies rather than assumed. One detail is easy to get wrong: the serialize site in
`ToolExecutionTelemetry` must be typed `IDictionary<string, JsonElement>`, the *declared* type of
`CallToolRequestParams.Arguments`. `Dictionary<,>` is a different contract and the source-generated
resolver has no entry for it.

The task store writes both types into `jsonb`, so byte-identity was **measured, not argued**:
`TaskStoreSerializationTests` compares against the pre-change call and reads back a row written the
old way. Both types declare their own `[JsonConverter]`, so the converter decides the shape and the
options' naming policy never enters into it. A store that cannot read its own history loses exactly
the in-flight work it exists to protect.

## Why `HandleCodec` is deferred rather than fixed

`Mint<TPayload>` and `TryRead<TPayload>` are open generics over payload types the **consumer** owns,
and this assembly ships to NuGet. It cannot enumerate its consumers' types, which is precisely what a
`JsonSerializerContext` requires.

Four options were considered, and the elegant one is blocked by architecture rather than by taste:

- **A payload interface exposing its own `static abstract JsonTypeInfo<T>`.** Impossible here.
  `JobSearchCursor` lives in `Talent.Application`, which is forbidden from referencing
  `Talent.Mcp.Toolkit` — a prohibition enforced by `LayerDependencyRules` and the entire reason the
  `IHandleCodec` port exists. Recorded because it is the design anyone would reach for first.
- **Callers hand the codec bytes.** Removes the `TPayload` that `PayloadTypeMarker<TPayload>()` is
  derived from, moving the marker-to-type binding out of one enforced place and into every call site.
  That marker is what makes a replayed handle detectable. Trading a hardening mechanism for a clean
  analyzer run is the wrong direction.
- **Annotate and propagate `[RequiresUnreferencedCode]`.** `IL2046` forces the attribute onto
  `IHandleCodec`, and from there it climbs through `SignedHandleCodec`, `SearchJobsUseCase`, the
  tools, and both `Program.cs` files. The terminal state is a `Program.cs` marked "this application is
  not trimmable" — in the phase whose deliverable is a trimmed publish. It also edits the same number
  of files as the real fix.
- **Thread a `JsonTypeInfo<TPayload>` parameter through.** The right answer, and a **breaking change
  to a published 1.0.x API** touching four production projects. Sequenced after F6 rather than rushed
  in beside it, so the decision is made with measurements in hand.

The suppression is scoped to the two lines, not the project, so the analyzer stays armed everywhere
else and the exception stays one known exception instead of quietly becoming policy.

Two guardrails make that change safe when it comes:

- **A golden handle vector** — fixed key, fixed clock, fixed payload, exact base64url string. Its
  payload decodes to camelCase, which is `JsonSerializerDefaults.Web`; a source-generated context
  inherits **none** of those settings unless they are declared. Miss them and handles minted before a
  deploy stop reading after it.
- **`TrimPostureRules`**, asserting the four assemblies still declare `IsTrimmable`. Deleting
  `IsAotCompatible` from a `.csproj` is a one-line change that breaks no build and fails no test — the
  analyzers simply stop running and the sites they were catching go quiet again. Verified to bite by
  flipping it to `false`.

## Measuring cold start honestly

Two decisions here matter more than the numbers.

**No environment variable that skips the database.** The host opens two blocking connections before
it serves anything: `CreateAndPrepareTaskStoreAsync` applies the task-store DDL, and `StartAsync`
opens a second connection and awaits its first `LISTEN`. Skipping them would produce a much better
number for a process that cannot answer five of the six tools — exactly the ADR-0002 config-A
mistake, where the thing measured was not the thing shipped. It would also add a production code path
whose only consumer is the benchmark.

Instead `StartupTrace` emits phase markers on stderr (never stdout — that is the JSON-RPC transport)
under `TALENT_STARTUP_TRACE`, so the database share is **reported separately and subtractable**
rather than folded into "runtime init".

**ADR-0002's numbers are retired, not compared against.** Its ~646 ms median and 22.7 MB cannot serve
as the baseline, for three independent reasons: it measured a *minimal* graph with no EF Core, Npgsql
or OpenTelemetry; on a *Windows developer laptop*; against a graph that trimmed cleanly. Diffing a new
figure against it would attribute to trimming a difference in which three variables moved. The
baseline is configuration A below — same runner, same commit, real dependency graph.

Two measurement caveats that would otherwise quietly make the table wrong:

- `Process.StartTime` on Linux comes from `/proc/<pid>/stat` at clock-tick granularity (~10 ms), too
  coarse to headline. Elapsed time is taken externally by the harness.
- Peak RSS is read via `/usr/bin/time -v`, not from inside the process: a process cannot reliably
  observe its own peak, and the peak may occur after the last marker.

`assemblies` is reported as its own column. It is the only figure with no measurement noise at all,
and the one that actually explains what trimming did.

### The functional gate is not optional

Every published configuration is started against live Postgres and must answer `tools/list` with all
six tools **by name** before any timing is recorded. This is the test ADR-0002 argued for: a silently
empty tool set answers `-32601` with no crash and no error log, and a configuration that starts fast
and serves nothing is not a faster configuration. A config failing the gate has its timings struck
through, not published.

This matters specifically for the trimmed build: ILLink can remove entity-type members EF Core
reaches by reflection, and `HandleCodec` is still reflective — so handle-carrying paths are the
likeliest place for a trimmed build to fail at runtime while starting perfectly.

The job never fails on timing. GitHub's 2-vCPU runners are noisy enough that a latency threshold
would be flaky theatre.

### One landmine worth naming

`dotnet publish -p:PublishTrimmed=true` over a graph containing EF Core and Npgsql emits ILLink
warnings; ILLink warnings are MSBuild warnings; and the root `Directory.Build.props` turns warnings
into errors. **The trimmed configuration cannot publish at all** without
`-p:SuppressTrimAnalysisWarnings=true` on the publish command line — never in a `.csproj`, so the
ordinary build stays strict.

The unsuppressed list is archived as the `trim-warnings.txt` CI artifact. Suppressing quietly is
precisely what ADR-0002 warns against, and that list is the raw material for any future decision
about widening trim coverage.

### Results

Produced by the `startup-benchmark` CI job (`scripts/measure-startup.sh`): 12 runs per configuration,
first 2 discarded, median and min–max, all raw samples retained in the JSON artifact.

> **Pending the first run of the job on `main`.** The table is generated by
> `scripts/summarize-startup.py` and pasted here rather than retyped, so the medians in this document
> are the ones the harness computed. Until then this section is deliberately empty rather than
> populated with laptop numbers — the same call ADR-0002 made, for the reason it gave: *"a cold-start
> number in the README has to be reproducible, and one produced by a single machine's toolchain is
> not."*

## Consequences

- `PublishAot` and `PackAsTool` are **mutually exclusive**. If a native binary is ever produced it
  cannot be the `talent-mcp` tool package; it would be a separate distribution channel. Recorded
  because it is not obvious until a publish fails.
- The named next step for widening trim coverage is the configuration-binding source generator for
  `BindOptions`, which is what currently keeps `Talent.Infrastructure` out.
- `HandleCodec`'s API change is queued, breaking, and now guarded by a golden vector. Package versions
  were decoupled in F6 specifically so the library can take that major without dragging the
  `talent-mcp` tool with it.
- Revisit AOT if EF Core compiled models plus Npgsql gain verified AOT support, or if the stdio host's
  dependency graph changes — which would mean revisiting ADR-0004 first.

## Verification

```bash
# The gate: any new reflection-based serialization site is a build error
dotnet build -c Release

# The posture is asserted, not merely configured
dotnet test tests/Talent.Architecture.Tests --filter TrimPostureRules

# The jsonb wire format is unchanged
dotnet test tests/Talent.Mcp.Tests --filter TaskStoreSerialization

# The trimmed publish completes and the binary still serves six tools
./scripts/measure-startup.sh --runs 12 --warmup 2
```
