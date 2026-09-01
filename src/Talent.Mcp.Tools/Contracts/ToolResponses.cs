namespace Talent.Mcp.Tools.Contracts;

using Talent.Domain.Entities;
using Talent.Domain.Enums;
using Talent.Domain.Scoring;

/// <summary>One page of job postings.</summary>
/// <param name="Jobs">The postings on this page.</param>
/// <param name="TotalMatches">Total matches across all pages.</param>
/// <param name="NextPageHandle">
/// Opaque handle to pass back as <c>pageHandle</c> for the next page, or <see langword="null"/> on the
/// last page. Signed and short-lived: it is not an offset a caller can edit, and it carries the search
/// criteria as well as the position, so page 2 cannot be an offset into a different result set.
/// </param>
/// <param name="HasMore">
/// Whether a next page exists.
/// <para>
/// Not redundant, as it first appears. Measured 1 Sep 2026: the SDK's serializer omits nulls, so on the
/// last page <c>nextPageHandle</c> is <em>absent from the payload</em> rather than present as
/// <c>null</c>. A caller would otherwise have to infer "no more pages" from a missing property, which
/// is a model guessing. This field says it outright.
/// </para>
/// </param>
public sealed record SearchJobsResponse(
    IReadOnlyList<JobSummaryContract> Jobs,
    int TotalMatches,
    string? NextPageHandle,
    bool HasMore);

/// <summary>One job posting, plus which region served it.</summary>
/// <param name="Job">The posting.</param>
/// <param name="ServedRegion">
/// The region this read was served from — the <c>Region</c> header when the caller sent one, otherwise
/// empty for "any region". Echoed back because the result is cached with <c>cacheScope: private</c> and
/// the protocol's cache fields have no <c>Vary</c>: a caller holding two responses needs to be able to
/// tell which region each one describes.
/// </param>
public sealed record GetJobResponse(JobDetailContract Job, string ServedRegion);

/// <summary>A canonical skill from the taxonomy.</summary>
/// <param name="Id">Canonical id, the stable identifier to filter and score with.</param>
/// <param name="DisplayName">Human-readable name.</param>
/// <param name="Category">Broad grouping.</param>
public sealed record SkillContract(string Id, string DisplayName, SkillCategory Category)
{
    /// <summary>Projects a domain skill onto the wire shape.</summary>
    /// <param name="skill">The skill.</param>
    /// <returns>The contract.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="skill"/> was <see langword="null"/>.</exception>
    public static SkillContract From(Skill skill)
    {
        ArgumentNullException.ThrowIfNull(skill);
        return new SkillContract(skill.Id, skill.DisplayName, skill.Category);
    }
}

/// <summary>Skills recognised in free text or in an explicit name list.</summary>
/// <param name="Skills">The canonical skills found, ordered by id.</param>
/// <param name="UnrecognisedNames">
/// Names the caller asserted were skills but the taxonomy does not know. Empty when the input was free
/// text, where every non-skill word would qualify.
/// </param>
public sealed record ExtractSkillsResponse(
    IReadOnlyList<SkillContract> Skills,
    IReadOnlyList<string> UnrecognisedNames);

/// <summary>One weighted component of a fit score.</summary>
/// <param name="Name">Component id, stable across versions.</param>
/// <param name="RawScore">The component's own 0–1 score before weighting.</param>
/// <param name="Weight">Its share of the total.</param>
/// <param name="WeightedScore">The product, so a caller need not multiply to explain the total.</param>
/// <param name="Reason">Machine-readable reason code.</param>
/// <param name="Detail">Human-readable explanation.</param>
public sealed record ScoreComponentContract(
    string Name,
    double RawScore,
    double Weight,
    double WeightedScore,
    ScoreReason Reason,
    string Detail);

/// <summary>An explainable candidate-fit score.</summary>
/// <param name="CandidateId">The candidate scored.</param>
/// <param name="JobId">The job scored against.</param>
/// <param name="Total">The 0–100 total.</param>
/// <param name="Components">
/// The per-component breakdown. The point of the tool: a bare number is not actionable, and A1's eval
/// harness scores the explanation as well as the total.
/// </param>
public sealed record ScoreCandidateFitResponse(
    Guid CandidateId,
    Guid JobId,
    double Total,
    IReadOnlyList<ScoreComponentContract> Components)
{
    /// <summary>Projects a domain score onto the wire shape.</summary>
    /// <param name="candidateId">The candidate scored.</param>
    /// <param name="jobId">The job scored against.</param>
    /// <param name="score">The domain score.</param>
    /// <returns>The contract.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="score"/> was <see langword="null"/>.</exception>
    public static ScoreCandidateFitResponse From(Guid candidateId, Guid jobId, FitScore score)
    {
        ArgumentNullException.ThrowIfNull(score);

        var components = score.Components
            .Select(static c => new ScoreComponentContract(
                c.Name,
                c.RawScore,
                c.Weight,
                c.WeightedScore,
                c.Reason,
                c.Detail))
            .ToArray();

        return new ScoreCandidateFitResponse(candidateId, jobId, score.Total, components);
    }
}
