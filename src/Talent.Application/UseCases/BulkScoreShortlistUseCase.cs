namespace Talent.Application.UseCases;

using Talent.Application.Configuration;
using Talent.Application.Ports;

/// <summary>Why a bulk scoring request could not be run.</summary>
public enum BulkScoreShortlistFailure
{
    /// <summary>No failure.</summary>
    None = 0,

    /// <summary>No job with that id exists.</summary>
    JobNotFound = 1,

    /// <summary>No candidate ids were supplied.</summary>
    ShortlistEmpty = 2,

    /// <summary>
    /// More candidate ids were supplied than <see cref="TalentOptions.MaxShortlistSize"/> allows.
    /// <para>
    /// Refused rather than truncated. Truncating a page the way <c>search_jobs</c> clamps a page size is
    /// safe because pagination is naturally incremental — the caller gets the rest on the next call. A
    /// shortlist has no "next call": silently scoring the first N and calling it done would produce a
    /// response that looks complete while quietly dropping candidates a recruiter asked about.
    /// </para>
    /// </summary>
    ShortlistTooLarge = 3,
}

/// <summary>The outcome of scoring a shortlist.</summary>
/// <param name="Entries">One score per candidate that was found, ordered by descending total.</param>
/// <param name="UnmatchedCandidateIds">
/// Ids that were requested but did not resolve to a candidate. Reported explicitly rather than silently
/// producing a shorter list than was asked for — the same reasoning as <c>extract_skills</c>'
/// <c>UnrecognisedNames</c>.
/// </param>
public sealed record BulkScoreShortlistResult(
    IReadOnlyList<ShortlistEntry> Entries,
    IReadOnlyList<Guid> UnmatchedCandidateIds);

/// <summary>
/// Scores every candidate in a shortlist against one job.
/// <para>
/// Thin on purpose, matching <see cref="ScoreCandidateFitUseCase"/>: the two validation rules that make
/// the request well-formed live here (cheap and fundamental first — is there anything to score, then
/// is it within the size the server accepts — before the one that costs a round trip: does the job
/// exist), and the actual per-candidate work is <see cref="IShortlistScorer"/>, so it stays reusable
/// outside the tool that calls it.
/// </para>
/// </summary>
public sealed class BulkScoreShortlistUseCase
{
    private readonly IJobRepository jobs;
    private readonly IShortlistScorer scorer;
    private readonly TalentOptions options;

    /// <summary>Creates the use case.</summary>
    /// <param name="jobs">Job repository port.</param>
    /// <param name="scorer">Shortlist scorer port.</param>
    /// <param name="options">Tunables, including the shortlist size cap.</param>
    /// <exception cref="ArgumentNullException">A required dependency was <see langword="null"/>.</exception>
    public BulkScoreShortlistUseCase(IJobRepository jobs, IShortlistScorer scorer, TalentOptions options)
    {
        ArgumentNullException.ThrowIfNull(jobs);
        ArgumentNullException.ThrowIfNull(scorer);
        ArgumentNullException.ThrowIfNull(options);

        this.jobs = jobs;
        this.scorer = scorer;
        this.options = options;
    }

    /// <summary>Scores a shortlist against a job.</summary>
    /// <param name="jobId">The job to score against.</param>
    /// <param name="candidateIds">The shortlisted candidates. Duplicates are ignored.</param>
    /// <param name="progress">Receives running completion counts, or <see langword="null"/> if nobody is listening.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The scored shortlist, or the reason it could not be produced.</returns>
    public async Task<(BulkScoreShortlistResult? Result, BulkScoreShortlistFailure Failure)> ExecuteAsync(
        Guid jobId,
        IReadOnlyList<Guid>? candidateIds,
        IProgress<int>? progress,
        CancellationToken cancellationToken = default)
    {
        var distinctIds = (candidateIds ?? []).Distinct().ToArray();

        if (distinctIds.Length == 0)
        {
            return (null, BulkScoreShortlistFailure.ShortlistEmpty);
        }

        if (distinctIds.Length > this.options.MaxShortlistSize)
        {
            return (null, BulkScoreShortlistFailure.ShortlistTooLarge);
        }

        var job = await this.jobs.FindByIdAsync(jobId, cancellationToken).ConfigureAwait(false);
        if (job is null)
        {
            return (null, BulkScoreShortlistFailure.JobNotFound);
        }

        var entries = await this.scorer
            .ScoreAsync(jobId, distinctIds, progress, cancellationToken)
            .ConfigureAwait(false);

        var scoredIds = entries.Select(static e => e.CandidateId).ToHashSet();
        var unmatched = distinctIds.Where(id => !scoredIds.Contains(id)).ToArray();

        return (new BulkScoreShortlistResult(entries, unmatched), BulkScoreShortlistFailure.None);
    }
}
