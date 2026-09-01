namespace Talent.Mcp.Tools.Tools;

using System.ComponentModel;
using ModelContextProtocol;
using ModelContextProtocol.Server;
using Talent.Application.UseCases;
using Talent.Domain.Enums;
using Talent.Mcp.Tools.Constants;
using Talent.Mcp.Tools.Contracts;

/// <summary>
/// Paginated job search.
/// <para>
/// The tool that demonstrates what replaced sessions. SEP-2567 removed <c>Mcp-Session-Id</c> and
/// SEP-2575 removed the <c>initialize</c> handshake, so continuation state is an ordinary tool
/// argument: a server-minted, signed, TTL-bounded handle. Signed because a cursor a client can edit is
/// an access-control hole, not a convenience.
/// </para>
/// <para>
/// Not static — <c>WithTools&lt;T&gt;()</c> rejects static classes with
/// <c>CS0718: static types cannot be used as type arguments</c>. See ADR-0002.
/// </para>
/// </summary>
[McpServerToolType]
public sealed class SearchJobsTool
{
    /// <summary>Runs or continues a job search.</summary>
    /// <param name="search">Injected use case. Resolved from DI, not from the caller's arguments.</param>
    /// <param name="query">Free-text query matched against title and description.</param>
    /// <param name="requiredSkillIds">Canonical skill ids a posting must require.</param>
    /// <param name="countryCode">ISO 3166-1 alpha-2 country filter.</param>
    /// <param name="arrangement">Work-arrangement filter.</param>
    /// <param name="pageSize">Requested page size, clamped to the server maximum.</param>
    /// <param name="pageHandle">Handle returned by a previous call.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The page, plus a handle for the next one when there is one.</returns>
    /// <exception cref="McpException">The supplied handle was not authentic or had expired.</exception>
    [McpServerTool(
        Name = Mcp.ToolNames.SearchJobs,
        Title = "Search job postings",
        ReadOnly = true,
        Idempotent = true,
        OpenWorld = false,
        UseStructuredContent = true),
     Description(
        "Searches job postings by free text, required skills, country and work arrangement. "
        + "Returns one page plus an opaque nextPageHandle. To read the next page, call again passing "
        + "only pageHandle: the handle already carries the original search criteria, and any filter "
        + "arguments sent alongside it are ignored. Handles are short-lived — start a fresh search if "
        + "one is rejected.")]
    public static async Task<SearchJobsResponse> ExecuteAsync(
        SearchJobsUseCase search,
        [Description("Free-text query matched against job title and description. Optional.")]
        string? query = null,
        [Description("Canonical skill ids a posting must require, e.g. [\"csharp\", \"postgresql\"]. Use extract_skills to obtain them.")]
        string[]? requiredSkillIds = null,
        [Description("ISO 3166-1 alpha-2 country code, e.g. \"ES\". Optional.")]
        string? countryCode = null,
        [Description("Work-arrangement filter. Unspecified matches any.")]
        WorkArrangement arrangement = WorkArrangement.Unspecified,
        [Description("Page size. Clamped to the server maximum when larger.")]
        int? pageSize = null,
        [Description("Handle from a previous call's nextPageHandle. When present, all other arguments are ignored.")]
        string? pageHandle = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(search);

        if (!string.IsNullOrWhiteSpace(pageHandle))
        {
            var (continued, failure) = await search
                .ContinueAsync(pageHandle, cancellationToken)
                .ConfigureAwait(false);

            if (failure is SearchJobsFailure.InvalidOrExpiredHandle || continued is null)
            {
                // One message for forged, tampered, expired and foreign handles alike. The use case
                // collapses those four cases on purpose — telling a caller which of their guesses was
                // closer is a gift to an attacker — so the tool must not re-separate them here.
                throw new McpException(
                    "The pageHandle is not valid or has expired. Run search_jobs again without "
                    + "pageHandle to start a fresh search.");
            }

            return ToResponse(continued);
        }

        var result = await search
            .ExecuteAsync(query, requiredSkillIds, countryCode, arrangement, pageSize, cancellationToken)
            .ConfigureAwait(false);

        return ToResponse(result);
    }

    private static SearchJobsResponse ToResponse(SearchJobsResult result) =>
        new(
            result.Jobs.Select(JobMapper.ToSummary).ToArray(),
            result.TotalMatches,
            result.NextPageHandle,
            result.NextPageHandle is not null);
}
