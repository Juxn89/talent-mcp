namespace Talent.Domain.Enums;

/// <summary>
/// Broad grouping for a skill in the taxonomy. Used to explain a fit score: two candidates with the
/// same overlap count are not equivalent if one matches the job's primary category and the other
/// does not.
/// </summary>
public enum SkillCategory
{
    /// <summary>Not classified. Present so a default-constructed value is never a real category.</summary>
    Unknown = 0,

    /// <summary>Server-side languages, frameworks and runtimes.</summary>
    Backend = 1,

    /// <summary>Browser-side languages, frameworks and styling.</summary>
    Frontend = 2,

    /// <summary>Databases, query languages, pipelines and analytics.</summary>
    Data = 3,

    /// <summary>CI/CD, containers, orchestration and infrastructure as code.</summary>
    DevOps = 4,

    /// <summary>Cloud platforms and their managed services.</summary>
    Cloud = 5,

    /// <summary>Native and cross-platform mobile development.</summary>
    Mobile = 6,

    /// <summary>Automated testing, quality engineering and performance work.</summary>
    Testing = 7,

    /// <summary>Application, infrastructure and identity security.</summary>
    Security = 8,

    /// <summary>Non-technical skills: communication, mentoring, product sense.</summary>
    Soft = 9,
}
