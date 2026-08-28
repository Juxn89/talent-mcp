namespace Talent.Domain.Enums;

/// <summary>Where the work is performed. Drives the location component of a fit score.</summary>
public enum WorkArrangement
{
    /// <summary>Not stated. Scored as the most restrictive option, <see cref="OnSite"/>.</summary>
    Unspecified = 0,

    /// <summary>Requires presence at the job's location.</summary>
    OnSite = 1,

    /// <summary>Partly on site, so the candidate must be within reach of the location.</summary>
    Hybrid = 2,

    /// <summary>Fully remote; the candidate's location does not constrain the match.</summary>
    Remote = 3,
}
