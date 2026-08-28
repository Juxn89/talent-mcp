namespace Talent.Architecture.Tests;

using ArchUnitNET.Domain;
using ArchUnitNET.Fluent;
using ArchUnitNET.Loader;
using ArchUnitNET.xUnit;
using Xunit;
using static ArchUnitNET.Fluent.ArchRuleDefinition;

/// <summary>
/// Enforces the inter-layer half of the Clean Architecture dependency rule with ArchUnitNET.
/// <para>
/// Written in F1, before there is code to violate it. A dependency rule added once the code exists is
/// negotiated, not enforced — someone always has a reason why their particular reference is fine. The
/// plan already made the mistake this guards against: an earlier revision put EF Core inside
/// <c>Talent.Domain</c>.
/// </para>
/// <para>
/// <strong>Scope: layer-to-layer only.</strong> Every rule here compares two of this repo's own
/// assemblies, so both sides are loaded into the architecture and ArchUnitNET can evaluate them
/// positively. They still need <c>WithoutRequiringPositiveResults()</c> — that is inherent to a
/// negative rule: when nobody depends on the target there is no positive result to report. The
/// difference from a namespace rule is that here the target types ARE loaded, so a genuine violation
/// is detected. Verified by injecting one. Rules about <em>framework</em>
/// namespaces are deliberately NOT here; see <see cref="ForbiddenAssemblyReferences"/> for why the
/// ArchUnitNET form of those silently passes.
/// </para>
/// </summary>
public sealed class LayerDependencyRules
{
    // Assembly objects, NOT names. ArchUnitNET 0.13.4 only offers ResideInAssembly(string fullName)
    // and ResideInAssembly(Assembly, params Assembly[]) — and the string overload matches the
    // assembly's FULL name. Passing the simple name "Talent.Domain" matches nothing, which leaves
    // both sides of every rule empty and makes the whole suite pass vacuously. Verified on
    // 27 Aug 2026 by injecting a Toolkit -> Domain reference: the name-based rules reported green.
    private static readonly System.Reflection.Assembly DomainAssembly = typeof(Domain.Entities.Job).Assembly;
    private static readonly System.Reflection.Assembly ApplicationAssembly = typeof(Application.Ports.IJobRepository).Assembly;
    private static readonly System.Reflection.Assembly InfrastructureAssembly = typeof(Infrastructure.TalentDbContext).Assembly;
    private static readonly System.Reflection.Assembly ToolkitAssembly = typeof(Mcp.Toolkit.HandleCodec).Assembly;

    private static readonly Architecture Architecture = new ArchLoader()
        .LoadAssemblies(DomainAssembly, ApplicationAssembly, InfrastructureAssembly, ToolkitAssembly)
        .Build();

    private static readonly IObjectProvider<IType> DomainLayer =
        Types().That().ResideInAssembly(DomainAssembly).As("Talent.Domain");

    private static readonly IObjectProvider<IType> ApplicationLayer =
        Types().That().ResideInAssembly(ApplicationAssembly).As("Talent.Application");

    private static readonly IObjectProvider<IType> InfrastructureLayer =
        Types().That().ResideInAssembly(InfrastructureAssembly).As("Talent.Infrastructure");

    private static readonly IObjectProvider<IType> ToolkitLayer =
        Types().That().ResideInAssembly(ToolkitAssembly).As("Talent.Mcp.Toolkit");

    /// <summary>
    /// The loaded architecture, exposed so <see cref="LayersAreNotEmpty"/> can assert that every layer
    /// actually contributed types.
    /// </summary>
    internal static Architecture LoadedArchitecture => Architecture;

    [Fact]
    public void Domain_does_not_depend_on_any_other_layer()
    {
        Types().That().Are(DomainLayer)
            .Should().NotDependOnAny(ApplicationLayer)
            .AndShould().NotDependOnAny(InfrastructureLayer)
            .AndShould().NotDependOnAny(ToolkitLayer)
            .Because("dependencies point inward, and nothing is further in than the domain.")
            .WithoutRequiringPositiveResults()
            .Check(Architecture);
    }

    [Fact]
    public void Application_does_not_reach_outward()
    {
        Types().That().Are(ApplicationLayer)
            .Should().NotDependOnAny(InfrastructureLayer)
            .AndShould().NotDependOnAny(ToolkitLayer)
            .Because(
                "the Application layer declares ports and lets Infrastructure implement them. "
                + "Reaching for an adapter directly inverts the dependency the ports exist to create.")
            .WithoutRequiringPositiveResults()
            .Check(Architecture);
    }

    [Fact]
    public void Toolkit_is_domain_agnostic()
    {
        Types().That().Are(ToolkitLayer)
            .Should().NotDependOnAny(DomainLayer)
            .AndShould().NotDependOnAny(ApplicationLayer)
            .AndShould().NotDependOnAny(InfrastructureLayer)
            .Because(
                "Talent.Mcp.Toolkit ships to NuGet as reusable protocol primitives. A single "
                + "recruitment concept leaking into it makes it a private library with a public name. "
                + "That is why HandleCodec implements no application interface and Infrastructure "
                + "supplies the SignedHandleCodec adapter instead.")
            .WithoutRequiringPositiveResults()
            .Check(Architecture);
    }
}
