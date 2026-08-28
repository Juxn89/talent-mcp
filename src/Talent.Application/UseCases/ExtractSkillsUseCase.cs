namespace Talent.Application.UseCases;

using Talent.Domain.Entities;
using Talent.Domain.Skills;

/// <summary>The outcome of normalizing free text against the skill taxonomy.</summary>
/// <param name="Skills">The canonical skills found, ordered by id.</param>
/// <param name="UnrecognisedNames">
/// Names that were supplied explicitly but are not in the taxonomy. Reported rather than silently
/// dropped: "we ignored COBOL and Fortran" is actionable where a short result list is not.
/// </param>
public sealed record ExtractSkillsResult(
    IReadOnlyList<Skill> Skills,
    IReadOnlyList<string> UnrecognisedNames);

/// <summary>
/// Normalizes free text or an explicit name list onto canonical taxonomy skills.
/// <para>
/// Has no ports at all, and that is the point: the work is a pure function over the domain taxonomy, so
/// this tool needs no database, no API key and no LLM. It is the cheapest tool in the surface and the
/// one whose determinism the conformance suite leans on.
/// </para>
/// </summary>
public sealed class ExtractSkillsUseCase
{
    /// <summary>Extracts skills mentioned anywhere in a block of free text.</summary>
    /// <param name="text">A CV, a job description, or any prose.</param>
    /// <returns>The skills found. Unrecognised names are not reported for free text — every word that
    /// is not a skill would be one.</returns>
    public ExtractSkillsResult FromText(string? text) =>
        new(SkillNormalizer.Extract(text), []);

    /// <summary>Normalizes an already-separated list of skill names.</summary>
    /// <param name="names">Skill names or aliases, as a structured CV or an import file would supply.</param>
    /// <returns>
    /// The recognised skills plus the names that were dropped. Here the unrecognised list is meaningful,
    /// because the caller asserted each entry was a skill.
    /// </returns>
    public ExtractSkillsResult FromNames(IEnumerable<string>? names)
    {
        var materialised = names as IReadOnlyList<string> ?? names?.ToArray() ?? [];

        var skills = SkillNormalizer.NormalizeAll(materialised)
            .Select(SkillTaxonomy.FindById)
            .Where(static s => s is not null)
            .Select(static s => s!)
            .ToArray();

        return new ExtractSkillsResult(skills, SkillNormalizer.FindUnrecognised(materialised));
    }
}
