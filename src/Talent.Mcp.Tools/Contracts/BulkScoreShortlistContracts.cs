namespace Talent.Mcp.Tools.Contracts;

using Talent.Application.Ports;
using Talent.Application.UseCases;

/// <summary>One candidate's score within a bulk shortlist run.</summary>
/// <param name="CandidateId">The candidate.</param>
/// <param name="Total">The 0–100 total.</param>
/// <param name="Components">The per-component breakdown — the same shape <c>score_candidate_fit</c> returns.</param>
public sealed record ShortlistScoreEntryContract(
    Guid CandidateId,
    double Total,
    IReadOnlyList<ScoreComponentContract> Components)
{
    /// <summary>Projects a domain shortlist entry onto the wire shape.</summary>
    /// <param name="entry">The domain entry.</param>
    /// <returns>The contract.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="entry"/> was <see langword="null"/>.</exception>
    public static ShortlistScoreEntryContract From(ShortlistEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);

        var components = entry.Score.Components
            .Select(static c => new ScoreComponentContract(c.Name, c.RawScore, c.Weight, c.WeightedScore, c.Reason, c.Detail))
            .ToArray();

        return new ShortlistScoreEntryContract(entry.CandidateId, entry.Score.Total, components);
    }
}

/// <summary>The result of scoring a shortlist against one job.</summary>
/// <param name="JobId">The job scored against.</param>
/// <param name="Entries">
/// One score per candidate that was found, ordered by descending <see cref="ShortlistScoreEntryContract.Total"/> —
/// the highest-fit candidate first.
/// </param>
/// <param name="UnmatchedCandidateIds">
/// Requested ids that did not resolve to a candidate. Empty, not absent, when every id matched — the
/// caller does not have to distinguish "no unmatched ids" from "the field was omitted".
/// </param>
/// <param name="RequestedCount">How many distinct candidate ids were requested.</param>
/// <param name="ScoredCount">How many were actually scored. Less than <paramref name="RequestedCount"/> exactly when <paramref name="UnmatchedCandidateIds"/> is non-empty.</param>
public sealed record BulkScoreShortlistResponse(
    Guid JobId,
    IReadOnlyList<ShortlistScoreEntryContract> Entries,
    IReadOnlyList<Guid> UnmatchedCandidateIds,
    int RequestedCount,
    int ScoredCount)
{
    /// <summary>Projects a domain result onto the wire shape.</summary>
    /// <param name="jobId">The job scored against.</param>
    /// <param name="result">The domain result.</param>
    /// <returns>The contract.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="result"/> was <see langword="null"/>.</exception>
    public static BulkScoreShortlistResponse From(Guid jobId, BulkScoreShortlistResult result)
    {
        ArgumentNullException.ThrowIfNull(result);

        return new BulkScoreShortlistResponse(
            jobId,
            result.Entries.Select(ShortlistScoreEntryContract.From).ToArray(),
            result.UnmatchedCandidateIds,
            result.Entries.Count + result.UnmatchedCandidateIds.Count,
            result.Entries.Count);
    }
}
