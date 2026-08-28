namespace Talent.Domain.Entities;

using Talent.Domain.Constants;
using Talent.Domain.Enums;
using Talent.Domain.ValueObjects;

/// <summary>
/// A job posting. A1 reuses this shape when it adds pgvector, so the model is deliberately free of
/// anything specific to how A2 happens to serve it.
/// </summary>
public sealed class Job
{
    /// <summary>Creates a job posting.</summary>
    /// <param name="id">Stable identifier.</param>
    /// <param name="title">Job title.</param>
    /// <param name="description">Free-text description.</param>
    /// <param name="requiredSkillIds">Canonical skill ids the role requires.</param>
    /// <param name="seniority">Seniority the role targets.</param>
    /// <param name="location">Where the role is based.</param>
    /// <param name="arrangement">Whether the role is on site, hybrid or remote.</param>
    /// <param name="salary">Salary band, or <see cref="SalaryRange.NotDisclosed"/>.</param>
    public Job(
        Guid id,
        string title,
        string description,
        IEnumerable<string> requiredSkillIds,
        SeniorityLevel seniority,
        Location location,
        WorkArrangement arrangement,
        SalaryRange salary)
    {
        ArgumentNullException.ThrowIfNull(requiredSkillIds);

        Id = id;
        Title = title ?? string.Empty;
        Description = description ?? string.Empty;
        RequiredSkillIds = requiredSkillIds
            .Where(static s => !string.IsNullOrWhiteSpace(s))
            .Select(static s => s.Trim().ToLowerInvariant())
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        Seniority = seniority;
        Location = location ?? ValueObjects.Location.Unknown;
        Arrangement = arrangement;
        Salary = salary ?? SalaryRange.NotDisclosed;
    }

    /// <summary>Stable identifier.</summary>
    public Guid Id { get; }

    /// <summary>Job title.</summary>
    public string Title { get; }

    /// <summary>Free-text description.</summary>
    public string Description { get; }

    /// <summary>Canonical skill ids the role requires, de-duplicated and lower-cased.</summary>
    public IReadOnlyList<string> RequiredSkillIds { get; }

    /// <summary>Seniority the role targets.</summary>
    public SeniorityLevel Seniority { get; }

    /// <summary>Where the role is based.</summary>
    public Location Location { get; }

    /// <summary>Whether the role is on site, hybrid or remote.</summary>
    public WorkArrangement Arrangement { get; }

    /// <summary>Salary band.</summary>
    public SalaryRange Salary { get; }

    /// <summary>
    /// Whether the posting satisfies the domain invariants in <see cref="JobSchema"/>. Validation
    /// lives here as well as at the presentation boundary: the HTTP layer checks shape, the domain
    /// checks meaning.
    /// </summary>
    /// <returns><see langword="true"/> when the posting can be stored.</returns>
    public bool IsValid() =>
        Id != Guid.Empty
        && Title.Length >= JobSchema.MinTitleLength
        && Title.Length <= JobSchema.MaxTitleLength
        && Description.Length <= JobSchema.MaxDescriptionLength
        && RequiredSkillIds.Count <= JobSchema.MaxRequiredSkills
        && Salary.IsValid();
}
