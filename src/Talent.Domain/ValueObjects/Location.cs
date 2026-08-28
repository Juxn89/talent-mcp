namespace Talent.Domain.ValueObjects;

/// <summary>
/// Where a job is based or a candidate lives. Coarse on purpose: city and ISO country are enough to
/// score location compatibility, and anything finer would be personal data this project has no
/// reason to hold.
/// </summary>
/// <param name="City">City name. Compared case-insensitively.</param>
/// <param name="CountryCode">ISO 3166-1 alpha-2 country code, upper-case.</param>
public sealed record Location(string City, string CountryCode)
{
    /// <summary>Length of an ISO 3166-1 alpha-2 country code.</summary>
    public const int CountryCodeLength = 2;

    /// <summary>A location that was not stated. Scores as incompatible rather than as a match.</summary>
    public static Location Unknown { get; } = new(string.Empty, string.Empty);

    /// <summary>Whether this location carries no information.</summary>
    public bool IsUnknown =>
        string.IsNullOrWhiteSpace(City) && string.IsNullOrWhiteSpace(CountryCode);

    /// <summary>Whether both locations name the same city in the same country.</summary>
    /// <param name="other">The location to compare against.</param>
    /// <returns><see langword="true"/> when both are known and refer to the same city.</returns>
    public bool IsSameCityAs(Location? other) =>
        other is not null
        && !IsUnknown
        && !other.IsUnknown
        && string.Equals(City, other.City, StringComparison.OrdinalIgnoreCase)
        && IsSameCountryAs(other);

    /// <summary>Whether both locations are in the same country.</summary>
    /// <param name="other">The location to compare against.</param>
    /// <returns><see langword="true"/> when both country codes are known and equal.</returns>
    public bool IsSameCountryAs(Location? other) =>
        other is not null
        && !string.IsNullOrWhiteSpace(CountryCode)
        && !string.IsNullOrWhiteSpace(other.CountryCode)
        && string.Equals(CountryCode, other.CountryCode, StringComparison.OrdinalIgnoreCase);

    /// <summary>Renders the location as "City, CC", or an empty string when unknown.</summary>
    /// <returns>A display string.</returns>
    public override string ToString() =>
        IsUnknown ? string.Empty : $"{City}, {CountryCode}".TrimStart(',', ' ');
}
