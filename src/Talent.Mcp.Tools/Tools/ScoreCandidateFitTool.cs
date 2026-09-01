namespace Talent.Mcp.Tools.Tools;

using System.ComponentModel;
using ModelContextProtocol;
using ModelContextProtocol.Server;
using Talent.Application.UseCases;
using Talent.Mcp.Tools.Constants;
using Talent.Mcp.Tools.Contracts;

/// <summary>
/// Scores one candidate against one job, with the reasoning attached.
/// <para>
/// Deterministic and LLM-free, which is the point: the score is reproducible, so A1's eval harness can
/// treat it as ground truth rather than as another model's opinion. The breakdown is not decoration —
/// a recruitment score without an explanation is unusable in the domain and unauditable outside it.
/// </para>
/// </summary>
[McpServerToolType]
public sealed class ScoreCandidateFitTool
{
    /// <summary>Scores a candidate against a job.</summary>
    /// <param name="score">Injected use case.</param>
    /// <param name="candidateId">The candidate.</param>
    /// <param name="jobId">The job.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The total and its per-component breakdown.</returns>
    /// <exception cref="McpException">Either id did not exist.</exception>
    [McpServerTool(
        Name = Mcp.ToolNames.ScoreCandidateFit,
        Title = "Score candidate fit",
        ReadOnly = true,
        Idempotent = true,
        OpenWorld = false,
        UseStructuredContent = true),
     Description(
        "Scores one candidate against one job posting and returns a 0-100 total with a per-component "
        + "breakdown: skill overlap, seniority distance and location compatibility, each with its own "
        + "raw score, weight and a reason code. Deterministic — no language model is involved, so the "
        + "same pair always scores the same.")]
    public static async Task<ScoreCandidateFitResponse> ExecuteAsync(
        ScoreCandidateFitUseCase score,
        [Description("The candidate id.")] Guid candidateId,
        [Description("The job posting id.")] Guid jobId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(score);

        var (fit, failure) = await score
            .ExecuteAsync(candidateId, jobId, cancellationToken)
            .ConfigureAwait(false);

        if (fit is null)
        {
            // Which id was wrong is named. Unlike a bad pagination handle this is not adversarial —
            // it is a typo, and a caller told only "not found" has to guess which of the two to fix.
            throw new McpException(failure switch
            {
                ScoreCandidateFitFailure.JobNotFound => $"No job posting with id {jobId} exists.",
                ScoreCandidateFitFailure.CandidateNotFound => $"No candidate with id {candidateId} exists.",
                _ => $"Candidate {candidateId} could not be scored against job {jobId}.",
            });
        }

        return ScoreCandidateFitResponse.From(candidateId, jobId, fit);
    }
}
