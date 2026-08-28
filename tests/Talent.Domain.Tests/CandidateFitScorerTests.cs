namespace Talent.Domain.Tests;

using Talent.Domain.Entities;
using Talent.Domain.Enums;
using Talent.Domain.Scoring;
using Talent.Domain.ValueObjects;
using Xunit;

/// <summary>
/// Table-driven tests for the explainable fit scorer.
/// <para>
/// These assert on the <em>breakdown and the reasons</em>, not only on the total. Two scorers can
/// agree on a number for opposite reasons, and A1's eval harness is going to compare against this
/// baseline — so the reasoning is part of the contract, not commentary on it.
/// </para>
/// </summary>
public sealed class CandidateFitScorerTests
{
    private static readonly Location Madrid = new("Madrid", "ES");
    private static readonly Location Barcelona = new("Barcelona", "ES");
    private static readonly Location Berlin = new("Berlin", "DE");

    [Fact]
    public void Perfect_match_scores_one_hundred()
    {
        var job = MakeJob(["dotnet", "postgresql"], SeniorityLevel.Senior, Madrid, WorkArrangement.OnSite);
        var candidate = MakeCandidate(["dotnet", "postgresql"], SeniorityLevel.Senior, Madrid);

        var score = CandidateFitScorer.Score(candidate, job);

        Assert.Equal(100, score.Total);
        Assert.Equal(ScoreReason.AllRequiredSkillsCovered, score.Component(FitScore.SkillOverlapComponent)!.Reason);
        Assert.Equal(ScoreReason.SeniorityExactMatch, score.Component(FitScore.SeniorityDistanceComponent)!.Reason);
        Assert.Equal(ScoreReason.SameCity, score.Component(FitScore.LocationCompatibilityComponent)!.Reason);
    }

    [Fact]
    public void Worst_case_scores_zero()
    {
        var job = MakeJob(["dotnet", "postgresql"], SeniorityLevel.Staff, Madrid, WorkArrangement.OnSite);
        var candidate = MakeCandidate(["cobol-not-in-taxonomy"], SeniorityLevel.Junior, Berlin, willingToRelocate: false);

        var score = CandidateFitScorer.Score(candidate, job);

        Assert.Equal(0, score.Total);
        Assert.Equal(ScoreReason.NoRequiredSkillsCovered, score.Component(FitScore.SkillOverlapComponent)!.Reason);
        Assert.Equal(ScoreReason.CandidateUnderqualified, score.Component(FitScore.SeniorityDistanceComponent)!.Reason);
        Assert.Equal(ScoreReason.DifferentCountryNoRelocation, score.Component(FitScore.LocationCompatibilityComponent)!.Reason);
    }

    [Theory]
    // Skills are 60% of the total; the other two components are held at a perfect 1.0 here, so the
    // expected total is 40 + 60 * (matched / required).
    [InlineData(new[] { "dotnet", "postgresql", "docker" }, 100.0, ScoreReason.AllRequiredSkillsCovered)]
    [InlineData(new[] { "dotnet", "postgresql" }, 80.0, ScoreReason.SomeRequiredSkillsMissing)]
    [InlineData(new[] { "dotnet" }, 60.0, ScoreReason.SomeRequiredSkillsMissing)]
    [InlineData(new string[0], 40.0, ScoreReason.NoRequiredSkillsCovered)]
    public void Skill_overlap_scales_linearly_with_coverage(
        string[] candidateSkills, double expectedTotal, ScoreReason expectedReason)
    {
        var job = MakeJob(["dotnet", "postgresql", "docker"], SeniorityLevel.Senior, Madrid, WorkArrangement.Remote);
        var candidate = MakeCandidate(candidateSkills, SeniorityLevel.Senior, Madrid);

        var score = CandidateFitScorer.Score(candidate, job);

        Assert.Equal(expectedTotal, score.Total);
        Assert.Equal(expectedReason, score.Component(FitScore.SkillOverlapComponent)!.Reason);
    }

