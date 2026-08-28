namespace Talent.Application.Ports;

using Talent.Domain.Entities;

/// <summary>Read and write access to candidate profiles.</summary>
public interface ICandidateRepository
{
    /// <summary>Fetches one profile by id.</summary>
    /// <param name="id">The candidate id.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The candidate, or <see langword="null"/> when they do not exist.</returns>
    Task<Candidate?> FindByIdAsync(Guid id, CancellationToken cancellationToken = default);

    /// <summary>Fetches several profiles by id, skipping ids that do not exist.</summary>
    /// <param name="ids">The candidate ids.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The candidates that were found, ordered by id.</returns>
    Task<IReadOnlyList<Candidate>> FindByIdsAsync(
        IReadOnlyList<Guid> ids,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Records a rejection. Destructive, which is why the tool exposing it requires both the
    /// <c>talent.candidates.reject</c> scope and an MRTR confirmation round-trip.
    /// </summary>
    /// <param name="id">The candidate id.</param>
    /// <param name="reason">Why the candidate was rejected. Required, and stored for audit.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns><see langword="true"/> when a profile was rejected, <see langword="false"/> when absent.</returns>
    Task<bool> RejectAsync(Guid id, string reason, CancellationToken cancellationToken = default);
}
