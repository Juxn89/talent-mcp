namespace Talent.Infrastructure.Persistence;

using System.Diagnostics;
using Talent.Application.Ports;
using Talent.Domain.Entities;
using Talent.Mcp.Toolkit.Tracing;

/// <summary>
/// Times every <see cref="EfJobRepository"/> call and reports it to the ambient
/// <see cref="ToolTelemetryScope"/>, for the <c>db.query_time</c> span tag.
/// <para>
/// A decorator, not a change to <see cref="EfJobRepository"/> itself: the five use cases depend only on
/// <see cref="IJobRepository"/>, so wrapping happens once, here, in the DI registration
/// (<c>TalentInfrastructureServiceCollectionExtensions</c>) rather than in every call site.
/// <see cref="ToolTelemetryScope.Current"/> is <see langword="null"/> outside a tool call (for example
/// in <c>Talent.Infrastructure.Tests</c>, which calls repositories directly) — timing is simply not
/// reported then, rather than thrown away loudly.
/// </para>
/// </summary>
/// <param name="inner">The real repository.</param>
public sealed class TimingJobRepository(IJobRepository inner) : IJobRepository
{
    /// <inheritdoc />
    public async Task<Job?> FindByIdAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var stopwatch = Stopwatch.StartNew();
        try
        {
            return await inner.FindByIdAsync(id, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            ToolTelemetryScope.Current?.RecordDbQueryTime(stopwatch.Elapsed);
        }
    }

    /// <inheritdoc />
    public async Task<JobPage> SearchAsync(JobSearchCriteria criteria, CancellationToken cancellationToken = default)
    {
        var stopwatch = Stopwatch.StartNew();
        try
        {
            return await inner.SearchAsync(criteria, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            ToolTelemetryScope.Current?.RecordDbQueryTime(stopwatch.Elapsed);
        }
    }
}
