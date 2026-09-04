namespace Talent.Mcp.Tests.Tracing;

using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol.Protocol;
using Talent.Mcp.Tests.Tools;
using Talent.Mcp.Tools.Constants;
using Talent.Mcp.Toolkit.Tracing;
using Xunit;

/// <summary>
/// <c>ToolExecutionTelemetry</c> over the real transport, exercising the actual incoming/outgoing
/// message-filter pair — not the filter delegates in isolation, since the whole point is the
/// <see cref="RequestId"/> correlation across two separate filter pipelines (see the type's own doc
/// comment for why there even are two).
/// <para>
/// <b>Why every test injects its own <c>traceparent</c>.</b> <see cref="TalentActivitySource"/> is a
/// process-wide singleton, registered once for every tool call across this whole test assembly, and
/// xUnit runs different test classes in parallel by default. An <see cref="ActivityListener"/> here
/// would otherwise capture spans other test classes' concurrent tool calls also produce on the same
/// source. Each test manufactures its own random trace id and filters the listener down to activities
/// carrying it, which isolates this test's span from everyone else's without disabling parallelism.
/// </para>
/// </summary>
public sealed class ToolExecutionTelemetryTests
{
    [Fact]
    public async Task A_tool_call_produces_a_span_carrying_the_documented_tags()
    {
        var jobs = new FakeJobRepository(ToolTestData.Many(4));
        await using var harness = await ToolHarness.StartAsync(jobs);

        var (meta, traceId) = NewTraceContext();
        using var captured = Listen(traceId);

        var arguments = new Dictionary<string, JsonElement>(StringComparer.Ordinal)
        {
            ["pageSize"] = JsonSerializer.SerializeToElement(2),
        };

        var result = await harness
            .CallRawAsync(new CallToolRequestParams { Name = Mcp.ToolNames.SearchJobs, Arguments = arguments, Meta = meta });

        Assert.False(result.IsError is true, harness.TextOf(result));

        var activity = Assert.Single(captured.Activities);
        Assert.Equal($"tool.{Mcp.ToolNames.SearchJobs}", activity.OperationName);
        Assert.Equal(Mcp.ToolNames.SearchJobs, activity.GetTagItem("tool.name"));
        Assert.Contains("pageSize", (string)activity.GetTagItem("tool.input")!, StringComparison.Ordinal);
        Assert.NotNull(activity.GetTagItem("tool.output_tokens"));
        Assert.NotNull(activity.GetTagItem("db.query_time"));
        Assert.NotEqual(ActivityStatusCode.Error, activity.Status);
    }

    [Fact]
    public async Task Cache_hit_and_oauth_token_refresh_are_never_tagged()
    {
        // Decided with the user during F4 planning: neither has a real server-side signal — there is
        // no server-side cache behind ttlMs/cacheScope, and this server never refreshes a token (it is
        // a pure OAuth resource server) — so an absent tag is honest and a hardcoded false is not.
        var jobs = new FakeJobRepository(ToolTestData.Many(1));
        await using var harness = await ToolHarness.StartAsync(jobs);

        var (meta, traceId) = NewTraceContext();
        using var captured = Listen(traceId);

        await harness
            .CallRawAsync(new CallToolRequestParams { Name = Mcp.ToolNames.SearchJobs, Meta = meta });

        var activity = Assert.Single(captured.Activities);
        Assert.Null(activity.GetTagItem("cache.hit"));
        Assert.Null(activity.GetTagItem("oauth.token_refresh"));
    }

    [Fact]
    public async Task An_isError_result_marks_the_span_as_failed()
    {
        await using var harness = await ToolHarness.StartAsync(new FakeJobRepository(ToolTestData.Many(4)));

        var (meta, traceId) = NewTraceContext();
        using var captured = Listen(traceId);

        var arguments = new Dictionary<string, JsonElement>(StringComparer.Ordinal)
        {
            ["pageHandle"] = JsonSerializer.SerializeToElement("AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA"),
        };

        var result = await harness
            .CallRawAsync(new CallToolRequestParams { Name = Mcp.ToolNames.SearchJobs, Arguments = arguments, Meta = meta });

        Assert.True(result.IsError);

        var activity = Assert.Single(captured.Activities);
        Assert.Equal(ActivityStatusCode.Error, activity.Status);
    }

    [Fact]
    public async Task A_meta_traceparent_parents_the_tool_span()
    {
        await using var harness = await ToolHarness.StartAsync(new FakeJobRepository(ToolTestData.Many(1)));

        var traceId = ActivityTraceId.CreateRandom().ToHexString();
        var spanId = ActivitySpanId.CreateRandom().ToHexString();
        var meta = new JsonObject
        {
            [Talent.Mcp.Toolkit.Constants.McpMetaKeys.TraceParent] = $"00-{traceId}-{spanId}-01",
        };

        using var captured = Listen(traceId);

        await harness
            .CallRawAsync(new CallToolRequestParams { Name = Mcp.ToolNames.SearchJobs, Meta = meta });

        var activity = Assert.Single(captured.Activities);
        Assert.Equal(traceId, activity.TraceId.ToHexString());
        Assert.Equal(spanId, activity.ParentSpanId.ToHexString());
    }

    /// <summary>A fresh random trace id plus a <c>_meta</c> object carrying it, for cross-test isolation.</summary>
    private static (JsonObject Meta, string TraceId) NewTraceContext()
    {
        var traceId = ActivityTraceId.CreateRandom().ToHexString();
        var spanId = ActivitySpanId.CreateRandom().ToHexString();
        var meta = new JsonObject
        {
            [Talent.Mcp.Toolkit.Constants.McpMetaKeys.TraceParent] = $"00-{traceId}-{spanId}-01",
        };

        return (meta, traceId);
    }

    private static CapturedActivities Listen(string traceId)
    {
        var activities = new List<Activity>();

        var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == TalentActivitySource.Name,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = activity =>
            {
                if (activity.TraceId.ToHexString() == traceId)
                {
                    lock (activities)
                    {
                        activities.Add(activity);
                    }
                }
            },
        };

        ActivitySource.AddActivityListener(listener);

        return new CapturedActivities(listener, activities);
    }

    private sealed class CapturedActivities(ActivityListener listener, List<Activity> activities) : IDisposable
    {
        public IReadOnlyList<Activity> Activities => activities;

        public void Dispose() => listener.Dispose();
    }
}
