namespace Talent.Domain.Enums;

/// <summary>Where a candidate stands in the process.</summary>
public enum CandidateStatus
{
    /// <summary>Not stated. Guards default construction; treated as <see cref="Active"/> nowhere.</summary>
    Unspecified = 0,

    /// <summary>In the process and available to be scored or shortlisted.</summary>
    Active = 1,

    /// <summary>
    /// Rejected. Set only through <see cref="Entities.Candidate.Reject"/>, which requires a reason —
    /// the state and its justification are recorded together or not at all.
    /// </summary>
    Rejected = 2,

    /// <summary>Hired. Terminal, and out of scope for the tool surface.</summary>
    Hired = 3,
}
