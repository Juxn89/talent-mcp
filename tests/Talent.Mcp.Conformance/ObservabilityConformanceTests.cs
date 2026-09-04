namespace Talent.Mcp.Conformance;

using System.Diagnostics;
using Talent.Mcp.Toolkit.Tracing;
using Xunit;

/// <summary>
/// Trace propagation over a real Streamable HTTP <c>tools/call</c>, closing the ambiguity in the F4
/// plan's "trace propagation in headers" bullet: this revision carries trace context in
/// <c>_meta.traceparent</c>, not an HTTP header (see <see cref="RawJsonRpc"/> and AGENTS.md's
/// Observability section) — so this test asserts on <c>_meta</c>, not a raw header, and the E2E level's
/// <c>TracePropagationE2ETests</c> makes the same clarification.
/// <para>
/// The listener attaches to <see cref="TalentActivitySource"/> in this same test process — the fixture
/// runs the real HTTP host in-process (see <see cref="ConformanceServerFixture"/>'s own doc comment) —
/// and filters by a fresh random trace id per test for the same reason <c>ToolExecutionTelemetryTests</c>
/// does: xUnit can run other collections concurrently, and the source is a process-wide singleton.
/// </para>
/// </summary>
[Collection(ConformanceServerCollection.Name)]
public sealed class ObservabilityConformanceTests
{
    private static readonly IReadOnlyDictionary<string, object?> ExtractSkillsArguments =
        new Dictionary<string, object?> { ["text"] = "Five years of C# and PostgreSQL." };

    private readonly ConformanceServerFixture fixture;

    public ObservabilityConformanceTests(ConformanceServerFixture fixture) => this.fixture = fixture;

    [Fact]
    public async Task A_meta_traceparent_on_a_real_HTTP_call_parents_the_tool_span()
    {
        var traceId = ActivityTraceId.CreateRandom().ToHexString();
        var spanId = ActivitySpanId.CreateRandom().ToHexString();
        var traceParent = $"00-{traceId}-{spanId}-01";

        using var captured = Listen(traceId);
        using var http = this.fixture.CreateHttpClient();
        using var request = RawJsonRpc.ToolsCall(
            this.fixture.McpEndpoint,
            "extract_skills",
            ExtractSkillsArguments,
            nameHeader: "extract_skills",
            traceParent: traceParent);

        using var response = await http.SendAsync(request);
        response.EnsureSuccessStatusCode();

        var activity = Assert.Single(captured.Activities);
        Assert.Equal(traceId, activity.TraceId.ToHexString());
        Assert.Equal(spanId, activity.ParentSpanId.ToHexString());
        Assert.Equal("extract_skills", activity.GetTagItem("tool.name"));
    }

    [Fact]
    public async Task A_call_with_no_traceparent_still_gets_an_unparented_span_of_its_own()
    {
        // No traceparent means no trace id to pre-filter by — StartServerActivity picks a fresh random
        // one itself. Safe to listen for every span here because ConformanceServerCollection disables
        // parallelization: nothing else in this collection calls a tool while this test runs.
        using var captured = ListenAll();
        using var http = this.fixture.CreateHttpClient();
        using var request = RawJsonRpc.ToolsCall(
            this.fixture.McpEndpoint, "extract_skills", ExtractSkillsArguments, nameHeader: "extract_skills");

        using var response = await http.SendAsync(request);
        response.EnsureSuccessStatusCode();

        var activity = Assert.Single(captured.Activities);
        Assert.Equal(default, activity.ParentSpanId);
    }

    private static CapturedActivities Listen(string traceId) =>
        ListenWhere(activity => activity.TraceId.ToHexString() == traceId);

    private static CapturedActivities ListenAll() => ListenWhere(static _ => true);

    private static CapturedActivities ListenWhere(Func<Activity, bool> predicate)
    {
        var activities = new List<Activity>();

        var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == TalentActivitySource.Name,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = activity =>
            {
                if (predicate(activity))
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
