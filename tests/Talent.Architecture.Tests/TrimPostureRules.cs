namespace Talent.Architecture.Tests;

using System.Reflection;
using Talent.Mcp.Toolkit;
using Xunit;

/// <summary>
/// Pins which assemblies declare themselves trim-safe — and, just as deliberately, which one does
/// not.
/// <para>
/// F6 armed the trim, AOT and single-file analyzers on the four inner projects. Combined with the
/// repo-wide <c>TreatWarningsAsErrors</c>, that is what turns a newly introduced reflection-based
/// serialization site into a build error rather than a warning nobody reads — the gate
/// <see href="../../docs/adr/0002-native-aot-and-explicit-tool-registration.md">ADR-0002</see> asked
/// CI to hold.
/// </para>
/// <para>
/// Both halves of this need a test, because both are one-line changes to a <c>.csproj</c> that break
/// no build and fail nothing on their own.
/// </para>
/// </summary>
public sealed class TrimPostureRules
{
    /// <summary>
    /// The three assemblies that are genuinely trim-clean and say so.
    /// <para>
    /// <c>Talent.Mcp.Toolkit</c> is absent on purpose — see
    /// <see cref="The_toolkit_does_not_claim_trim_safety_while_HandleCodec_is_reflective"/>.
    /// </para>
    /// </summary>
    public static TheoryData<string> TrimmableAssemblies =>
        new("Talent.Domain", "Talent.Application", "Talent.Mcp.Tools");

    private static IReadOnlyDictionary<string, string?> MetadataOf(Assembly assembly) =>
        assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
            .ToDictionary(a => a.Key, a => a.Value, StringComparer.Ordinal);

    [Theory]
    [MemberData(nameof(TrimmableAssemblies))]
    public void Trim_clean_assemblies_declare_themselves_trimmable(string assemblyName)
    {
        var metadata = MetadataOf(Assembly.Load(assemblyName));

        Assert.True(
            metadata.TryGetValue("IsTrimmable", out var trimmable) && trimmable == "True",
            $"{assemblyName} is no longer marked IsTrimmable. If IsAotCompatible was removed from "
            + "its .csproj, the trim and AOT analyzers stopped running for it and reflection-based "
            + "serialization can be reintroduced without failing the build.");
    }

    [Fact]
    public void The_toolkit_does_not_claim_trim_safety_while_HandleCodec_is_reflective()
    {
        // The asymmetry is the point, so it is asserted rather than left to a comment.
        //
        // Talent.Mcp.Toolkit runs the same analyzers as the other three, but must NOT declare
        // IsTrimmable, because it would be publishing a guarantee it does not keep:
        // HandleCodec.Mint/TryRead are still reflection-based, and the narrow suppression at those
        // two call sites hides the warning from a CONSUMER trimming their own app, not only from our
        // build. Measured 8 Sep 2026: a downstream trimmed publish reports 4 HandleCodec diagnostics
        // without the pragma and 0 with it.
        //
        // Claiming trim-safety while silencing the one signal that contradicts it is exactly the
        // silent-failure mode ADR-0002 exists to prevent. This package is on NuGet, so the audience
        // for that claim is strangers.
        //
        // Restore IsAotCompatible here — and delete this test — when the HandleCodec JsonTypeInfo
        // change lands and the suppression goes away. See ADR-0007.
        var metadata = MetadataOf(typeof(HandleCodec).Assembly);

        Assert.False(
            metadata.ContainsKey("IsTrimmable") || metadata.ContainsKey("IsAotCompatible"),
            "Talent.Mcp.Toolkit now declares itself trim- or AOT-safe. That is only honest once "
            + "HandleCodec no longer serializes reflectively: while the IL2026/IL3050 suppression "
            + "stands, the claim tells consumers the assembly is safe to trim AND hides the warning "
            + "that would tell them otherwise. Fix HandleCodec first — see ADR-0007.");
    }
}
