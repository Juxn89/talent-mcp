namespace Talent.Domain.Entities;

using Talent.Domain.Constants;
using Talent.Domain.Enums;
using Talent.Domain.ValueObjects;

/// <summary>
/// A candidate profile. Holds only what scoring needs — no contact details, no CV text beyond the
/// skills already normalized out of it.
/// </summary>
public sealed class Candidate
{
    /// <summary>Creates a candidate profile.</summary>
    /// <param name="id">Stable identifier.</param>
    /// <param name="fullName">Display name.</param>
    /// <param name="skillIds">Canonical skill ids the candidate has.</param>
    /// <param name="yearsOfExperience">Years of professional experience.</param>
    /// <param name="seniority">Self-reported or assessed seniority.</param>
    /// <param name="location">Where the candidate lives.</param>
    /// <param name="willingToRelocate">Whether the candidate would move country for a role.</param>
    public Candidate(
        Guid id,
        string fullName,
        IEnumerable<string> skillIds,
        int yearsOfExperience,
        SeniorityLevel seniority,
        Location location,
        bool willingToRelocate)
    {
        ArgumentNullException.ThrowIfNull(skillIds);

        Id = id;
        FullName = fullName ?? string.Empty;
        SkillIds = skillIds
            .Where(static s => !string.IsNullOrWhiteSpace(s))
            .Select(static s => s.Trim().ToLowerInvariant())
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        YearsOfExperience = yearsOfExperience;
        Seniority = seniority;
        Location = location ?? ValueObjects.Location.Unknown;
        WillingToRelocate = willingToRelocate;
    }

    /// <summary>Stable identifier.</summary>
    public Guid Id { get; }

    /// <summary>Display name.</summary>
    public string FullName { get; }

    /// <summary>Canonical skill ids, de-duplicated and lower-cased.</summary>
    public IReadOnlyList<string> SkillIds { get; }

    /// <summary>Years of professional experience.</summary>
    public int YearsOfExperience { get; }

    /// <summary>Self-reported or assessed seniority.</summary>
    public SeniorityLevel Seniority { get; }

    /// <summary>Where the candidate lives.</summary>
    public Location Location { get; }

    /// <summary>Whether the candidate would move country for a role.</summary>
    public bool WillingToRelocate { get; }

    /// <summary>
    /// Whether the profile satisfies the domain invariants in <see cref="CandidateSchema"/>.
    /// </summary>
    /// <returns><see langword="true"/> when the profile can be stored.</returns>
    public bool IsValid() =>
        Id != Guid.Empty
        && FullName.Length > 0
        && FullName.Length <= CandidateSchema.MaxFullNameLength
        && YearsOfExperience >= CandidateSchema.MinExperienceYears
        && YearsOfExperience <= CandidateSchema.MaxExperienceYears
        && SkillIds.Count <= CandidateSchema.MaxSkills;
}
