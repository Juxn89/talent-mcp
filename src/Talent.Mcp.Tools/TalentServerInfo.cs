namespace Talent.Mcp.Tools;

using System.Reflection;
using ModelContextProtocol.Protocol;
using Talent.Mcp.Tools.Constants;

/// <summary>
/// The identity this server reports in <c>server/discover</c> and in the client handshake.
/// </summary>
public static class TalentServerInfo
{
    /// <summary>
    /// Server name and version.
    /// <para>
    /// The version is read from the assembly rather than declared as a constant, so it cannot drift
    /// from the <c>.csproj</c>. It is read from <em>this</em> assembly, not the entry assembly:
    /// both hosts are versioned together, and under a test runner the entry assembly is the runner,
    /// which would make the conformance suite assert a version no release ever had.
    /// </para>
    /// </summary>
    public static Implementation Value { get; } = new()
    {
        Name = Mcp.ServerName,
        Title = "Talent MCP",
        Version = ReadVersion(),
        Description = "Recruitment domain tools: job search, skill normalization and explainable candidate-fit scoring.",
    };

    private static string ReadVersion()
    {
        var assembly = typeof(TalentServerInfo).Assembly;

        var informational = assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()
            ?.InformationalVersion;

        if (string.IsNullOrWhiteSpace(informational))
        {
            return assembly.GetName().Version?.ToString() ?? "0.0.0";
        }

        // Strip the source-revision suffix the SDK appends ("1.0.0+<sha>"). The commit belongs in
        // build metadata, not in a version a client displays.
        var plus = informational.IndexOf('+', StringComparison.Ordinal);
        return plus < 0 ? informational : informational[..plus];
    }
}
