namespace Talent.Mcp.Tests.Tools;

using System.Text.Json;
using ModelContextProtocol;
using ModelContextProtocol.Extensions.Tasks;
using Talent.Mcp.Tools.Constants;
using Xunit;

/// <summary>
/// <c>bulk_score_shortlist</c>: the one tool that requires the MCP Tasks extension.
/// <para>
/// Every positive-path test drives it through <see cref="ToolHarness.RunAsTaskToCompletionAsync"/>,
/// which is what a task-capable client actually does — <c>tools/call</c> returns a task id almost
/// immediately, and the result arrives from polling <c>tasks/get</c>, not from the original call.
/// </para>
/// <para>
/// Measured 2 Sep 2026: a tool that throws <see cref="McpException"/> lands the task in
/// <see cref="McpTaskStatus.Completed"/>, not <see cref="McpTaskStatus.Failed"/> —
/// <see cref="CompletedTaskResult.Result"/> is the ordinary <c>CallToolResult</c> shape, carrying
/// <c>isError: true</c> and the message as text. <see cref="FailedTaskResult"/> is reserved for an
/// exception that is not an <c>McpException</c>, matching the non-task rule that only
/// <c>McpException</c> keeps its message on the wire.
/// </para>
/// </summary>
public sealed class BulkScoreShortlistToolTests
{
    [Fact]
    public async Task A_plain_call_is_refused_because_the_client_declared_no_task_support()
    {
        await using var harness = await ToolHarness.StartAsync(
            new FakeJobRepository([ToolTestData.MadridBackend]),
            new FakeCandidateRepository([ToolTestData.MadridSenior]));

        // CallToolAsync, not CallToolAsTaskAsync — this client never declares the Tasks extension.
        // The refusal happens in the SDK's own filter, before the tool body runs, which is why this
        // is asserted as an exception rather than an isError result.
        await Assert.ThrowsAsync<MissingRequiredClientCapabilityException>(() => harness.CallAsync(
            Mcp.ToolNames.BulkScoreShortlist,
            new Dictionary<string, object?>
            {
                ["jobId"] = ToolTestData.MadridBackend.Id,
                ["candidateIds"] = new[] { ToolTestData.MadridSenior.Id },
            }));
    }

    [Fact]
    public async Task A_task_capable_client_gets_scores_ordered_by_descending_fit()
    {
        var candidates = ToolTestData.ManyCandidates(6);
        await using var harness = await ToolHarness.StartAsync(
            new FakeJobRepository([ToolTestData.MadridBackend]),
            new FakeCandidateRepository(candidates));

        var payload = await StructuredResultOfAsync(
            harness,
            new Dictionary<string, object?>
            {
                ["jobId"] = ToolTestData.MadridBackend.Id,
                ["candidateIds"] = candidates.Select(static c => c.Id).ToArray(),
            });

        var totals = payload.GetProperty("entries").EnumerateArray()
            .Select(static e => e.GetProperty("total").GetDouble())
            .ToArray();

        Assert.Equal(6, totals.Length);
        Assert.Equal(totals.OrderByDescending(static t => t).ToArray(), totals);
        Assert.Empty(payload.GetProperty("unmatchedCandidateIds").EnumerateArray());
        Assert.Equal(6, payload.GetProperty("requestedCount").GetInt32());
        Assert.Equal(6, payload.GetProperty("scoredCount").GetInt32());
    }

    [Fact]
    public async Task Each_entry_carries_the_same_breakdown_shape_as_score_candidate_fit()
    {
        await using var harness = await ToolHarness.StartAsync(
            new FakeJobRepository([ToolTestData.MadridBackend]),
            new FakeCandidateRepository([ToolTestData.MadridSenior]));

        var payload = await StructuredResultOfAsync(
            harness,
            new Dictionary<string, object?>
            {
                ["jobId"] = ToolTestData.MadridBackend.Id,
                ["candidateIds"] = new[] { ToolTestData.MadridSenior.Id },
            });

        var entry = payload.GetProperty("entries")[0];

        Assert.Equal(ToolTestData.MadridSenior.Id.ToString(), entry.GetProperty("candidateId").GetString());
        Assert.Equal(100, entry.GetProperty("total").GetDouble());

        var componentNames = entry.GetProperty("components").EnumerateArray()
            .Select(static c => c.GetProperty("name").GetString()!)
            .ToArray();
        Assert.Equal(["skill_overlap", "seniority_distance", "location_compatibility"], componentNames);
    }

