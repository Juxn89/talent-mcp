namespace Talent.Mcp.Tools.Tools;

using System.ComponentModel;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Talent.Application.Configuration;
using Talent.Application.UseCases;
using Talent.Mcp.Tools.Constants;
using Talent.Mcp.Tools.Contracts;

/// <summary>
/// Scores every candidate in a shortlist against one job — the long-running tool, and the one that
/// demonstrates the MCP Tasks extension.
/// <para>
/// <c>TalentTools.AddTalentTools</c> sets this tool's execution mode to
/// <c>McpTaskExecutionMode.Required</c>: a client must declare the Tasks extension to call it at all.
/// The other five tools stay <c>Synchronous</c>. That split is deliberate — this is the one operation
/// in the surface sized to actually take a while (up to <see cref="TalentOptions.MaxShortlistSize"/>
/// candidates), and forcing task mode is what makes the demonstration real rather than incidental: a
/// client that never opts in never proves anything about the store surviving a restart.
/// </para>
/// <para>
/// Once task mode applies, the SDK owns the whole lifecycle — creating the task, running this handler
/// in the background, and recording the result in the store on completion or failure
/// (<c>McpTasksBuilderExtensions.WithTasks</c> docs: "filters registered after it… run in the
/// background before the tool"). This method never touches <c>IMcpTaskStore</c> directly; it returns a
/// result exactly as any other tool would, and the extension does the rest.
/// </para>
/// </summary>
[McpServerToolType]
public sealed class BulkScoreShortlistTool
{
    /// <summary>Scores a shortlist against a job.</summary>
    /// <param name="context">Request context, for the caller's progress token.</param>
    /// <param name="bulkScore">Injected use case.</param>
    /// <param name="options">Injected tunables, for the size-cap error message.</param>
    /// <param name="jobId">The job to score against.</param>
    /// <param name="candidateIds">The shortlisted candidate ids. Duplicates are ignored.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The scored shortlist, ordered by descending fit.</returns>
    /// <exception cref="McpException">
    /// The job does not exist, no candidate ids were supplied, or more were supplied than the server
    /// accepts in one call.
    /// </exception>
    [McpServerTool(
        Name = Mcp.ToolNames.BulkScoreShortlist,
        Title = "Bulk score a shortlist",
        ReadOnly = true,
        Idempotent = true,
        OpenWorld = false,
        UseStructuredContent = true),
     Description(
        "Scores every candidate in a shortlist against one job posting, using the same explainable "
        + "scoring as score_candidate_fit. Runs as an MCP task: the client must declare the Tasks "
        + "extension, and should poll tasks/get with the returned task id rather than waiting on this "
        + "call. Candidates that do not exist are reported in unmatchedCandidateIds rather than "
        + "silently dropped. Deterministic — no language model is involved.")]
    public static async Task<BulkScoreShortlistResponse> ExecuteAsync(
        RequestContext<CallToolRequestParams> context,
        BulkScoreShortlistUseCase bulkScore,
        TalentOptions options,
        [Description("The job posting to score against.")] Guid jobId,
        [Description("Candidate ids to score. At least one is required.")] Guid[] candidateIds,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(bulkScore);
        ArgumentNullException.ThrowIfNull(options);

        var progress = new ProgressBridge(context.Server, context.Params?.ProgressToken, candidateIds?.Length ?? 0);

        var (result, failure) = await bulkScore
            .ExecuteAsync(jobId, candidateIds, progress, cancellationToken)
            .ConfigureAwait(false);

        if (result is null)
        {
            throw new McpException(failure switch
            {
                BulkScoreShortlistFailure.JobNotFound =>
                    $"No job posting with id {jobId} exists.",
                BulkScoreShortlistFailure.ShortlistEmpty =>
                    "candidateIds must contain at least one id.",
                BulkScoreShortlistFailure.ShortlistTooLarge =>
                    $"A shortlist may contain at most {options.MaxShortlistSize} candidates; got "
                    + $"{candidateIds?.Length ?? 0}.",
                _ => "The shortlist could not be scored.",
            });
        }

        return BulkScoreShortlistResponse.From(jobId, result);
    }

    /// <summary>
    /// Bridges <see cref="IProgress{T}"/> to an MCP <c>notifications/progress</c> message.
    /// <para>
    /// Best-effort by construction: <see cref="Report"/> fires the notification without awaiting it, and
    /// swallows any failure, because a client not receiving a progress update must never fail the
    /// scoring run it is a status update about.
    /// </para>
    /// <para>
    /// It is also, by reasoning rather than by measurement, likely undeliverable at all on the
    /// Streamable HTTP host once the task-creation response has returned: under
    /// <c>HttpServerSessionMode.Stateless</c> (ADR-0001) a <c>GET /mcp</c> answers <c>405</c>, so there
    /// is no channel left open for the server to push anything on between requests. It still costs
    /// nothing to attempt — and it reaches a stdio client, whose single duplex stream stays open for the
    /// task's entire lifetime. See the F2 verification record for how this was reasoned through rather
    /// than exhaustively proven over HTTP.
    /// </para>
    /// </summary>
    private sealed class ProgressBridge(McpServer server, ProgressToken? token, int total) : IProgress<int>
    {
        /// <inheritdoc />
        public void Report(int value)
        {
            if (token is not { } progressToken)
            {
                return;
            }

            _ = this.NotifyAsync(progressToken, value);
        }

        private async Task NotifyAsync(ProgressToken progressToken, int scored)
        {
            try
            {
                await server.NotifyProgressAsync(
                    progressToken,
                    new ProgressNotificationValue
                    {
                        Progress = scored,
                        Total = total,
                        Message = $"Scored {scored} of {total} candidates.",
                    }).ConfigureAwait(false);
            }
            catch (Exception)
            {
                // Best-effort; see the type-level comment.
            }
        }
    }
}
