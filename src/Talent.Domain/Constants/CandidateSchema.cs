namespace Talent.Domain.Constants;

/// <summary>Domain invariants for candidate profiles.</summary>
public static class CandidateSchema
{
    /// <summary>Maximum length of a candidate's full name.</summary>
    public const int MaxFullNameLength = 200;

    /// <summary>Lowest permitted years of professional experience.</summary>
    public const int MinExperienceYears = 0;

    /// <summary>
    /// Highest permitted years of professional experience. A profile claiming more is a data error,
    /// not an unusually long career.
    /// </summary>
    public const int MaxExperienceYears = 50;

    /// <summary>Maximum number of skills a single profile may list.</summary>
    public const int MaxSkills = 200;
}
