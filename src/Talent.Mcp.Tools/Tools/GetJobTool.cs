namespace Talent.Mcp.Tools.Tools;

using System.ComponentModel;
using ModelContextProtocol;
using ModelContextProtocol.Server;
using Talent.Application.UseCases;
using Talent.Mcp.Tools.Constants;
using Talent.Mcp.Tools.Contracts;

/// <summary>
/// Reads one job posting in full.
/// <para>
/// The tool that demonstrates header promotion. <c>Region</c> is routing metadata, not a search
/// parameter, so it travels as a transport header via <see cref="McpHeaderAttribute"/> rather than in
/// the argument list — which is what a multi-brand, multi-region job board does in practice. Under
/// stdio the SDK carries the same value through <c>_meta</c>, so the tool behaves identically on both
/// hosts.
/// </para>
/// <para>
/// It is also the cacheable read: <c>resources/read</c>-style results carry <c>ttlMs</c> and
/// <c>cacheScope</c>, and this one is <c>private</c> precisely because the region header varies the
/// answer and the protocol's cache fields have no <c>Vary</c> equivalent.
/// </para>
/// </summary>
[McpServerToolType]
public sealed class GetJobTool
{
    /// <summary>Reads a posting by id.</summary>
    /// <param name="getJob">Injected use case.</param>
    /// <param name="jobId">The posting id.</param>
    /// <param name="region">Region header, promoted out of the argument list.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The posting and the region that served it.</returns>
    /// <exception cref="McpException">
    /// The posting does not exist, is not served in the requested region, or the region was malformed.
    /// </exception>
    [McpServerTool(
        Name = Mcp.ToolNames.GetJob,
        Title = "Read a job posting",
        ReadOnly = true,
        Idempotent = true,
        OpenWorld = false,
        UseStructuredContent = true),
     Description(
        "Reads one job posting in full, including its description. Use search_jobs to find ids. "
        + "The read is scoped by the Region header when the client sends one: a posting belonging to "
        + "another region is reported as not served here rather than returned.")]
    public static async Task<GetJobResponse> ExecuteAsync(
        GetJobUseCase getJob,
        [Description("The job posting id.")] Guid jobId,
        [McpHeader(Mcp.RegionHeader)]
        [Description("ISO 3166-1 alpha-2 region to serve the read from. Optional; omit for any region.")]
        string? region = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(getJob);

        var (job, failure) = await getJob
            .ExecuteAsync(jobId, region, cancellationToken)
            .ConfigureAwait(false);

        if (job is null)
        {
            throw new McpException(FailureMessage(failure, jobId, region));
        }

        return new GetJobResponse(JobMapper.ToDetail(job), (region ?? string.Empty).Trim());
    }

    private static string FailureMessage(GetJobFailure failure, Guid jobId, string? region) => failure switch
    {
        GetJobFailure.JobNotFound =>
            $"No job posting with id {jobId} exists.",
        GetJobFailure.RegionMismatch =>
            $"Job posting {jobId} exists but is not served in region '{region}'. Retry without the "
            + $"{Mcp.RegionHeader} header, or with the region that owns the posting.",
        GetJobFailure.InvalidRegion =>
            $"The {Mcp.RegionHeader} header must be a two-letter ISO 3166-1 alpha-2 code, e.g. 'ES'; "
            + $"got '{region}'.",

        // Unreachable while every failure above is handled, and kept so that adding a member to
        // GetJobFailure surfaces here as a wrong message rather than as an unhandled switch at runtime.
        _ => $"Job posting {jobId} could not be read.",
    };
}
