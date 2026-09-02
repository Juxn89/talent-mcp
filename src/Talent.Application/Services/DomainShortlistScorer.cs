namespace Talent.Application.Services;

using Talent.Application.Configuration;
using Talent.Application.Ports;
using Talent.Domain.Scoring;

/// <summary>
/// Scores a shortlist by composing the two repository ports and the pure domain scorer.
/// <para>
/// Lives in Application rather than Infrastructure, unlike <c>IJobRepository</c>'s and
/// <c>ICandidateRepository</c>'s own implementations. It needs no EF Core: everything it touches is
/// either another Application port or <see cref="CandidateFitScorer"/>, so there is nothing for
/// Infrastructure to add. <c>ICandidateRepository.FindByIdsAsync</c> — a batched lookup that already
/// existed for exactly this shape of caller — is what keeps a 500-candidate shortlist to one query
/// instead of 500.
/// </para>
/// </summary>
public sealed class DomainShortlistScorer : IShortlistScorer
{
    private readonly IJobRepository jobs;
    private readonly ICandidateRepository candidates;
    private readonly TalentOptions options;

    /// <summary>Creates the scorer.</summary>
    /// <param name="jobs">Job repository port.</param>
    /// <param name="candidates">Candidate repository port.</param>
    /// <param name="options">Tunables, including the scoring weights.</param>
    /// <exception cref="ArgumentNullException">A required dependency was <see langword="null"/>.</exception>
    public DomainShortlistScorer(IJobRepository jobs, ICandidateRepository candidates, TalentOptions options)
    {
        ArgumentNullException.ThrowIfNull(jobs);
        ArgumentNullException.ThrowIfNull(candidates);
        ArgumentNullException.ThrowIfNull(options);

        this.jobs = jobs;
        this.candidates = candidates;
        this.options = options;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<ShortlistEntry>> ScoreAsync(
        Guid jobId,
        IReadOnlyList<Guid> candidateIds,
        IProgress<int>? progress,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(candidateIds);

        var job = await this.jobs.FindByIdAsync(jobId, cancellationToken).ConfigureAwait(false);
        if (job is null)
        {
            // The interface has no failure channel for a missing job, by design: BulkScoreShortlistUseCase
            // checks existence before ever calling here, which is where "no such job" is reported with an
            // actionable message. A caller that skips that check gets an empty shortlist rather than a
            // crash — a defensive floor, not a supported way to ask "does this job exist".
            return [];
        }

        var found = await this.candidates.FindByIdsAsync(candidateIds, cancellationToken).ConfigureAwait(false);

        var entries = new List<ShortlistEntry>(found.Count);
        var scoredCount = 0;

        foreach (var candidate in found)
        {
            cancellationToken.ThrowIfCancellationRequested();

            entries.Add(new ShortlistEntry(candidate.Id, CandidateFitScorer.Score(candidate, job, this.options.ScoringWeights)));
            scoredCount++;
            progress?.Report(scoredCount);
        }

        return entries.OrderByDescending(static e => e.Score.Total).ToArray();
    }
}
