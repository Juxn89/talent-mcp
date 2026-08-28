namespace Talent.Infrastructure.Persistence;

using Microsoft.EntityFrameworkCore;
using Talent.Application.Ports;
using Talent.Domain.Entities;
using Talent.Domain.Enums;

/// <summary>
/// EF Core adapter for <see cref="ICandidateRepository"/>.
/// </summary>
public sealed class EfCandidateRepository : ICandidateRepository
{
    private readonly TalentDbContext context;
    private readonly TimeProvider timeProvider;

    /// <summary>Creates the repository.</summary>
    /// <param name="context">The database context.</param>
    /// <param name="timeProvider">
    /// Clock used to stamp a rejection. Injected so the audit timestamp is assertable in a test
    /// instead of being whatever the machine clock said.
    /// </param>
    /// <exception cref="ArgumentNullException">The context was <see langword="null"/>.</exception>
    public EfCandidateRepository(TalentDbContext context, TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(context);

        this.context = context;
        this.timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <inheritdoc />
    public async Task<Candidate?> FindByIdAsync(Guid id, CancellationToken cancellationToken = default) =>
        await this.context.Candidates
            .AsNoTracking()
            .FirstOrDefaultAsync(c => c.Id == id, cancellationToken)
            .ConfigureAwait(false);

    /// <inheritdoc />
    public async Task<IReadOnlyList<Candidate>> FindByIdsAsync(
        IReadOnlyList<Guid> ids,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(ids);

        if (ids.Count == 0)
        {
            return [];
        }

        var wanted = ids.Distinct().ToArray();

        return await this.context.Candidates
            .AsNoTracking()
            .Where(c => wanted.Contains(c.Id))
            .OrderBy(c => c.Id)
            .ToArrayAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<bool> RejectAsync(
        Guid id,
        string reason,
        CancellationToken cancellationToken = default)
    {
        // Tracked, not AsNoTracking: this is the one write path, and the state transition goes through
        // the domain method so the invariant that a rejection carries a reason cannot be bypassed by
        // an UPDATE built here.
        var candidate = await this.context.Candidates
            .FirstOrDefaultAsync(c => c.Id == id, cancellationToken)
            .ConfigureAwait(false);

        if (candidate is null)
        {
            return false;
        }

        if (candidate.Status is CandidateStatus.Rejected)
        {
            // Idempotent by design. The destructive tool runs behind an MRTR confirmation, and a
            // client that retries after a dropped response must not get an error for work that already
            // succeeded — nor have the original reason and timestamp overwritten by the retry.
            return true;
        }

        candidate.Reject(reason, this.timeProvider.GetUtcNow());

        await this.context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        return true;
    }
}
