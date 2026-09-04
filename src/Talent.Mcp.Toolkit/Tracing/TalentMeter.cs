namespace Talent.Mcp.Toolkit.Tracing;

using System.Diagnostics.Metrics;

/// <summary>
/// The single <see cref="Meter"/> Talent-specific instruments are created from.
/// <para>
/// Kept separate from <see cref="TalentActivitySource"/> because traces and metrics are registered
/// through different OpenTelemetry builders (<c>AddSource</c> vs <c>AddMeter</c>), even though both
/// share the same logical name. <c>PostgresMcpTaskStore</c> (ADR-0003) creates the in-flight-tasks
/// gauge from this instance, since it already owns the database connection that query needs.
/// </para>
/// </summary>
public static class TalentMeter
{
    /// <summary>The meter name both hosts register with <c>MeterProviderBuilder.AddMeter</c>.</summary>
    public const string Name = "Talent.Mcp";

    /// <summary>The shared meter instance.</summary>
    public static Meter Instance { get; } = new(Name);

    /// <summary>
    /// Wall-clock time for one <c>tools/call</c>, tagged by <c>tool.name</c> — the source for a
    /// "latency per tool" Grafana panel. Deliberately a Talent-owned metric rather than a dependency on
    /// the SDK's own internal GenAI-semconv instrumentation (present in 2.2.0 but undocumented and
    /// unnamed in any public API, found only by reflecting the DLL's string table): a dashboard this
    /// project ships should not rest on an implementation detail that could rename or disappear.
    /// </summary>
    public static Histogram<double> ToolDuration { get; } =
        Instance.CreateHistogram<double>("talent.tool.duration", unit: "ms");

    /// <summary>
    /// Count of <c>tools/call</c> results that were an error — a business error (<c>IsError: true</c>),
    /// a protocol-level <c>JsonRpcError</c>, or an unhandled exception — tagged by <c>tool.name</c>. The
    /// source for a "tasa de error" Grafana panel.
    /// </summary>
    public static Counter<long> ToolErrors { get; } = Instance.CreateCounter<long>("talent.tool.errors");
}
