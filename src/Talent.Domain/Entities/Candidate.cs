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
    /// <param name="status">Where they stand in the process. Defaults to <see cref="CandidateStatus.Active"/>.</param>
    public Candidate(
        Guid id,
        string fullName,
        IEnumerable<string> skillIds,
        int yearsOfExperience,
        SeniorityLevel seniority,
        Location location,
        bool willingToRelocate,
        CandidateStatus status = CandidateStatus.Active)
    {
        ArgumentNullException.ThrowIfNull(skillIds);

        this.Id = id;
        this.FullName = fullName ?? string.Empty;
        this.SkillIds = skillIds
            .Where(static s => !string.IsNullOrWhiteSpace(s))
            .Select(static s => s.Trim().ToLowerInvariant())
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        this.YearsOfExperience = yearsOfExperience;
        this.Seniority = seniority;
        this.Location = location ?? ValueObjects.Location.Unknown;
        this.WillingToRelocate = willingToRelocate;
        this.Status = status;
    }

    /// <summary>
    /// Private constructor for EF Core materialization.
    /// <para>
    /// EF cannot use the public constructor: it binds constructor parameters by name to <em>scalar</em>
    /// properties, and <see cref="Location"/> is an owned navigation which EF sets after construction.
    /// So the public constructor stays the domain-facing one that enforces invariants, and this one
    /// exists purely so the mapping has somewhere to put values. It is never called by domain code.
    /// </para>
    /// </summary>
    private Candidate()
    {
        this.FullName = string.Empty;
        this.SkillIds = [];
        this.Location = ValueObjects.Location.Unknown;
    }

    /// <summary>Stable identifier.</summary>
    public Guid Id { get; private set; }

    /// <summary>Display name.</summary>
    public string FullName { get; private set; }

    /// <summary>Canonical skill ids, de-duplicated and lower-cased.</summary>
    public IReadOnlyList<string> SkillIds { get; private set; }

    /// <summary>Years of professional experience.</summary>
    public int YearsOfExperience { get; private set; }

    /// <summary>Self-reported or assessed seniority.</summary>
    public SeniorityLevel Seniority { get; private set; }

    /// <summary>Where the candidate lives.</summary>
    public Location Location { get; private set; }

    /// <summary>Whether the candidate would move country for a role.</summary>
    public bool WillingToRelocate { get; private set; }

    /// <summary>Where they stand in the process.</summary>
    public CandidateStatus Status { get; private set; }

    /// <summary>Why they were rejected, when they were. Retained for audit.</summary>
    public string? RejectionReason { get; private set; }

    /// <summary>When they were rejected.</summary>
    public DateTimeOffset? RejectedAt { get; private set; }

    /// <summary>
    /// Rejects the candidate, recording why.
    /// <para>
    /// The reason is mandatory <em>in the domain</em>, not only at the tool boundary. That is what makes
    /// the MRTR confirmation round-trip a consequence of a business rule rather than a UI flourish: no
    /// caller, however it reaches this method, can reject someone without saying why.
    /// </para>
    /// </summary>
    /// <param name="reason">Why they were rejected.</param>
    /// <param name="at">
    /// When. Passed in rather than read from the clock, because the domain has no framework
    /// dependencies and therefore no <c>TimeProvider</c> — and because a caller-supplied instant is
    /// what makes this testable without freezing time.
    /// </param>
    /// <exception cref="ArgumentException">The reason was missing or blank.</exception>
    /// <exception cref="InvalidOperationException">The candidate was already rejected or hired.</exception>
    public void Reject(string reason, DateTimeOffset at)
    {
        if (string.IsNullOrWhiteSpace(reason))
        {
            throw new ArgumentException("A rejection must record why.", nameof(reason));
        }

        if (this.Status is CandidateStatus.Rejected or CandidateStatus.Hired)
        {
            throw new InvalidOperationException(
                $"Candidate {this.Id} is already {this.Status} and cannot be rejected again.");
        }

        this.Status = CandidateStatus.Rejected;
        this.RejectionReason = reason.Trim();
        this.RejectedAt = at;
    }

    /// <summary>
    /// Whether the profile satisfies the domain invariants in <see cref="CandidateSchema"/>.
    /// </summary>
    /// <returns><see langword="true"/> when the profile can be stored.</returns>
    public bool IsValid() =>
        this.Id != Guid.Empty
        && this.FullName.Length > 0
        && this.FullName.Length <= CandidateSchema.MaxFullNameLength
        && this.YearsOfExperience >= CandidateSchema.MinExperienceYears
        && this.YearsOfExperience <= CandidateSchema.MaxExperienceYears
        && this.SkillIds.Count <= CandidateSchema.MaxSkills
        && this.RejectionIsConsistent();

    /// <summary>
    /// A rejected candidate must carry both a reason and a timestamp, and a non-rejected one must carry
    /// neither. Half-set state means something wrote the fields directly instead of going through
    /// <see cref="Reject"/>.
    /// </summary>
    private bool RejectionIsConsistent() =>
        this.Status == CandidateStatus.Rejected
            ? !string.IsNullOrWhiteSpace(this.RejectionReason) && this.RejectedAt is not null
            : this.RejectionReason is null && this.RejectedAt is null;
}
