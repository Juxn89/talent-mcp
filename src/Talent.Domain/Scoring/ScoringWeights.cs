namespace Talent.Domain.Scoring;

/// <summary>
/// Relative importance of each fit-score component.
/// <para>
/// Passed in rather than read from configuration: <c>Talent.Domain</c> has no framework dependencies,
/// so it cannot see <c>IOptions&lt;T&gt;</c>. The Application layer is where a configured value is
/// bound and handed down. That constraint is deliberate — it is what keeps the scorer a pure
/// function, testable without a host.
/// </para>
/// </summary>
/// <param name="SkillOverlap">Weight of how many required skills the candidate covers.</param>
/// <param name="SeniorityDistance">Weight of how close the seniority levels are.</param>
/// <param name="LocationCompatibility">Weight of whether the candidate can work where the job is.</param>
public sealed record ScoringWeights(double SkillOverlap, double SeniorityDistance, double LocationCompatibility)
{
    /// <summary>
    /// The default weighting: skills dominate, seniority matters, location is a tiebreaker.
    /// <para>
    /// 60/25/15 is a judgement call, not a measured optimum, and it is stated here so it can be
    /// argued with rather than discovered in the middle of the scorer. The reasoning: a candidate
    /// missing the required skills is not a fit regardless of anything else; seniority mismatch is
    /// usually negotiable in one direction; and location is close to free to fix when the role allows
    /// remote work. A1's eval harness is the thing that should eventually replace this guess with
    /// evidence.
    /// </para>
    /// </summary>
    public static ScoringWeights Default { get; } = new(0.60, 0.25, 0.15);

    /// <summary>Sum of all weights. <see cref="IsValid"/> requires this to be 1.</summary>
    public double Total => SkillOverlap + SeniorityDistance + LocationCompatibility;

    /// <summary>
    /// Whether the weights are usable: none negative, and summing to 1 within floating-point
    /// tolerance. Weights that do not sum to 1 would make the total score leave the 0–100 range and
    /// quietly break every comparison built on it.
    /// </summary>
    /// <returns><see langword="true"/> when the weights can be used for scoring.</returns>
    public bool IsValid() =>
        SkillOverlap >= 0
        && SeniorityDistance >= 0
        && LocationCompatibility >= 0
        && Math.Abs(Total - 1.0) < 1e-9;
}
