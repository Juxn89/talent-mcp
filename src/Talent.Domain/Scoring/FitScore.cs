namespace Talent.Domain.Scoring;

using Talent.Domain.Enums;

/// <summary>
/// One component of a fit score, with the reason it landed where it did.
/// </summary>
/// <param name="Name">Component name, stable across versions — tests and clients key off it.</param>
/// <param name="RawScore">The component's own score, 0–1.</param>
/// <param name="Weight">The component's weight in the total, 0–1.</param>
/// <param name="Reason">Why the component scored as it did.</param>
/// <param name="Detail">Human-readable specifics, for example which skills were missing.</param>
public sealed record ScoreComponent(
    string Name,
    double RawScore,
    double Weight,
    ScoreReason Reason,
    string Detail)
{
    /// <summary>The component's contribution to the total, before scaling to 0–100.</summary>
    public double WeightedScore => RawScore * Weight;
}

/// <summary>
/// An explainable candidate-to-job fit score.
/// <para>
/// The per-component breakdown is the point, not a nicety. A bare number cannot be argued with, and
/// A1's eval harness needs to assert on the reasoning rather than only on the total — otherwise two
/// scorers that agree on a number for opposite reasons look identical.
/// </para>
/// </summary>
/// <param name="Total">The overall score, 0–100, rounded to two decimals.</param>
/// <param name="Components">The components that produced the total, in a stable order.</param>
public sealed record FitScore(double Total, IReadOnlyList<ScoreComponent> Components)
{
    /// <summary>Lowest possible total.</summary>
    public const double MinTotal = 0;

    /// <summary>Highest possible total.</summary>
    public const double MaxTotal = 100;

    /// <summary>Component name for the skill-overlap dimension.</summary>
    public const string SkillOverlapComponent = "skill_overlap";

    /// <summary>Component name for the seniority-distance dimension.</summary>
    public const string SeniorityDistanceComponent = "seniority_distance";

    /// <summary>Component name for the location-compatibility dimension.</summary>
    public const string LocationCompatibilityComponent = "location_compatibility";

    /// <summary>Looks up one component by name.</summary>
    /// <param name="name">Component name; use one of the constants on this type.</param>
    /// <returns>The component, or <see langword="null"/> when absent.</returns>
    public ScoreComponent? Component(string name) =>
        Components.FirstOrDefault(c => string.Equals(c.Name, name, StringComparison.Ordinal));
}