    [Theory]
    // Seniority is 25%. Under-qualification costs 1/3 per step; over-qualification only 0.15 per step,
    // because a senior person doing a mid-level job is a much smaller risk than the reverse.
    [InlineData(SeniorityLevel.Senior, SeniorityLevel.Senior, 1.00, ScoreReason.SeniorityExactMatch)]
    [InlineData(SeniorityLevel.Staff, SeniorityLevel.Senior, 0.85, ScoreReason.CandidateOverqualified)]
    [InlineData(SeniorityLevel.Principal, SeniorityLevel.Senior, 0.70, ScoreReason.CandidateOverqualified)]
    [InlineData(SeniorityLevel.Mid, SeniorityLevel.Senior, 2.0 / 3.0, ScoreReason.CandidateUnderqualified)]
    [InlineData(SeniorityLevel.Junior, SeniorityLevel.Senior, 1.0 / 3.0, ScoreReason.CandidateUnderqualified)]
    [InlineData(SeniorityLevel.Intern, SeniorityLevel.Senior, 0.00, ScoreReason.CandidateUnderqualified)]
    public void Seniority_penalty_is_asymmetric(
        SeniorityLevel candidateLevel, SeniorityLevel jobLevel, double expectedRaw, ScoreReason expectedReason)
    {
        var job = MakeJob(["dotnet"], jobLevel, Madrid, WorkArrangement.Remote);
        var candidate = MakeCandidate(["dotnet"], candidateLevel, Madrid);

        var component = CandidateFitScorer.Score(candidate, job).Component(FitScore.SeniorityDistanceComponent)!;

        Assert.Equal(expectedRaw, component.RawScore, precision: 6);
        Assert.Equal(expectedReason, component.Reason);
    }

    [Fact]
    public void Seniority_penalty_saturates_beyond_three_steps()
    {
        // Intern vs Principal is five steps; the component floors at zero rather than going negative
        // and dragging the total below the documented 0-100 range.
        var job = MakeJob(["dotnet"], SeniorityLevel.Principal, Madrid, WorkArrangement.Remote);
        var candidate = MakeCandidate(["dotnet"], SeniorityLevel.Intern, Madrid);

        var component = CandidateFitScorer.Score(candidate, job).Component(FitScore.SeniorityDistanceComponent)!;

        Assert.Equal(0.0, component.RawScore);
    }

    [Theory]
    [InlineData(WorkArrangement.Remote, "Berlin", "DE", false, 1.0, ScoreReason.RemoteRoleLocationIrrelevant)]
    [InlineData(WorkArrangement.OnSite, "Madrid", "ES", false, 1.0, ScoreReason.SameCity)]
    [InlineData(WorkArrangement.OnSite, "Barcelona", "ES", false, 0.6, ScoreReason.SameCountry)]
    [InlineData(WorkArrangement.Hybrid, "Barcelona", "ES", false, 0.6, ScoreReason.SameCountry)]
    [InlineData(WorkArrangement.OnSite, "Berlin", "DE", true, 0.4, ScoreReason.DifferentCountryWillRelocate)]
    [InlineData(WorkArrangement.OnSite, "Berlin", "DE", false, 0.0, ScoreReason.DifferentCountryNoRelocation)]
    public void Location_component_reflects_arrangement_and_relocation(
        WorkArrangement arrangement,
        string candidateCity,
        string candidateCountry,
        bool willingToRelocate,
        double expectedRaw,
        ScoreReason expectedReason)
    {
        var job = MakeJob(["dotnet"], SeniorityLevel.Senior, Madrid, arrangement);
        var candidate = MakeCandidate(
            ["dotnet"], SeniorityLevel.Senior, new Location(candidateCity, candidateCountry), willingToRelocate);

        var component = CandidateFitScorer.Score(candidate, job).Component(FitScore.LocationCompatibilityComponent)!;

        Assert.Equal(expectedRaw, component.RawScore, precision: 6);
        Assert.Equal(expectedReason, component.Reason);
    }

    [Fact]
    public void A_job_with_no_required_skills_scores_the_component_neutral()
    {
        // Neither 1 (which would flatter everyone) nor 0 (which would punish the candidate for the
        // posting's omission).
        var job = MakeJob([], SeniorityLevel.Senior, Madrid, WorkArrangement.Remote);
        var candidate = MakeCandidate(["dotnet"], SeniorityLevel.Senior, Madrid);

        var component = CandidateFitScorer.Score(candidate, job).Component(FitScore.SkillOverlapComponent)!;

        Assert.Equal(0.5, component.RawScore);
        Assert.Equal(ScoreReason.NoSkillsRequired, component.Reason);
    }

    [Theory]
    [InlineData(SeniorityLevel.Unspecified, SeniorityLevel.Senior)]
    [InlineData(SeniorityLevel.Senior, SeniorityLevel.Unspecified)]
    [InlineData(SeniorityLevel.Unspecified, SeniorityLevel.Unspecified)]
    public void Unstated_seniority_scores_neutral_rather_than_matching(
        SeniorityLevel candidateLevel, SeniorityLevel jobLevel)
    {
        var job = MakeJob(["dotnet"], jobLevel, Madrid, WorkArrangement.Remote);
        var candidate = MakeCandidate(["dotnet"], candidateLevel, Madrid);

        var component = CandidateFitScorer.Score(candidate, job).Component(FitScore.SeniorityDistanceComponent)!;

        Assert.Equal(0.5, component.RawScore);
        Assert.Equal(ScoreReason.SeniorityUnknown, component.Reason);
    }

