namespace Talent.Application.Ports;

using Talent.Domain.Enums;

/// <summary>
/// What to search job postings for, plus where in the result set to resume.
/// </summary>
/// <param name="Query">Free-text query matched against title and description. May be empty.</param>
/// <param name="RequiredSkillIds">Canonical skill ids that a posting must require.</param>
/// <param name="CountryCode">ISO country filter, or empty for any country.</param>
/// <param name="Arrangement">Work-arrangement filter, or <see cref="WorkArrangement.Unspecified"/> for any.</param>
/// <param name="Skip">How many matches to skip. Carried inside a signed handle, never trusted raw.</param>
/// <param name="Take">Page size.</param>
public sealed record JobSearchCriteria(
    string Query,
    IReadOnlyList<string> RequiredSkillIds,
    string CountryCode,
    WorkArrangement Arrangement,
    int Skip,
    int Take);
