namespace Talent.Infrastructure.Persistence;

using Microsoft.EntityFrameworkCore;
using Talent.Application.Ports;
using Talent.Domain.Entities;
using Talent.Domain.Enums;

/// <summary>
/// EF Core adapter for <see cref="IJobRepository"/>.
/// </summary>
public sealed class EfJobRepository : IJobRepository
{
    private readonly TalentDbContext context;

    /// <summary>Creates the repository.</summary>
    /// <param name="context">The database context.</param>
    /// <exception cref="ArgumentNullException">The context was <see langword="null"/>.</exception>
    public EfJobRepository(TalentDbContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        this.context = context;
    }

    /// <inheritdoc />
    public async Task<Job?> FindByIdAsync(Guid id, CancellationToken cancellationToken = default) =>
        await this.context.Jobs
            .AsNoTracking()
            .FirstOrDefaultAsync(j => j.Id == id, cancellationToken)
            .ConfigureAwait(false);

    /// <inheritdoc />
    public async Task<JobPage> SearchAsync(
        JobSearchCriteria criteria,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(criteria);

        var query = this.context.Jobs.AsNoTracking().AsQueryable();

        if (!string.IsNullOrWhiteSpace(criteria.Query))
        {
            // EF.Functions.ILike maps to Postgres ILIKE — case-insensitive without the
            // ToLower(column) call that would make the index unusable.
            var pattern = $"%{Escape(criteria.Query.Trim())}%";

            query = query.Where(j =>
                EF.Functions.ILike(j.Title, pattern) || EF.Functions.ILike(j.Description, pattern));
        }

        // Every requested skill must be required by the posting, expressed as one predicate per skill
        // rather than `wanted.All(w => ...)`. The All form reads better but pushes a lambda over a
        // local array into the translator, which is where it either falls back to client evaluation —
        // pulling the whole table back to filter in memory — or fails outright. A chain of
        // `Contains` calls maps cleanly onto Postgres array containment.
        foreach (var required in criteria.RequiredSkillIds.Where(static s => !string.IsNullOrWhiteSpace(s)))
        {
            // Captured into a local: closing over the loop variable directly would give every
            // predicate the last value once the query is finally enumerated.
            var skillId = required.Trim().ToLowerInvariant();

            query = query.Where(j => j.RequiredSkillIds.Contains(skillId));
        }

        if (!string.IsNullOrWhiteSpace(criteria.CountryCode))
        {
            var country = criteria.CountryCode.Trim().ToUpperInvariant();

            query = query.Where(j => j.Location.CountryCode == country);
        }

        if (criteria.Arrangement != WorkArrangement.Unspecified)
        {
            query = query.Where(j => j.Arrangement == criteria.Arrangement);
        }

        var total = await query.CountAsync(cancellationToken).ConfigureAwait(false);

        // Ordered by Title then Id. The Id tiebreaker is not decoration: without a total order, two
        // postings with the same title can swap places between the two queries that make up a page
        // boundary, and a paginated caller would skip one and see the other twice. A signed handle
        // makes the offset trustworthy; only a stable sort makes the offset *mean* something.
        var jobs = await query
            .OrderBy(j => j.Title)
            .ThenBy(j => j.Id)
            .Skip(criteria.Skip)
            .Take(criteria.Take)
            .ToArrayAsync(cancellationToken)
            .ConfigureAwait(false);

        var consumed = criteria.Skip + jobs.Length;
        int? nextSkip = consumed < total ? consumed : null;

        return new JobPage(jobs, total, nextSkip);
    }

    /// <summary>
    /// Escapes the LIKE wildcards so a query containing '%' or '_' searches for those characters
    /// instead of matching everything — the difference between a search box and a full table scan.
    /// </summary>
    private static string Escape(string value) =>
        value.Replace("\\", "\\\\", StringComparison.Ordinal)
             .Replace("%", "\\%", StringComparison.Ordinal)
             .Replace("_", "\\_", StringComparison.Ordinal);
}
