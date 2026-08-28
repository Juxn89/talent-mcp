namespace Talent.Domain.Enums;

/// <summary>
/// Why a fit-score component landed where it did. Every component of every score carries one, which
/// is what makes the score explainable rather than a bare number — and what lets A1's eval harness
/// assert on the reasoning and not just the total.
/// </summary>
public enum ScoreReason
{
    /// <summary>No reason recorded. Never produced by the scorer; guards default construction.</summary>
    None = 0,

    /// <summary>The candidate covers every skill the job requires.</summary>
    AllRequiredSkillsCovered = 1,

    /// <summary>Some required skills are covered and some are missing.</summary>
    SomeRequiredSkillsMissing = 2,

    /// <summary>None of the required skills are covered.</summary>
    NoRequiredSkillsCovered = 3,

    /// <summary>The job lists no required skills, so the component cannot discriminate.</summary>
    NoSkillsRequired = 4,

    /// <summary>Candidate and job seniority are the same level.</summary>
    SeniorityExactMatch = 5,

    /// <summary>The candidate is more senior than the job asks for.</summary>
    CandidateOverqualified = 6,

    /// <summary>The candidate is less senior than the job asks for.</summary>
    CandidateUnderqualified = 7,

    /// <summary>Either side did not state a seniority level.</summary>
    SeniorityUnknown = 8,

    /// <summary>The role is remote, so location does not constrain the match.</summary>
    RemoteRoleLocationIrrelevant = 9,

    /// <summary>Candidate and job are in the same city.</summary>
    SameCity = 10,

    /// <summary>Different cities in the same country.</summary>
    SameCountry = 11,

    /// <summary>Different countries, and the candidate will not relocate.</summary>
    DifferentCountryNoRelocation = 12,

    /// <summary>Different countries, but the candidate is willing to relocate.</summary>
    DifferentCountryWillRelocate = 13,
}