    [Fact]
    public async Task Ids_that_do_not_resolve_to_a_candidate_are_reported_not_dropped()
    {
        await using var harness = await ToolHarness.StartAsync(
            new FakeJobRepository([ToolTestData.MadridBackend]),
            new FakeCandidateRepository([ToolTestData.MadridSenior]));

        var missing = Guid.Parse("99999999-0000-0000-0000-000000000001");

        var payload = await StructuredResultOfAsync(
            harness,
            new Dictionary<string, object?>
            {
                ["jobId"] = ToolTestData.MadridBackend.Id,
                ["candidateIds"] = new[] { ToolTestData.MadridSenior.Id, missing },
            });

        Assert.Equal(1, payload.GetProperty("scoredCount").GetInt32());
        Assert.Equal(2, payload.GetProperty("requestedCount").GetInt32());

        var unmatched = payload.GetProperty("unmatchedCandidateIds").EnumerateArray()
            .Select(static id => id.GetString()!)
            .ToArray();
        Assert.Equal([missing.ToString()], unmatched);
    }

    [Fact]
    public async Task Duplicate_ids_in_the_request_are_collapsed_not_double_scored()
    {
        await using var harness = await ToolHarness.StartAsync(
            new FakeJobRepository([ToolTestData.MadridBackend]),
            new FakeCandidateRepository([ToolTestData.MadridSenior]));

        var payload = await StructuredResultOfAsync(
            harness,
            new Dictionary<string, object?>
            {
                ["jobId"] = ToolTestData.MadridBackend.Id,
                ["candidateIds"] = new[] { ToolTestData.MadridSenior.Id, ToolTestData.MadridSenior.Id },
            });

        Assert.Equal(1, payload.GetProperty("requestedCount").GetInt32());
        Assert.Equal(1, payload.GetProperty("scoredCount").GetInt32());
    }

    [Fact]
    public async Task An_empty_shortlist_fails_with_an_actionable_error()
    {
        await using var harness = await ToolHarness.StartAsync(
            new FakeJobRepository([ToolTestData.MadridBackend]),
            new FakeCandidateRepository([]));

        var text = await ErrorTextOfAsync(
            harness,
            new Dictionary<string, object?>
            {
                ["jobId"] = ToolTestData.MadridBackend.Id,
                ["candidateIds"] = Array.Empty<Guid>(),
            });

        Assert.Contains("at least one id", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_shortlist_beyond_the_maximum_is_refused_rather_than_silently_truncated()
    {
        var options = new Talent.Application.Configuration.TalentOptions { MaxShortlistSize = 3 };
        var five = ToolTestData.ManyCandidates(5);
        await using var harness = await ToolHarness.StartAsync(
            new FakeJobRepository([ToolTestData.MadridBackend]),
            new FakeCandidateRepository(five),
            options: options);

        var text = await ErrorTextOfAsync(
            harness,
            new Dictionary<string, object?>
            {
                ["jobId"] = ToolTestData.MadridBackend.Id,
                ["candidateIds"] = five.Select(static c => c.Id).ToArray(),
            });

        // The number itself is in the message: a client hitting this needs to know the actual cap, not
        // just that one exists.
        Assert.Contains("at most 3", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_missing_job_fails_naming_the_id()
    {
        await using var harness = await ToolHarness.StartAsync(
            new FakeJobRepository([]),
            new FakeCandidateRepository([ToolTestData.MadridSenior]));

        var missingJob = Guid.Parse("88888888-0000-0000-0000-000000000002");

        var text = await ErrorTextOfAsync(
            harness,
            new Dictionary<string, object?>
            {
                ["jobId"] = missingJob,
                ["candidateIds"] = new[] { ToolTestData.MadridSenior.Id },
            });

        Assert.Contains(missingJob.ToString(), text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_tool_is_listed_with_an_input_schema_like_every_other_tool()
    {
        await using var harness = await ToolHarness.StartAsync();

        var tool = (await harness.Client.ListToolsAsync())
            .Single(t => t.Name == Mcp.ToolNames.BulkScoreShortlist);

        var schema = tool.ProtocolTool.InputSchema;
        Assert.Equal(
            new[] { "jobId", "candidateIds" },
            schema.GetProperty("required").EnumerateArray().Select(static r => r.GetString()).ToArray());
    }

    private static async Task<JsonElement> StructuredResultOfAsync(
        ToolHarness harness,
        IReadOnlyDictionary<string, object?> arguments)
    {
        var status = await harness.RunAsTaskToCompletionAsync(Mcp.ToolNames.BulkScoreShortlist, arguments);
        var completed = Assert.IsType<CompletedTaskResult>(status);

        // isError is omitted on success, not sent as false — the same null-omission behaviour measured
        // for nextPageHandle on search_jobs.
        Assert.False(completed.Result.TryGetProperty("isError", out var isError) && isError.GetBoolean());

        return completed.Result.GetProperty("structuredContent");
    }

    private static async Task<string> ErrorTextOfAsync(
        ToolHarness harness,
        IReadOnlyDictionary<string, object?> arguments)
    {
        var status = await harness.RunAsTaskToCompletionAsync(Mcp.ToolNames.BulkScoreShortlist, arguments);
        var completed = Assert.IsType<CompletedTaskResult>(status);

        Assert.True(completed.Result.GetProperty("isError").GetBoolean());

        return completed.Result.GetProperty("content")[0].GetProperty("text").GetString()!;
    }
}
