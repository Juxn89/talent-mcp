namespace Talent.Domain.ValueObjects;

using Talent.Domain.Constants;

/// <summary>
/// An annual salary band. Not part of fit scoring — it filters in <c>search_jobs</c> and is
/// displayed by <c>get_job</c>, but paying more does not make a candidate fit better.
/// </summary>
/// <param name="Minimum">Lower bound. Zero means "not disclosed".</param>
/// <param name="Maximum">Upper bound. Zero means "not disclosed".</param>
/// <param name="CurrencyCode">ISO 4217 currency code, upper-case.</param>
public sealed record SalaryRange(int Minimum, int Maximum, string CurrencyCode)
{
    /// <summary>
    /// A band that was not disclosed. A new instance per access for the same defensive reason as
    /// <see cref="Location.Unknown"/>: owned-value instances carry EF change-tracking state, so sharing
    /// one is a hazard worth avoiding even where it is not currently a bug.
    /// </summary>
    public static SalaryRange NotDisclosed => new(0, 0, string.Empty);

    /// <summary>Whether the posting disclosed any salary information.</summary>
    public bool IsDisclosed => Minimum > 0 || Maximum > 0;

    /// <summary>
    /// Whether the band is internally consistent and within the schema bounds. Undisclosed bands are
    /// valid — a posting is allowed to say nothing about pay.
    /// </summary>
    /// <returns><see langword="true"/> when the range can be stored.</returns>
    public bool IsValid()
    {
        if (!IsDisclosed)
        {
            return true;
        }

        return Minimum >= JobSchema.MinSalary
            && Maximum <= JobSchema.MaxSalary
            && Minimum <= Maximum
            && !string.IsNullOrWhiteSpace(CurrencyCode);
    }
}
