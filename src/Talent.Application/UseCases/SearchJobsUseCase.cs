namespace Talent.Application.UseCases;

using Talent.Application.Configuration;
using Talent.Application.Ports;
using Talent.Domain.Entities;
using Talent.Domain.Enums;

/// <summary>
/// State a pagination handle carries between calls.
/// <para>
/// The criteria travel inside the handle, not just the offset. If a client could send back an offset
/// alongside <em>different</em> criteria, page 2 would be an offset into a different result set — the
/// classic pagination bug that silently skips and repeats rows. Carrying both, signed together, makes
/// that impossible to express.
/// </para>
/// </summary>
/// <param name="Query">The original free-text query.</param>
/// <param name="RequiredSkillIds">The original skill filter.</param>
/// <param name="CountryCode">The original country filter.</param>
/// <param name="Arrangement">The original arrangement filter.</param>
/// <param name="Skip">Where the next page starts.</param>
/// <param name="Take">The page size the search was run with.</param>
public sealed record JobSearchCursor(
    string Query,
    IReadOnlyList<string> RequiredSkillIds,
    string CountryCode,
    WorkArrangement Arrangement,
    int Skip,
    int Take);

/// <summary>The outcome of a job search.</summary>
/// <param name="Jobs">The postings on this page.</param>
/// <param name="TotalMatches">Total matches across all pages.</param>
/// <param name="NextPageHandle">
/// Opaque signed handle for the next page, or <see langword="null"/> on the last page.
/// </param>
public sealed record SearchJobsResult(
    IReadOnlyList<Job> Jobs,
    int TotalMatches,
    string? NextPageHandle);

/// <summary>Why a search request was rejected.</summary>
public enum SearchJobsFailure
{
    /// <summary>No failure.</summary>
    None = 0,

    /// <summary>
    /// The supplied handle was forged, tampered with, expired, or minted for something else. All four
    /// are reported identically on purpose: distinguishing them tells an attacker which of their
    /// guesses was closer.
    /// </summary>
    InvalidOrExpiredHandle = 1,
}

/// <summary>
/// Searches job postings, paginating with a server-minted signed handle instead of a session.
/// <para>
/// This is the pattern the 2026-07-28 revision requires: SEP-2567 removed <c>Mcp-Session-Id</c> and
/// SEP-2575 removed the <c>initialize</c> handshake, so continuation state travels as an ordinary tool
/// argument. Signed, because a cursor a client can edit is an access-control hole rather than a
/// convenience.
/// </para>
/// </summary>
public sealed class SearchJobsUseCase
{
    private readonly IJobRepository jobs;
    private readonly IHandleCodec handles;
    private readonly TalentOptions options;

    /// <summary>Creates the use case.</summary>
    /// <param name="jobs">Job repository port.</param>
    /// <param name="handles">Handle codec port.</param>
    /// <param name="options">Tunables.</param>
    /// <exception cref="ArgumentNullException">A required dependency was <see langword="null"/>.</exception>
    public SearchJobsUseCase(IJobRepository jobs, IHandleCodec handles, TalentOptions options)
    {
        ArgumentNullException.ThrowIfNull(jobs);
        ArgumentNullException.ThrowIfNull(handles);
        ArgumentNullException.ThrowIfNull(options);

        this.jobs = jobs;
        this.handles = handles;
        this.options = options;
    }

    /// <summary>Runs the first page of a search.</summary>
    /// <param name="query">Free-text query. May be empty.</param>
    /// <param name="requiredSkillIds">Canonical skill ids to filter by.</param>
    /// <param name="countryCode">ISO country filter, or empty for any.</param>
    /// <param name="arrangement">Arrangement filter, or unspecified for any.</param>
    /// <param name="pageSize">
    /// Requested page size. Clamped to <see cref="TalentOptions.MaxPageSize"/> rather than rejected: a
    /// client asking for too much gets the most it may have, which is more useful than an error.
    /// </param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The first page.</returns>
    public async Task<SearchJobsResult> ExecuteAsync(
        string? query,
        IReadOnlyList<string>? requiredSkillIds,
        string? countryCode,
        WorkArrangement arrangement = WorkArrangement.Unspecified,
        int? pageSize = null,
        CancellationToken cancellationToken = default)
    {
        var criteria = new JobSearchCriteria(
            query ?? string.Empty,
            requiredSkillIds ?? [],
            countryCode ?? string.Empty,
            arrangement,
            Skip: 0,
            Take: this.ClampPageSize(pageSize));

        return await this.RunAsync(criteria, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Continues a search from a handle returned by a previous call.</summary>
    /// <param name="pageHandle">The handle from the previous page.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>
    /// The next page, or <see cref="SearchJobsFailure.InvalidOrExpiredHandle"/> when the handle cannot
    /// be trusted.
    /// </returns>
    public async Task<(SearchJobsResult? Result, SearchJobsFailure Failure)> ContinueAsync(
        string? pageHandle,
        CancellationToken cancellationToken = default)
    {
        if (!this.handles.TryRead<JobSearchCursor>(pageHandle, out var cursor) || cursor is null)
        {
            return (null, SearchJobsFailure.InvalidOrExpiredHandle);
        }

        var criteria = new JobSearchCriteria(
            cursor.Query,
            cursor.RequiredSkillIds,
            cursor.CountryCode,
            cursor.Arrangement,
            cursor.Skip,
            // Re-clamped even though it came from a handle this server signed: options can change
            // between minting and redeeming, and the cap is about protecting this process now.
            this.ClampPageSize(cursor.Take));

        var result = await this.RunAsync(criteria, cancellationToken).ConfigureAwait(false);

        return (result, SearchJobsFailure.None);
    }

    private async Task<SearchJobsResult> RunAsync(
        JobSearchCriteria criteria,
        CancellationToken cancellationToken)
    {
        var page = await this.jobs.SearchAsync(criteria, cancellationToken).ConfigureAwait(false);

        var nextHandle = page.NextSkip is { } nextSkip
            ? this.handles.Mint(
                new JobSearchCursor(
                    criteria.Query,
                    criteria.RequiredSkillIds,
                    criteria.CountryCode,
                    criteria.Arrangement,
                    nextSkip,
                    criteria.Take),
                this.options.PaginationHandleTimeToLive)
            : null;

        return new SearchJobsResult(page.Jobs, page.TotalMatches, nextHandle);
    }

    private int ClampPageSize(int? requested) =>
        requested is null or <= 0
            ? this.options.DefaultPageSize
            : Math.Min(requested.Value, this.options.MaxPageSize);
}