    [Fact]
    public void Missing_skills_are_named_in_the_detail_using_display_names()
    {
        var job = MakeJob(["dotnet", "kubernetes"], SeniorityLevel.Senior, Madrid, WorkArrangement.Remote);
        var candidate = MakeCandidate(["dotnet"], SeniorityLevel.Senior, Madrid);

        var detail = CandidateFitScorer.Score(candidate, job).Component(FitScore.SkillOverlapComponent)!.Detail;

        // ".NET"/"Kubernetes", not "dotnet"/"kubernetes": the explanation is read by a recruiter.
        Assert.Contains("Kubernetes", detail, StringComparison.Ordinal);
        Assert.DoesNotContain("kubernetes", detail, StringComparison.Ordinal);
    }

    [Fact]
    public void Score_is_deterministic_and_breakdown_order_is_stable()
    {
        var job = MakeJob(["dotnet", "docker"], SeniorityLevel.Senior, Madrid, WorkArrangement.Hybrid);
        var candidate = MakeCandidate(["dotnet"], SeniorityLevel.Mid, Barcelona);

        var first = CandidateFitScorer.Score(candidate, job);
        var second = CandidateFitScorer.Score(candidate, job);

        Assert.Equal(first.Total, second.Total);
        Assert.Equal(
            first.Components.Select(c => c.Name),
            second.Components.Select(c => c.Name));
        Assert.Equal(
            [FitScore.SkillOverlapComponent, FitScore.SeniorityDistanceComponent, FitScore.LocationCompatibilityComponent],
            first.Components.Select(c => c.Name));
    }

    [Fact]
    public void Total_always_lands_inside_the_documented_range()
    {
        var levels = Enum.GetValues<SeniorityLevel>();
        var arrangements = Enum.GetValues<WorkArrangement>();

        foreach (var candidateLevel in levels)
        {
            foreach (var jobLevel in levels)
            {
                foreach (var arrangement in arrangements)
                {
                    var job = MakeJob(["dotnet", "docker"], jobLevel, Madrid, arrangement);
                    var candidate = MakeCandidate(["dotnet"], candidateLevel, Berlin);

                    var total = CandidateFitScorer.Score(candidate, job).Total;

                    Assert.InRange(total, FitScore.MinTotal, FitScore.MaxTotal);
                }
            }
        }
    }

    [Fact]
    public void Weights_that_do_not_sum_to_one_are_rejected()
    {
        var job = MakeJob(["dotnet"], SeniorityLevel.Senior, Madrid, WorkArrangement.Remote);
        var candidate = MakeCandidate(["dotnet"], SeniorityLevel.Senior, Madrid);
        var broken = new ScoringWeights(0.5, 0.25, 0.15);

        // Weights summing to less than 1 would silently cap the total below 100 and quietly break
        // every comparison built on the score.
        var error = Assert.Throws<ArgumentException>(() => CandidateFitScorer.Score(candidate, job, broken));
        Assert.Equal("weights", error.ParamName);
    }

    [Fact]
    public void Default_weights_are_valid_and_sum_to_one()
    {
        Assert.True(ScoringWeights.Default.IsValid());
        Assert.Equal(1.0, ScoringWeights.Default.Total, precision: 9);
    }

    [Fact]
    public void Null_arguments_are_rejected()
    {
        var job = MakeJob(["dotnet"], SeniorityLevel.Senior, Madrid, WorkArrangement.Remote);
        var candidate = MakeCandidate(["dotnet"], SeniorityLevel.Senior, Madrid);

        Assert.Throws<ArgumentNullException>(() => CandidateFitScorer.Score(null!, job));
        Assert.Throws<ArgumentNullException>(() => CandidateFitScorer.Score(candidate, null!));
        Assert.Throws<ArgumentNullException>(() => CandidateFitScorer.Score(candidate, job, null!));
    }

    private static Job MakeJob(
        string[] requiredSkillIds,
        SeniorityLevel seniority,
        Location location,
        WorkArrangement arrangement) =>
        new(
            Guid.Parse("11111111-1111-1111-1111-111111111111"),
            "Senior Backend Engineer",
            "Build and operate the matching platform.",
            requiredSkillIds,
            seniority,
            location,
            arrangement,
            SalaryRange.NotDisclosed);

    private static Candidate MakeCandidate(
        string[] skillIds,
        SeniorityLevel seniority,
        Location location,
        bool willingToRelocate = false) =>
        new(
            Guid.Parse("22222222-2222-2222-2222-222222222222"),
            "Ada Recruiter",
            skillIds,
            yearsOfExperience: 8,
            seniority,
            location,
            willingToRelocate);
}
