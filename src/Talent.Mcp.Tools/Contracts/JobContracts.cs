namespace Talent.Mcp.Tools.Contracts;

using Talent.Domain.Entities;
using Talent.Domain.Enums;

/// <summary>A salary band as it appears on the wire.</summary>
/// <param name="Minimum">Lower bound, or zero when undisclosed.</param>
/// <param name="Maximum">Upper bound, or zero when undisclosed.</param>
/// <param name="CurrencyCode">ISO currency code, empty when undisclosed.</param>
/// <param name="IsDisclosed">
/// Whether the posting states a band at all. Sent explicitly rather than left for the caller to infer
/// from two zeroes: "not disclosed" and "pays nothing" are different facts and a model should not have
/// to guess which one a zero means.
/// </param>
public sealed record SalaryContract(int Minimum, int Maximum, string CurrencyCode, bool IsDisclosed);

/// <summary>
/// A job posting as it appears in a search result: everything needed to decide whether to open it, and
/// no description.
/// <para>
/// Trimming the description is the whole reason <c>get_job</c> exists as a separate tool. A page of
/// twenty postings with full descriptions is a large response for a decision the model makes from the
/// title, the skills and the band.
/// </para>
/// </summary>
/// <param name="Id">Job id.</param>
/// <param name="Title">Job title.</param>
/// <param name="RequiredSkillIds">Canonical taxonomy ids the posting requires.</param>
/// <param name="Seniority">Seniority the posting is pitched at.</param>
/// <param name="City">City, empty when the location is unknown.</param>
/// <param name="CountryCode">ISO country code, empty when the location is unknown.</param>
/// <param name="Arrangement">On-site, hybrid or remote.</param>
/// <param name="Salary">The advertised band.</param>
public sealed record JobSummaryContract(
    Guid Id,
    string Title,
    IReadOnlyList<string> RequiredSkillIds,
    SeniorityLevel Seniority,
    string City,
    string CountryCode,
    WorkArrangement Arrangement,
    SalaryContract Salary);

/// <summary>A job posting in full, as <c>get_job</c> returns it.</summary>
/// <param name="Id">Job id.</param>
/// <param name="Title">Job title.</param>
/// <param name="Description">The full posting text.</param>
/// <param name="RequiredSkillIds">Canonical taxonomy ids the posting requires.</param>
/// <param name="Seniority">Seniority the posting is pitched at.</param>
/// <param name="City">City, empty when the location is unknown.</param>
/// <param name="CountryCode">ISO country code, empty when the location is unknown.</param>
/// <param name="Arrangement">On-site, hybrid or remote.</param>
/// <param name="Salary">The advertised band.</param>
public sealed record JobDetailContract(
    Guid Id,
    string Title,
    string Description,
    IReadOnlyList<string> RequiredSkillIds,
    SeniorityLevel Seniority,
    string City,
    string CountryCode,
    WorkArrangement Arrangement,
    SalaryContract Salary);

/// <summary>
/// Maps domain entities onto the wire contracts.
/// <para>
/// The entities are deliberately not serialized directly. Two reasons, and the second is the load-bearing
/// one: the wire shape is a published contract that must survive a domain refactor, and A1 consumes this
/// schema — a rename inside <see cref="Job"/> should be a compile error here, not a silent break in
/// another repository.
/// </para>
/// </summary>
public static class JobMapper
{
    /// <summary>Projects a posting onto the search-result shape.</summary>
    /// <param name="job">The posting.</param>
    /// <returns>The summary contract.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="job"/> was <see langword="null"/>.</exception>
    public static JobSummaryContract ToSummary(Job job)
    {
        ArgumentNullException.ThrowIfNull(job);

        return new JobSummaryContract(
            job.Id,
            job.Title,
            job.RequiredSkillIds,
            job.Seniority,
            job.Location.City,
            job.Location.CountryCode,
            job.Arrangement,
            ToContract(job.Salary));
    }

    /// <summary>Projects a posting onto the full-detail shape.</summary>
    /// <param name="job">The posting.</param>
    /// <returns>The detail contract.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="job"/> was <see langword="null"/>.</exception>
    public static JobDetailContract ToDetail(Job job)
    {
        ArgumentNullException.ThrowIfNull(job);

        return new JobDetailContract(
            job.Id,
            job.Title,
            job.Description,
            job.RequiredSkillIds,
            job.Seniority,
            job.Location.City,
            job.Location.CountryCode,
            job.Arrangement,
            ToContract(job.Salary));
    }

    private static SalaryContract ToContract(Domain.ValueObjects.SalaryRange salary) =>
        new(salary.Minimum, salary.Maximum, salary.CurrencyCode, salary.IsDisclosed);
}
