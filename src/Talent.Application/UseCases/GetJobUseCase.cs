namespace Talent.Application.UseCases;

using Talent.Application.Ports;
using Talent.Domain.Entities;
using Talent.Domain.ValueObjects;

/// <summary>Why a job read could not be answered.</summary>
public enum GetJobFailure
{
    /// <summary>No failure.</summary>
    None = 0,

    /// <summary>No posting with that id exists.</summary>
    JobNotFound = 1,

    /// <summary>
    /// The posting exists but belongs to a different region than the caller asked for.
    /// <para>
    /// Reported separately from <see cref="JobNotFound"/> because it is not adversarial: a recruiter
    /// routed to the wrong regional catalogue needs to know the posting is real and they are looking in
    /// the wrong place. Contrast with a bad pagination handle, where the four failure causes are
    /// deliberately collapsed into one.
    /// </para>
    /// </summary>
    RegionMismatch = 2,

    /// <summary>The requested region was not a two-letter ISO 3166-1 alpha-2 code.</summary>
    InvalidRegion = 3,
}

/// <summary>
/// Reads one job posting, optionally restricted to a region.
/// <para>
/// Region is a routing concern promoted out of the argument list and into a transport header by the
/// tool layer — the multi-brand, multi-region shape a job board actually has. The rule itself lives
/// here rather than in the tool, because "a posting is only visible in its own region" is a business
/// rule, not a header-parsing detail.
/// </para>
/// </summary>
public sealed class GetJobUseCase
{
    private readonly IJobRepository jobs;

    /// <summary>Creates the use case.</summary>
    /// <param name="jobs">Job repository port.</param>
    /// <exception cref="ArgumentNullException"><paramref name="jobs"/> was <see langword="null"/>.</exception>
    public GetJobUseCase(IJobRepository jobs)
    {
        ArgumentNullException.ThrowIfNull(jobs);
        this.jobs = jobs;
    }

    /// <summary>Reads a posting.</summary>
    /// <param name="jobId">The posting id.</param>
    /// <param name="region">
    /// ISO 3166-1 alpha-2 region the read is served from, or <see langword="null"/>/empty for any
    /// region. An empty region is "no routing applied", not "an unknown region": the header is optional
    /// and its absence must not hide every posting.
    /// </param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The posting, or why it could not be returned.</returns>
    public async Task<(Job? Job, GetJobFailure Failure)> ExecuteAsync(
        Guid jobId,
        string? region = null,
        CancellationToken cancellationToken = default)
    {
        var requestedRegion = (region ?? string.Empty).Trim();
        var isRegionScoped = requestedRegion.Length > 0;

        if (isRegionScoped && requestedRegion.Length != Location.CountryCodeLength)
        {
            return (null, GetJobFailure.InvalidRegion);
        }

        var job = await this.jobs.FindByIdAsync(jobId, cancellationToken).ConfigureAwait(false);
        if (job is null)
        {
            return (null, GetJobFailure.JobNotFound);
        }

        if (!isRegionScoped)
        {
            return (job, GetJobFailure.None);
        }

        // A posting with no country is served in every region rather than none. Location.Unknown is a
        // gap in the data, and hiding a posting because of a missing field would make a seeding
        // omission look like a routing rule.
        var servedHere =
            job.Location.CountryCode.Length == 0
            || string.Equals(job.Location.CountryCode, requestedRegion, StringComparison.OrdinalIgnoreCase);

        return servedHere ? (job, GetJobFailure.None) : (null, GetJobFailure.RegionMismatch);
    }
}
