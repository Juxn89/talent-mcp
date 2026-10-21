namespace Talent.Mcp.Tests.Tools;

using Talent.Domain.Enums;
using Talent.Domain.Scoring;
using Talent.Mcp.Tools.Contracts;
using Xunit;

/// <summary>
/// The score breakdown is the explainable part of <c>score_candidate_fit</c>: the part a recruiter, or
/// an agent speaking for one, shows to a person. Found 20 Oct 2026 (#14): a candidate covering 3 of 4
/// skills at weight 0.6 reached the wire as <c>"weightedScore": 0.44999999999999996</c>.
/// </summary>
public sealed class ScoreComponentContractTests
{
    [Fact]
    public void Weighted_score_reaches_the_wire_without_floating_point_noise()
    {
        var component = new ScoreComponent(
            FitScore.SkillOverlapComponent, RawScore: 0.75, Weight: 0.6, ScoreReason.SomeRequiredSkillsMissing, "Covers 3 of 4 required skills.");

        // The domain keeps the exact product, which is what the total is computed from.
        Assert.NotEqual(0.45, component.WeightedScore);

        Assert.Equal(0.45, ScoreComponentContract.From(component).WeightedScore);
    }

    [Fact]
    public void Rounding_keeps_the_declared_precision()
    {
        var component = new ScoreComponent(
            FitScore.SkillOverlapComponent, RawScore: 2.0 / 3.0, Weight: 0.6, ScoreReason.SomeRequiredSkillsMissing, "Covers 2 of 3 required skills.");

        Assert.Equal(0.4, ScoreComponentContract.From(component).WeightedScore);
    }

    [Fact]
    public void Every_other_field_is_carried_over_unchanged()
    {
        var component = new ScoreComponent(
            FitScore.LocationCompatibilityComponent, RawScore: 1, Weight: 0.15, ScoreReason.SameCity, "Both are in Madrid, ES.");

        var contract = ScoreComponentContract.From(component);

        Assert.Equal(component.Name, contract.Name);
        Assert.Equal(component.RawScore, contract.RawScore);
        Assert.Equal(component.Weight, contract.Weight);
        Assert.Equal(component.Reason, contract.Reason);
        Assert.Equal(component.Detail, contract.Detail);
    }
}
