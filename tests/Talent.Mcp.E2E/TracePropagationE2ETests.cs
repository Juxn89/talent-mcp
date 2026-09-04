namespace Talent.Mcp.E2E;

using System.Diagnostics;
using ModelContextProtocol.Protocol;
using Talent.Mcp.Toolkit.Tracing;
using Xunit;

/// <summary>
/// Trace propagation across the full real stack: Postgres + Keycloak + the real HTTP host, through
/// OAuth. This is what the plan's F4 bullet "trace propagation in headers" asks for — read literally
/// that would mean an HTTP header, but the 2026-07-28 revision carries trace context in
/// <c>_meta.traceparent</c>, not a header (AGENTS.md's Observability section), so this asserts on
/// <c>_meta</c>, same as <c>ObservabilityConformanceTests</c> does one level down.
/// <para>
/// Sent through <see cref="McpClient.SendRequestAsync{TParams,TResult}"/> rather than
/// <c>CallToolAsync</c>, because the typed convenience method has no parameter for <c>_meta</c> —
/// building <see cref="CallToolRequestParams"/> by hand is the only way to set <c>Meta</c> while still
/// getting the SDK client's own bearer-token handling (<c>RealServerFixture.CreateClientAsync</c>
/// already attached it), rather than a fully hand-rolled HTTP request the way the conformance level
/// does it.
/// </para>
/// </summary>
[Collection(RealServerCollection.Name)]
public sealed class TracePropagationE2ETests
{
    private readonly RealServerFixture fixture;

    public TracePropagationE2ETests(RealServerFixture fixture) => this.fixture = fixture;

    [Fact]
    public async Task A_meta_traceparent_survives_postgres_keycloak_and_a_real_socket()
    {
        var traceId = ActivityTraceId.CreateRandom().ToHexString();
        var spanId = ActivitySpanId.CreateRandom().ToHexString();

        var meta = new System.Text.Json.Nodes.JsonObject
        {
            [Talent.Mcp.Toolkit.Constants.McpMetaKeys.TraceParent] = $"00-{traceId}-{spanId}-01",
        };

        var token = await this.fixture.MintTokenAsync("openid talent.jobs.read");
        await using var client = await this.fixture.CreateClientAsync(token);

        using var captured = Listen(traceId);

        var result = await client.SendRequestAsync<CallToolRequestParams, CallToolResult>(
            "tools/call",
            new CallToolRequestParams { Name = "extract_skills", Meta = meta, Arguments = Arguments() });

        Assert.False(result.IsError is true, TextOf(result));

        var activity = Assert.Single(captured.Activities);
        Assert.Equal(traceId, activity.TraceId.ToHexString());
        Assert.Equal(spanId, activity.ParentSpanId.ToHexString());
        Assert.Equal("extract_skills", activity.GetTagItem("tool.name"));
    }

    private static IDictionary<string, System.Text.Json.JsonElement> Arguments() =>
        new Dictionary<string, System.Text.Json.JsonElement>(StringComparer.Ordinal)
        {
            ["text"] = System.Text.Json.JsonSerializer.SerializeToElement("Five years of C# and PostgreSQL."),
        };

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

    private static string TextOf(CallToolResult result) =>
        string.Join(
            Environment.NewLine,
            result.Content.OfType<TextContentBlock>().Select(static block => block.Text));

    private sealed class CapturedActivities(ActivityListener listener, List<Activity> activities) : IDisposable
    {
        public IReadOnlyList<Activity> Activities => activities;

        public void Dispose() => listener.Dispose();
    }
}
