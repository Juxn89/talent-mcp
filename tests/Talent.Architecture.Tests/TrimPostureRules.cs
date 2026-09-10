namespace Talent.Architecture.Tests;

using System.Diagnostics.CodeAnalysis;
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
    /// <summary>The four assemblies that are trim-clean and declare it.</summary>
    public static TheoryData<string> TrimmableAssemblies =>
        new("Talent.Domain", "Talent.Application", "Talent.Mcp.Tools", "Talent.Mcp.Toolkit");

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
    public void The_toolkit_annotates_its_reflective_overloads_rather_than_suppressing_them()
    {
        // Talent.Mcp.Toolkit spent part of F6 deliberately NOT claiming IsTrimmable, because its
        // HandleCodec still serialized reflectively behind a #pragma — and a suppression hides the
        // warning from consumers trimming their own applications, not only from this build. Claiming
        // trim-safety while silencing the evidence against it is the silent-failure mode ADR-0002
        // exists to prevent.
        //
        // The claim is honest now because the remaining reflective paths are [Obsolete] overloads
        // carrying [RequiresUnreferencedCode], which warns the caller instead of hiding from them.
        // This asserts that shape holds: if someone deletes the annotations to quiet a warning, the
        // assembly would go back to over-claiming and this fails.
        var reflectiveOverloads = typeof(HandleCodec)
            .GetMethods()
            .Where(m => m.Name is "Mint" or "TryRead")
            .Where(m => m.GetCustomAttributes(typeof(ObsoleteAttribute), inherit: false).Length > 0)
            .ToArray();

        Assert.NotEmpty(reflectiveOverloads);
        Assert.All(reflectiveOverloads, method =>
        {
            Assert.True(
                method.GetCustomAttributes(typeof(RequiresUnreferencedCodeAttribute), false).Length > 0,
                $"{method.Name}'s obsolete overload must carry [RequiresUnreferencedCode]: the assembly "
                + "declares itself trimmable, so every reflective path has to warn its caller.");
            Assert.True(
                method.GetCustomAttributes(typeof(RequiresDynamicCodeAttribute), false).Length > 0,
                $"{method.Name}'s obsolete overload must carry [RequiresDynamicCode].");
        });
    }
}
