namespace Talent.Mcp.Conformance;

using System.Diagnostics;
using Talent.Mcp.Toolkit.Tracing;
using Talent.Mcp.Tools.Constants;
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

    private static readonly IReadOnlyDictionary<string, object?> SearchJobsArguments =
        new Dictionary<string, object?> { ["query"] = "engineer", ["pageSize"] = 2 };

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

    [Fact]
    public async Task A_tool_that_reads_Postgres_carries_the_trace_into_the_database()
    {
        // #13: the README promised client → server → Postgres, but only the tool source was registered.
        // search_jobs runs a query, so with Npgsql's source registered its command span joins the trace,
        // parented to the tool span. Filtered by this test's trace id: the task store's background
        // LISTEN/sweep also talks to Postgres, outside any tool call.
        var traceId = ActivityTraceId.CreateRandom().ToHexString();
        var traceParent = $"00-{traceId}-{ActivitySpanId.CreateRandom().ToHexString()}-01";

        using var captured = ListenWhere(
            activity => activity.TraceId.ToHexString() == traceId,
            TalentActivitySource.Name,
            ExternalActivitySources.Npgsql);
        using var http = this.fixture.CreateHttpClient();
        using var request = RawJsonRpc.ToolsCall(
            this.fixture.McpEndpoint,
            "search_jobs",
            SearchJobsArguments,
            nameHeader: "search_jobs",
            traceParent: traceParent);

        using var response = await http.SendAsync(request);
        response.EnsureSuccessStatusCode();

        var tool = Assert.Single(captured.Activities, static a => a.Source.Name == TalentActivitySource.Name);
        var database = captured.Activities.Where(static a => a.Source.Name == ExternalActivitySources.Npgsql).ToArray();
        Assert.NotEmpty(database);
        Assert.All(database, span => Assert.Equal(tool.SpanId, span.ParentSpanId));
    }

    private static CapturedActivities Listen(string traceId) =>
        ListenWhere(activity => activity.TraceId.ToHexString() == traceId);

    private static CapturedActivities ListenAll() => ListenWhere(static _ => true);

    private static CapturedActivities ListenWhere(Func<Activity, bool> predicate, params string[] sourceNames)
    {
        var activities = new List<Activity>();
        var sources = sourceNames.Length == 0 ? [TalentActivitySource.Name] : sourceNames;

        var listener = new ActivityListener
        {
            ShouldListenTo = source => sources.Contains(source.Name, StringComparer.Ordinal),
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
