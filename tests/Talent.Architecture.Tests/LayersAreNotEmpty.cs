namespace Talent.Architecture.Tests;

using Xunit;

/// <summary>
/// Guards the dependency rules against becoming vacuous.
/// <para>
/// Every rule in <see cref="LayerDependencyRules"/> is a negative — "must not depend on" — and uses
/// ArchUnitNET's <c>WithoutRequiringPositiveResults()</c>, which is required because the forbidden
/// namespaces resolve to zero loaded types when nobody violates them. The cost of that relaxation is
/// that an <em>empty</em> layer also satisfies every rule about it.
/// </para>
/// <para>
/// So this exists: if a project is renamed, dropped from the architecture loader, or fails to produce
/// types, these tests fail loudly instead of the suite going green while asserting nothing. It is the
/// same class of problem ADR-0002 found in the AOT spike — the failure that looks like success.
/// </para>
/// </summary>
public sealed class LayersAreNotEmpty
{
    /// <summary>
    /// Minimum type count per layer.
    /// <para>
    /// One, not a larger number. The failure this guards against is a layer that is <em>absent</em> —
    /// renamed, dropped from the loader, or producing nothing — and one type disproves that. A higher
    /// floor would additionally assert that each layer is "big enough", which is not a property worth
    /// asserting: it fails while a layer is legitimately thin and teaches people to bump the number
    /// rather than read the message.
    /// </para>
    /// </summary>
    private const int MinimumTypesPerLayer = 1;

    [Theory]
    [InlineData("Talent.Domain")]
    [InlineData("Talent.Application")]
    [InlineData("Talent.Infrastructure")]
    [InlineData("Talent.Mcp.Toolkit")]
    [InlineData("Talent.Mcp.Tools")]
    public void Layer_is_loaded_and_has_types(string assemblyName)
    {
        var types = LayerDependencyRules.LoadedArchitecture.Types
            .Where(t => string.Equals(t.Assembly.Name, assemblyName, StringComparison.Ordinal))
            .ToArray();

        Assert.True(
            types.Length >= MinimumTypesPerLayer,
            $"Layer '{assemblyName}' contributed {types.Length} types to the architecture, which is "
            + $"below the floor of {MinimumTypesPerLayer}. Either the project was renamed or removed "
            + "from the ArchLoader in LayerDependencyRules, or it produced no types — in both cases "
            + "every dependency rule about this layer is now passing vacuously.");
    }
}
