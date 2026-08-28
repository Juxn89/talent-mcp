namespace Talent.Architecture.Tests;

using System.Reflection;
using Xunit;

/// <summary>
/// Asserts that the inner layers carry no framework dependencies, by reading their compiled assembly
/// references directly.
/// <para>
/// <strong>Why this is not an ArchUnitNET rule.</strong> The obvious form —
/// <c>Types().That().Are(DomainLayer).Should().NotDependOnAny(Types().That().ResideInNamespace("Microsoft.EntityFrameworkCore*"))</c>
/// — <em>silently passes even when the violation is present</em>. ArchUnitNET evaluates rules against
/// the assemblies loaded into the <c>Architecture</c>; EF Core is not one of them, so the forbidden
/// set resolves to zero types and "depends on none of these zero types" is trivially true.
/// ArchUnitNET does flag that as a non-positive evaluation — and the natural fix,
/// <c>WithoutRequiringPositiveResults()</c>, suppresses exactly the signal that was warning you.
/// </para>
/// <para>
/// This was verified rather than assumed on 27 Aug 2026: EF Core was added to
/// <c>Talent.Domain.csproj</c> and a <c>DbContext</c> property was put on a domain entity. The
/// ArchUnitNET namespace rules reported 9 of 9 passing. ArchUnitNET 0.13.4 has no name-pattern
/// overload of <c>NotDependOnAny</c> that would avoid this, so the check is done by reflection
/// instead — which is also faster and has no dependency of its own.
/// </para>
/// <para>
/// Note what this does and does not catch: it reads the references the compiler actually emitted, so
/// an unused <c>PackageReference</c> passes. That is the right boundary — an unused package is not an
/// architecture violation; <em>using</em> one is, and using one always emits a reference.
/// </para>
/// </summary>
public sealed class ForbiddenAssemblyReferences
{
    /// <summary>
    /// Assembly-name prefixes that must not appear in the inner layers. Matched case-insensitively on
    /// the assembly simple name.
    /// </summary>
    private static readonly string[] ForbiddenPrefixes =
    [
        "Microsoft.EntityFrameworkCore",
        "Npgsql",
        "Microsoft.AspNetCore",
        "ModelContextProtocol",
        "Microsoft.Extensions.DependencyInjection",
        "Microsoft.Extensions.Options",
        "Microsoft.Extensions.Hosting",
        "OpenTelemetry",
    ];

    [Fact]
    public void Domain_references_no_framework_assembly()
    {
        AssertNoForbiddenReferences(
            typeof(Domain.Entities.Job).Assembly,
            "Talent.Domain knows nobody: no EF Core, no MCP SDK, no ASP.NET. That is what lets the "
            + "scoring and normalization functions run in milliseconds without Docker and be reused "
            + "as-is by A1.");
    }

    [Fact]
    public void Application_references_no_persistence_or_transport_assembly()
    {
        AssertNoForbiddenReferences(
            typeof(Application.Ports.IJobRepository).Assembly,
            "use cases are written against ports, not against EF Core or HTTP. That is what lets them "
            + "be tested against fakes, including the degradation paths.");
    }

    [Fact]
    public void Toolkit_references_no_domain_assembly()
    {
        var referenced = typeof(Mcp.Toolkit.HandleCodec).Assembly
            .GetReferencedAssemblies()
            .Select(static a => a.Name ?? string.Empty)
            .Where(static name => name.StartsWith("Talent.", StringComparison.OrdinalIgnoreCase))
            .ToArray();

        Assert.True(
            referenced.Length == 0,
            "Talent.Mcp.Toolkit must reference no Talent.* assembly — it ships to NuGet as "
            + "domain-agnostic protocol primitives. Found: " + string.Join(", ", referenced));
    }

    private static void AssertNoForbiddenReferences(Assembly assembly, string because)
    {
        var violations = assembly
            .GetReferencedAssemblies()
            .Select(static a => a.Name ?? string.Empty)
            .Where(static name => !string.IsNullOrEmpty(name))
            .Where(static name => ForbiddenPrefixes.Any(
                prefix => name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(static name => name, StringComparer.Ordinal)
            .ToArray();

        Assert.True(
            violations.Length == 0,
            $"{assembly.GetName().Name} references forbidden assemblies: "
            + $"{string.Join(", ", violations)}. Reason the rule exists: {because}");
    }
}
