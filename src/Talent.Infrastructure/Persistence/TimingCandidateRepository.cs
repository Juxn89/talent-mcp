namespace Talent.Infrastructure.Persistence;

using System.Diagnostics;
using Talent.Application.Ports;
using Talent.Domain.Entities;
using Talent.Mcp.Toolkit.Tracing;

/// <summary>
/// Times every <see cref="EfCandidateRepository"/> call and reports it to the ambient
/// <see cref="ToolTelemetryScope"/>, for the <c>db.query_time</c> span tag. See
/// <see cref="TimingJobRepository"/> for why this is a decorator rather than a change to the adapter.
/// </summary>
/// <param name="inner">The real repository.</param>
public sealed class TimingCandidateRepository(ICandidateRepository inner) : ICandidateRepository
{
    /// <inheritdoc />
    public async Task<Candidate?> FindByIdAsync(Guid id, CancellationToken cancellationToken = default)
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
    public async Task<IReadOnlyList<Candidate>> FindByIdsAsync(
        IReadOnlyList<Guid> ids,
        CancellationToken cancellationToken = default)
    {
        var stopwatch = Stopwatch.StartNew();
        try
        {
            return await inner.FindByIdsAsync(ids, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            ToolTelemetryScope.Current?.RecordDbQueryTime(stopwatch.Elapsed);
        }
    }

    /// <inheritdoc />
    public async Task<bool> RejectAsync(Guid id, string reason, CancellationToken cancellationToken = default)
    {
        var stopwatch = Stopwatch.StartNew();
        try
        {
            return await inner.RejectAsync(id, reason, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            ToolTelemetryScope.Current?.RecordDbQueryTime(stopwatch.Elapsed);
        }
    }
}
