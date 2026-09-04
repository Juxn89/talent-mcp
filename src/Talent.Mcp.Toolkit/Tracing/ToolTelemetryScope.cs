namespace Talent.Mcp.Toolkit.Tracing;

/// <summary>
/// Ambient, per-request accumulator for the <c>db.query_time</c> span tag.
/// <para>
/// A tool's use case calls a repository port it holds by interface — it has no reference to the
/// <see cref="System.Diagnostics.Activity"/> the request filter started, and passing one down through
/// every port would leak a tracing concern into <c>Talent.Application</c>, which must stay
/// framework-free. An <see cref="AsyncLocal{T}"/> lets a repository decorator in
/// <c>Talent.Infrastructure</c> report timing back up to <c>ToolExecutionTelemetry</c> without either
/// side knowing about the other.
/// </para>
/// </summary>
public sealed class ToolTelemetryScope
{
    private static readonly AsyncLocal<ToolTelemetryScope?> Ambient = new();

    private long totalDbQueryTicks;

    /// <summary>The scope for the request currently executing on this async flow, or <see langword="null"/>.</summary>
    public static ToolTelemetryScope? Current => Ambient.Value;

    /// <summary>Total time recorded across every repository call made while this scope was current.</summary>
    public TimeSpan TotalDbQueryTime => TimeSpan.FromTicks(Interlocked.Read(ref totalDbQueryTicks));

    /// <summary>Adds one repository call's elapsed time to the running total.</summary>
    /// <param name="elapsed">How long the call took.</param>
    public void RecordDbQueryTime(TimeSpan elapsed) => Interlocked.Add(ref totalDbQueryTicks, elapsed.Ticks);

    /// <summary>
    /// Makes <paramref name="scope"/> the ambient scope for the duration of the returned
    /// <see cref="IDisposable"/>, restoring whatever was current before.
    /// </summary>
    /// <param name="scope">The scope to install.</param>
    public static IDisposable Push(ToolTelemetryScope scope)
    {
        var previous = Ambient.Value;
        Ambient.Value = scope;
        return new Popper(previous);
    }

    private sealed class Popper(ToolTelemetryScope? previous) : IDisposable
    {
        public void Dispose() => Ambient.Value = previous;
    }
}
