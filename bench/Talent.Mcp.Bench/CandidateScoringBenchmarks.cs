namespace Talent.Mcp.Bench;

using BenchmarkDotNet.Attributes;
using Talent.Domain.Entities;
using Talent.Domain.Enums;
using Talent.Domain.Scoring;
using Talent.Domain.ValueObjects;

/// <summary>
/// Measures <see cref="CandidateFitScorer.Score(Candidate, Job)"/>, the deterministic scorer the
/// plan names directly, both for one pair and across a full shortlist.
/// <para>
/// A single score is expected to be cheap and flat. The number that matters is the shortlist one:
/// <c>bulk_score_shortlist</c> runs this per candidate up to
/// <c>TalentOptions.MaxShortlistSize</c> (500), and that is the path that runs as an MCP task
/// because it was assumed to be slow enough to need one. This measures whether that assumption
/// holds.
/// </para>
/// <para>
/// The shortlist case loops the scorer directly rather than going through
/// <c>DomainShortlistScorer</c>: the service needs repositories, and a benchmark that starts a
/// database measures the database. The per-candidate work is identical either way.
/// </para>
/// </summary>
[MemoryDiagnoser]
public class CandidateScoringBenchmarks
{
    /// <summary>
    /// The shortlist ceiling from TalentOptions.MaxShortlistSize, plus two smaller points so the
    /// shape between them is visible rather than inferred from a single measurement.
    /// </summary>
    [Params(1, 50, 500)]
    public int ShortlistSize { get; set; } = 500;

    private Job job = null!;
    private Candidate[] candidates = null!;

    [GlobalSetup]
    public void Setup()
    {
        this.job = new Job(
            Guid.Parse("11111111-1111-1111-1111-111111111111"),
            "Senior Backend Engineer",
            "Distributed services in .NET on PostgreSQL.",
            ["csharp", "dotnet", "aspnet-core", "postgresql", "docker", "kubernetes"],
            SeniorityLevel.Senior,
            new Location("Madrid", "ES"),
            WorkArrangement.Hybrid,
            new SalaryRange(60000, 85000, "EUR"));

        // Varied on purpose: a fixed candidate would let branch prediction flatter the seniority and
        // location components, which are the two branches in the scorer.
        string[][] skillSets =
        [
            ["csharp", "dotnet", "aspnet-core", "postgresql"],
            ["java", "spring-boot", "kubernetes"],
            ["typescript", "react", "nodejs"],
            ["csharp", "dotnet", "docker", "kubernetes", "postgresql", "grpc"],
        ];
        SeniorityLevel[] levels =
            [SeniorityLevel.Junior, SeniorityLevel.Mid, SeniorityLevel.Senior, SeniorityLevel.Staff];

        // Location is constructed per candidate, never shared. A single cached instance handed to
        // many entities is the owned-value-object trap AGENTS.md records; keeping benchmark
        // fixtures in the same shape as production ones stops that habit forming here.
        this.candidates = [.. Enumerable.Range(0, this.ShortlistSize).Select(i => new Candidate(
            Guid.NewGuid(),
            $"Candidate {i}",
            skillSets[i % skillSets.Length],
            yearsOfExperience: 3 + (i % 12),
            levels[i % levels.Length],
            i % 3 == 0 ? new Location("Madrid", "ES") : new Location("Lisbon", "PT"),
            willingToRelocate: i % 2 == 0))];
    }

    [Benchmark(Baseline = true)]
    public FitScore ScoreOne() => CandidateFitScorer.Score(this.candidates[0], this.job);

    [Benchmark]
    public double ScoreShortlist()
    {
        var total = 0d;
        foreach (var candidate in this.candidates)
        {
            total += CandidateFitScorer.Score(candidate, this.job).Total;
        }

        return total;
    }
}
