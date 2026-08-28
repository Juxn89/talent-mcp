namespace Talent.Application.Ports;

using Talent.Domain.Scoring;

/// <summary>
/// Scores a whole shortlist. Separate from the pure domain scorer because the bulk path is
/// long-running: it is driven by the MCP Tasks extension with a Postgres-backed store, so it must be
/// able to report progress and survive a container restart.
/// </summary>
public interface IShortlistScorer
{
    /// <summary>Scores every candidate in the shortlist against one job.</summary>
    /// <param name="jobId">The job to score against.</param>
    /// <param name="candidateIds">The shortlisted candidates.</param>
    /// <param name="progress">Receives completion counts so a task can report progress.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>One score per candidate that was found, ordered by descending total.</returns>
    Task<IReadOnlyList<ShortlistEntry>> ScoreAsync(
        Guid jobId,
        IReadOnlyList<Guid> candidateIds,
        IProgress<int>? progress,
        CancellationToken cancellationToken = default);
}

/// <summary>One scored candidate within a shortlist.</summary>
/// <param name="CandidateId">The candidate.</param>
/// <param name="Score">Their explainable fit score.</param>
public sealed record ShortlistEntry(Guid CandidateId, FitScore Score);
