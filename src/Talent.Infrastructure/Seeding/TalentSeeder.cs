namespace Talent.Infrastructure.Seeding;

using Microsoft.EntityFrameworkCore;

/// <summary>
/// Applies <see cref="SeedData"/> to a database.
/// <para>
/// Kept out of the model rather than using EF's <c>HasData</c>. <c>HasData</c> bakes the rows into a
/// migration, which means editing a seed row requires a new migration, and it cannot handle entities
/// whose state is produced by a domain method — the pre-rejected candidate would have to be written as
/// raw column values, bypassing the invariant that a rejection carries a reason.
/// </para>
/// <para>
/// Idempotent: seeding an already-seeded database is a no-op, so <c>docker compose up</c> on an
/// existing volume does not fail or duplicate.
/// </para>
/// </summary>
public static class TalentSeeder
{
    /// <summary>
    /// Ensures the schema is current and the seed data present.
    /// </summary>
    /// <param name="context">The database context.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>How many jobs and candidates were inserted; zeros when already seeded.</returns>
    /// <exception cref="ArgumentNullException">The context was <see langword="null"/>.</exception>
    public static async Task<(int Jobs, int Candidates)> SeedAsync(
        TalentDbContext context,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);

        await context.Database.MigrateAsync(cancellationToken).ConfigureAwait(false);

        return await SeedWithoutMigratingAsync(context, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Inserts the seed data without touching the schema, for a caller that has already migrated.
    /// </summary>
    /// <param name="context">The database context.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>How many jobs and candidates were inserted.</returns>
    /// <exception cref="ArgumentNullException">The context was <see langword="null"/>.</exception>
    public static async Task<(int Jobs, int Candidates)> SeedWithoutMigratingAsync(
        TalentDbContext context,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);

        var insertedJobs = 0;
        var insertedCandidates = 0;

        // Checked per row by id rather than "is the table empty". A partially-seeded database — an
        // interrupted first run, or a dataset that grew since the last deploy — converges instead of
        // being either skipped entirely or duplicated.
        var existingJobIds = await context.Jobs
            .AsNoTracking()
            .Select(j => j.Id)
            .ToArrayAsync(cancellationToken)
            .ConfigureAwait(false);

        var knownJobs = existingJobIds.ToHashSet();

        foreach (var job in SeedData.CreateJobs().Where(j => !knownJobs.Contains(j.Id)))
        {
            context.Jobs.Add(job);
            insertedJobs++;
        }

        var existingCandidateIds = await context.Candidates
            .AsNoTracking()
            .Select(c => c.Id)
            .ToArrayAsync(cancellationToken)
            .ConfigureAwait(false);

        var knownCandidates = existingCandidateIds.ToHashSet();

        foreach (var candidate in SeedData.CreateCandidates().Where(c => !knownCandidates.Contains(c.Id)))
        {
            context.Candidates.Add(candidate);
            insertedCandidates++;
        }

        if (insertedJobs > 0 || insertedCandidates > 0)
        {
            await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }

        return (insertedJobs, insertedCandidates);
    }
}
