namespace Talent.Application.UseCases;

using Talent.Application.Configuration;
using Talent.Application.Ports;
using Talent.Domain.Scoring;

/// <summary>Why a scoring request could not be answered.</summary>
public enum ScoreCandidateFitFailure
{
    /// <summary>No failure.</summary>
    None = 0,

    /// <summary>No job with that id exists.</summary>
    JobNotFound = 1,

    /// <summary>No candidate with that id exists.</summary>
    CandidateNotFound = 2,
}

/// <summary>
/// Scores one candidate against one job.
/// <para>
/// Thin by design: fetch through the ports, then call the pure domain scorer. The interesting logic is
/// deliberately not here — it is in <see cref="CandidateFitScorer"/>, where it can be tested without a
/// repository and reused unchanged by A1's eval harness.
/// </para>
/// </summary>
public sealed class ScoreCandidateFitUseCase
{
    private readonly IJobRepository jobs;
    private readonly ICandidateRepository candidates;
    private readonly TalentOptions options;

    /// <summary>Creates the use case.</summary>
    /// <param name="jobs">Job repository port.</param>
    /// <param name="candidates">Candidate repository port.</param>
    /// <param name="options">Tunables, including the scoring weights.</param>
    /// <exception cref="ArgumentNullException">A required dependency was <see langword="null"/>.</exception>
    public ScoreCandidateFitUseCase(
        IJobRepository jobs,
        ICandidateRepository candidates,
        TalentOptions options)
    {
        ArgumentNullException.ThrowIfNull(jobs);
        ArgumentNullException.ThrowIfNull(candidates);
        ArgumentNullException.ThrowIfNull(options);

        this.jobs = jobs;
        this.candidates = candidates;
        this.options = options;
    }

    /// <summary>Scores a candidate against a job.</summary>
    /// <param name="candidateId">The candidate.</param>
    /// <param name="jobId">The job.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>
    /// The explainable score, or the reason it could not be produced. Which id was missing is reported
    /// specifically, because unlike a bad handle this is not adversarial — a caller with a typo needs to
    /// know which of the two ids was wrong.
    /// </returns>
    public async Task<(FitScore? Score, ScoreCandidateFitFailure Failure)> ExecuteAsync(
        Guid candidateId,
        Guid jobId,
        CancellationToken cancellationToken = default)
    {
        var job = await this.jobs.FindByIdAsync(jobId, cancellationToken).ConfigureAwait(false);
        if (job is null)
        {
            return (null, ScoreCandidateFitFailure.JobNotFound);
        }

        var candidate = await this.candidates
            .FindByIdAsync(candidateId, cancellationToken)
            .ConfigureAwait(false);

        if (candidate is null)
        {
            return (null, ScoreCandidateFitFailure.CandidateNotFound);
        }

        return (CandidateFitScorer.Score(candidate, job, this.options.ScoringWeights), ScoreCandidateFitFailure.None);
    }
}
