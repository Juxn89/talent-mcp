namespace Talent.Domain.Constants;

/// <summary>
/// Domain invariants for job postings. Constants are PascalCase: `.editorconfig` enforces
/// `constant_fields_should_be_pascal_case` at error severity, so UPPER_SNAKE_CASE fails the build.
/// </summary>
public static class JobSchema
{
    /// <summary>Maximum length of a job title.</summary>
    public const int MaxTitleLength = 255;

    /// <summary>Minimum length of a job title. Guards against empty or single-character titles.</summary>
    public const int MinTitleLength = 3;

    /// <summary>Maximum length of a job description.</summary>
    public const int MaxDescriptionLength = 20_000;

    /// <summary>Lowest permitted salary figure. Zero means "not disclosed", never "unpaid".</summary>
    public const int MinSalary = 0;

    /// <summary>Highest permitted salary figure, as an annual amount in minor-unit-free currency.</summary>
    public const int MaxSalary = 10_000_000;

    /// <summary>Maximum number of required skills a single posting may list.</summary>
    public const int MaxRequiredSkills = 50;
}
