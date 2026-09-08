namespace Talent.Architecture.Tests;

using System.Reflection;
using Talent.Mcp.Toolkit;
using Xunit;

/// <summary>
/// Asserts that the four inner assemblies still declare themselves trim- and AOT-compatible.
/// <para>
/// F6 turned on <c>IsAotCompatible</c> for <c>Talent.Domain</c>, <c>Talent.Application</c>,
/// <c>Talent.Mcp.Tools</c> and <c>Talent.Mcp.Toolkit</c>. Combined with the repo-wide
/// <c>TreatWarningsAsErrors</c>, that is what makes a newly introduced reflection-based
/// serialization site a build error instead of a warning nobody reads — the gate
/// <see href="../../docs/adr/0002-native-aot-and-explicit-tool-registration.md">ADR-0002</see>
/// asked CI to hold.
/// </para>
/// <para>
/// The property lives in four <c>.csproj</c> files, and deleting one is a one-line change that
/// produces no error and no failing test — the analyzers simply stop running for that project and
/// the reflection sites they were catching become invisible again. This test makes the trim posture
/// a property of the build output rather than of a file nobody re-reads.
/// </para>
/// <para>
/// <c>Talent.Infrastructure</c> and the two hosts are deliberately absent: EF Core is not
/// trim-clean, and <c>BindOptions</c> has its own <c>IL2026</c> site. Turning the analyzers on there
/// would produce noise that has to be suppressed wholesale, which defeats the point. ADR-0007
/// records that as the named next step.
/// </para>
/// </summary>
public sealed class TrimPostureRules
{
    public static TheoryData<string> TrimmableAssemblies => new(
        "Talent.Domain", "Talent.Application", "Talent.Mcp.Tools", "Talent.Mcp.Toolkit");

    [Theory]
    [MemberData(nameof(TrimmableAssemblies))]
    public void Inner_assemblies_declare_themselves_trimmable(string assemblyName)
    {
        // Loaded via a type the test project already references, so this does not depend on the
        // assembly having been touched earlier in the run.
        var assembly = typeof(HandleCodec).Assembly.GetName().Name == assemblyName
            ? typeof(HandleCodec).Assembly
            : Assembly.Load(assemblyName);

        var metadata = assembly
            .GetCustomAttributes<AssemblyMetadataAttribute>()
            .ToDictionary(a => a.Key, a => a.Value, StringComparer.Ordinal);

        Assert.True(
            metadata.TryGetValue("IsTrimmable", out var trimmable) && trimmable == "True",
            $"{assemblyName} is no longer marked IsTrimmable. If IsAotCompatible was removed from "
            + "its .csproj, the trim and AOT analyzers stopped running for it and reflection-based "
            + "serialization can be reintroduced without failing the build.");
    }
}
