# Verification · Domain benchmarks — what the deterministic functions actually cost

> **Measured 8 Sep 2026 · Phase F6 · BenchmarkDotNet 0.15.8 · .NET 10.0.11**
> Harness: [`bench/Talent.Mcp.Bench`](../../bench/Talent.Mcp.Bench). Reproduce with
> `dotnet run --project bench/Talent.Mcp.Bench -c Release`.

## The headline: allocations are trustworthy here, timings are not

Two full runs were taken on the same machine and commit. **Every allocation figure was byte-identical
between them. Every timing figure moved by up to 2×.**

| Measurement | Run 1 | Run 2 |
|---|---|---|
| `ScoreOne`, allocated | 1.41 KB | 1.41 KB |
| `ScoreShortlist` @500, allocated | 763.36 KB | 763.36 KB |
| `Extract` ten-page, allocated | 27.11 KB | 27.11 KB |
| `ScoreOne`, mean | 823 ns | 1,513 ns |
| `ScoreShortlist` @500, mean | 556 µs | 1,164 µs |

The cause is in BenchmarkDotNet's own header: `11th Gen Intel Core i7-1165G7 2.80GHz`
**`(Max: 1.69GHz)`** — a laptop sustaining 60% of base clock. Standard deviations run to 27% of the
mean, and run 1 was additionally competing with concurrent builds.

So this record reports **allocation as measurement and timing as order of magnitude.** Allocation is
deterministic for these functions — same inputs, same object graph, no clock and no I/O — which is
exactly why it survives a noisy host and the wall-clock numbers do not. Publishable latency figures
need a quiet, unthrottled machine; that is a deliberate future run, not a by-product of this one.

## Configuration

```
BenchmarkDotNet v0.15.8, Windows 11 (10.0.26200.9168/25H2)
11th Gen Intel Core i7-1165G7 2.80GHz (Max: 1.69GHz), 1 CPU, 8 logical and 4 physical cores
.NET SDK 10.0.400
  [Host] / DefaultJob : .NET 10.0.11, X64 RyuJIT x86-64-v4
```

## `CandidateFitScorer.Score`

| Method | ShortlistSize | Mean | Allocated | Alloc ratio |
|---|---|---:|---:|---:|
| `ScoreOne` | 1 | 1.513 µs | 1.41 KB | 1.00 |
| `ScoreShortlist` | 1 | 1.645 µs | 1.44 KB | 1.02 |
| `ScoreOne` | 50 | 1.002 µs | 1.41 KB | 1.00 |
| `ScoreShortlist` | 50 | 66.01 µs | 76.36 KB | 54.30 |
| `ScoreOne` | 500 | 1.288 µs | 1.41 KB | 1.00 |
| `ScoreShortlist` | 500 | 1,163.55 µs | 763.36 KB | 542.84 |

**Scoring is linear and cheap.** 763.36 KB across 500 candidates is 1.53 KB each, against 1.41 KB for
a single score — so the loop adds no hidden amplification, no shared buffer growth, no quadratic
surprise. `ScoreOne` is flat across all three parameter values, as a pure function of one pair should
be.

### The finding worth acting on

**A full 500-candidate shortlist scores in about a millisecond**, on a throttled laptop, at the
configured `TalentOptions.MaxShortlistSize` ceiling. Run 1 measured 0.56 ms; run 2 measured 1.16 ms.
Either way the order of magnitude is the same, and it is small.

`bulk_score_shortlist` runs on the Tasks extension — with `PostgresMcpTaskStore`, cross-node input
delivery over `LISTEN`/`NOTIFY`, and the whole apparatus of [ADR-0003](../adr/0003-cross-node-task-input-responses.md).
This measurement says that machinery is **not** justified by the scoring. Whatever makes the tool
long-running is the data access around it — loading 500 candidates and a job through the repositories
— not `CandidateFitScorer`.

That is not an argument for removing the task: surviving a restart is a real property, and the tool
demonstrates a capability of the revision on purpose. It is an argument against ever optimizing the
scorer for bulk throughput, and a pointer at where to look if the tool is ever actually slow.

## `SkillNormalizer.Extract`

| Size | Mean | Allocated |
|---|---:|---:|
| short (~40 chars) | 3.975 µs | 1.03 KB |
| one-page (~1.1 KB) | 10.941 µs | 4.44 KB |
| ten-page (~11 KB) | 102.442 µs | 27.11 KB |

**Time scales linearly with text length: 9.4× for 10× the text.** That is the shape the
implementation predicts. Per call it lowercases the whole input and allocates a `bool[]` of the same
length, then walks all ~121 aliases in `SkillTaxonomy.AliasesLongestFirst`, running `IndexOf`
repeatedly across the entire haystack. A long CV pays for every alias in the taxonomy whether or not
it appears — the cost is proportional to alias count times text length.

**Allocation scales sub-linearly: 6.1× for 10× the text.** The two text-proportional allocations grow
with input, but the `HashSet` of matched skills saturates at the taxonomy's ~53 canonical entries no
matter how long the document is.

### What this does and does not justify

At ~100 µs for a ten-page CV, `extract_skills` is nowhere near being a bottleneck for a
request-per-call tool. The linear scan is fine at this scale.

It is worth recording that the shape would not hold up under a materially larger taxonomy or
document — the product of the two is what grows — and that .NET's `SearchValues<string>` exists for
exactly this multi-pattern search. **No optimization is being made here.** F6's job is to measure and
publish, and changing the implementation in the same phase that first measured it would mean shipping
a change whose baseline is one noisy afternoon on a throttled laptop. The number is now on record so
that decision can be made on evidence later.

## Method notes

- Both targets are pure functions over `Talent.Domain`: no repository, no `DbContext`, no Docker.
  That is what makes them reproducible anywhere, and it is the same property that lets the domain
  tests run in milliseconds and lets A1 reuse these functions for its eval harness.
- The shortlist case loops `CandidateFitScorer.Score` directly rather than going through
  `DomainShortlistScorer`. That service needs repositories, and a benchmark that starts a database
  measures the database.
- Candidates in the fixture vary by skill set, seniority and location. A single repeated candidate
  would let branch prediction flatter the seniority and location components, which are the two
  branches in the scorer.
- CI runs the harness with `--job short` on every push. That checks the harness still builds and
  executes; it is not a source of publishable numbers, and a shared 2-vCPU runner would be worse than
  this laptop.

## Related

- [ADR-0007 · Trim-clean over Native AOT](../adr/0007-trim-clean-over-native-aot.md) — the other half
  of F6's measurement work, and why cold start is measured in CI rather than here.
- [ADR-0003 · Cross-node task input responses](../adr/0003-cross-node-task-input-responses.md) — the
  machinery the shortlist finding comments on.
