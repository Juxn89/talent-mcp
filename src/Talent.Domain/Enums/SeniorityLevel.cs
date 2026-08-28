namespace Talent.Domain.Enums;

/// <summary>
/// Seniority ladder. The numeric values are deliberately consecutive: the scorer measures the
/// distance between two levels by subtracting them, so gaps would silently distort the score.
/// </summary>
public enum SeniorityLevel
{
    /// <summary>Not stated. Treated as a full-penalty distance rather than as a match.</summary>
    Unspecified = 0,

    /// <summary>Internship or apprenticeship.</summary>
    Intern = 1,

    /// <summary>Junior individual contributor.</summary>
    Junior = 2,

    /// <summary>Established individual contributor.</summary>
    Mid = 3,

    /// <summary>Senior individual contributor.</summary>
    Senior = 4,

    /// <summary>Cross-team individual contributor.</summary>
    Staff = 5,

    /// <summary>Organisation-wide technical leadership.</summary>
    Principal = 6,
}
