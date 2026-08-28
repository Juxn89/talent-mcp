namespace Talent.Domain.Entities;

using Talent.Domain.Enums;

/// <summary>
/// A canonical skill in the taxonomy. Free text is normalized onto these, which is what makes
/// <c>extract_skills</c> deterministic and free of any LLM call.
/// </summary>
/// <param name="Id">Stable canonical identifier, lower-kebab-case (for example <c>dotnet</c>).</param>
/// <param name="DisplayName">Human-readable name (for example <c>.NET</c>).</param>
/// <param name="Category">Broad grouping used when explaining a score.</param>
public sealed record Skill(string Id, string DisplayName, SkillCategory Category)
{
    /// <summary>Renders the display name.</summary>
    /// <returns>The display name.</returns>
    public override string ToString() => DisplayName;
}
