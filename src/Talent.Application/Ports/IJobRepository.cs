namespace Talent.Application.Ports;

using Talent.Domain.Entities;

/// <summary>
/// Read access to job postings. A port: the Application layer declares what it needs and
/// Infrastructure supplies EF Core behind it, so no use case ever sees a <c>DbContext</c>.
/// </summary>
public interface IJobRepository
{
    /// <summary>Fetches one posting by id.</summary>
    /// <param name="id">The job id.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The job, or <see langword="null"/> when it does not exist.</returns>
    Task<Job?> FindByIdAsync(Guid id, CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns a page of postings matching the criteria, ordered stably so a page boundary means the
    /// same thing on every call — which is what makes a signed pagination handle safe to hand out.
    /// </summary>
    /// <param name="criteria">Search criteria.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The matching page.</returns>
    Task<JobPage> SearchAsync(JobSearchCriteria criteria, CancellationToken cancellationToken = default);
}
