namespace Talent.Application.Ports;

using Talent.Domain.Entities;

/// <summary>
/// One page of job postings.
/// </summary>
/// <param name="Jobs">The postings on this page, in a stable order.</param>
/// <param name="TotalMatches">Total matches across all pages.</param>
/// <param name="NextSkip">
/// Where the next page starts, or <see langword="null"/> when this is the last page. The tool layer
/// wraps this in a signed handle — the client never receives a raw offset it could tamper with.
/// </param>
public sealed record JobPage(IReadOnlyList<Job> Jobs, int TotalMatches, int? NextSkip);
