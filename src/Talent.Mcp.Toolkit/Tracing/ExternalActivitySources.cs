namespace Talent.Mcp.Toolkit.Tracing;

/// <summary>
/// <see cref="System.Diagnostics.ActivitySource"/> names of libraries that trace themselves, registered by
/// <c>AddTalentTelemetry</c> next to <see cref="TalentActivitySource"/> so their spans join the tool's trace.
/// </summary>
public static class ExternalActivitySources
{
    /// <summary>
    /// Npgsql's own source: one span per command, parented to whatever activity is current — the tool
    /// span — so a trace reaches Postgres for both EF Core queries and the raw commands of
    /// <c>PostgresMcpTaskStore</c>. Built into Npgsql since 8.0, so no instrumentation package is needed;
    /// <c>Npgsql.OpenTelemetry</c>'s <c>AddNpgsql()</c> registers this same name. Verified 21 Oct 2026
    /// against Npgsql 10.0.3, see <c>docs/verification/npgsql-activity-source.md</c>.
    /// </summary>
    public const string Npgsql = "Npgsql";
}
