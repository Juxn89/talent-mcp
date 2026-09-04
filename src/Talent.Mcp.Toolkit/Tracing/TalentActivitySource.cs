namespace Talent.Mcp.Toolkit.Tracing;

using System.Diagnostics;
using System.Reflection;

/// <summary>
/// The single <see cref="ActivitySource"/> every tool-execution span comes from.
/// <para>
/// One source shared by both hosts (registered once via <c>AddTalentTelemetry</c>, per host, with
/// <c>AddSource(Name)</c>), so a Jaeger query for this name finds spans regardless of which host
/// produced them.
/// </para>
/// </summary>
public static class TalentActivitySource
{
    /// <summary>The source name both hosts register with <c>TracerProviderBuilder.AddSource</c>.</summary>
    public const string Name = "Talent.Mcp";

    /// <summary>The shared source instance. Every caller null-checks the result of starting from it.</summary>
    public static ActivitySource Instance { get; } = new(Name, ReadVersion());

    private static string ReadVersion()
    {
        var assembly = typeof(TalentActivitySource).Assembly;

        var informational = assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()
            ?.InformationalVersion;

        if (string.IsNullOrWhiteSpace(informational))
        {
            return assembly.GetName().Version?.ToString() ?? "0.0.0";
        }

        // Strip the source-revision suffix the SDK appends ("1.0.0+<sha>") — same reasoning as
        // TalentServerInfo.ReadVersion in Talent.Mcp.Tools.
        var plus = informational.IndexOf('+', StringComparison.Ordinal);
        return plus < 0 ? informational : informational[..plus];
    }
}
