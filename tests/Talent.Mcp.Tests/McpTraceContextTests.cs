namespace Talent.Mcp.Tests;

using System.Diagnostics;
using System.Text.Json;
using Talent.Mcp.Toolkit.Constants;
using Talent.Mcp.Toolkit.Tracing;
using Xunit;

/// <summary>
/// Tests for extracting W3C Trace Context out of an MCP request's <c>_meta</c>.
/// <para>
/// The recurring theme: a malformed header must cost a trace link, never the request. The MCP Logging
/// API is deprecated, so OpenTelemetry is the only observability channel an HTTP host has — but
/// observability failing closed would turn a monitoring problem into an outage.
/// </para>
/// </summary>
public sealed class McpTraceContextTests
{
    private const string ValidTraceParent = "00-0af7651916cd43dd8448eb211c80319c-b7ad6b7169203331-01";

    [Fact]
    public void Extracts_a_valid_traceparent()
    {
        var meta = Meta((McpMetaKeys.TraceParent, ValidTraceParent));

        Assert.True(McpTraceContext.TryExtract(meta, out var context));
        Assert.Equal("0af7651916cd43dd8448eb211c80319c", context.TraceId.ToHexString());
        Assert.Equal("b7ad6b7169203331", context.SpanId.ToHexString());
        Assert.Equal(ActivityTraceFlags.Recorded, context.TraceFlags);
    }

    [Fact]
    public void Marks_the_parent_as_remote_so_the_server_span_is_a_child()
    {
        var meta = Meta((McpMetaKeys.TraceParent, ValidTraceParent));

        Assert.True(McpTraceContext.TryExtract(meta, out var context));
        Assert.True(context.IsRemote);
    }

    [Fact]
    public void Carries_tracestate_through()
    {
        var meta = Meta(
            (McpMetaKeys.TraceParent, ValidTraceParent),
            (McpMetaKeys.TraceState, "vendor=value,other=thing"));

        Assert.True(McpTraceContext.TryExtract(meta, out var context));
        Assert.Equal("vendor=value,other=thing", context.TraceState);
    }

    [Theory]
    [InlineData("not-a-traceparent")]
    [InlineData("00-tooshort-b7ad6b7169203331-01")]
    [InlineData("")]
    [InlineData("   ")]
    public void A_malformed_traceparent_is_ignored_rather_than_fatal(string traceParent)
    {
        var meta = Meta((McpMetaKeys.TraceParent, traceParent));

        Assert.False(McpTraceContext.TryExtract(meta, out var context));
        Assert.Equal(default, context);
    }

    [Fact]
    public void Absent_meta_yields_no_context()
    {
        Assert.False(McpTraceContext.TryExtract(null, out _));
        Assert.False(McpTraceContext.TryExtract(Meta(), out _));
    }

    [Fact]
    public void A_non_string_traceparent_is_ignored()
    {
        // A client sending a number where a string belongs should not take the request down with it.
        var meta = new Dictionary<string, JsonElement>(StringComparer.Ordinal)
        {
            [McpMetaKeys.TraceParent] = JsonDocument.Parse("42").RootElement,
        };

        Assert.False(McpTraceContext.TryExtract(meta, out _));
    }

    [Fact]
    public void Parses_baggage_entries()
    {
        var meta = Meta((McpMetaKeys.Baggage, "tenant=acme,region=eu-west-1"));

        var baggage = McpTraceContext.ExtractBaggage(meta);

        Assert.Equal(2, baggage.Count);
        Assert.Equal("acme", baggage.Single(b => b.Key == "tenant").Value);
        Assert.Equal("eu-west-1", baggage.Single(b => b.Key == "region").Value);
    }

    [Fact]
    public void Drops_w3c_baggage_properties_which_are_metadata_not_value()
    {
        var meta = Meta((McpMetaKeys.Baggage, "tenant=acme;propertyKey=propertyValue,region=eu"));

        var baggage = McpTraceContext.ExtractBaggage(meta);

        Assert.Equal("acme", baggage.Single(b => b.Key == "tenant").Value);
    }

    [Fact]
    public void Percent_decodes_baggage_values()
    {
        var meta = Meta((McpMetaKeys.Baggage, "note=hello%20world"));

        Assert.Equal("hello world", McpTraceContext.ExtractBaggage(meta).Single().Value);
    }

    [Fact]
    public void Skips_malformed_baggage_members_individually()
    {
        // One bad pair must not cost the rest — losing all context because of a stray comma would be
        // a worse outcome than dropping the one entry.
        var meta = Meta((McpMetaKeys.Baggage, "good=1,broken,=noKey,noValue=,alsoGood=2"));

        var baggage = McpTraceContext.ExtractBaggage(meta);

        Assert.Equal(["alsoGood", "good"], baggage.Select(b => b.Key).OrderBy(k => k, StringComparer.Ordinal));
    }

    [Fact]
    public void Absent_baggage_yields_an_empty_list()
    {
        Assert.Empty(McpTraceContext.ExtractBaggage(null));
        Assert.Empty(McpTraceContext.ExtractBaggage(Meta()));
        Assert.Empty(McpTraceContext.ExtractBaggage(Meta((McpMetaKeys.Baggage, "   "))));
    }

    [Fact]
    public void StartServerActivity_parents_the_span_on_the_clients_trace()
    {
        using var source = new ActivitySource("Talent.Mcp.Tests.Trace");
        using var listener = Listen(source);

        var meta = Meta(
            (McpMetaKeys.TraceParent, ValidTraceParent),
            (McpMetaKeys.Baggage, "tenant=acme"));

        using var activity = McpTraceContext.StartServerActivity(source, "tools/call", meta);

        Assert.NotNull(activity);
        Assert.Equal("0af7651916cd43dd8448eb211c80319c", activity.TraceId.ToHexString());
        Assert.Equal("b7ad6b7169203331", activity.ParentSpanId.ToHexString());
        Assert.Equal(ActivityKind.Server, activity.Kind);
        Assert.Equal("acme", activity.GetBaggageItem("tenant"));
    }

    [Fact]
    public void StartServerActivity_still_starts_a_span_without_a_parent()
    {
        using var source = new ActivitySource("Talent.Mcp.Tests.Trace.NoParent");
        using var listener = Listen(source);

        using var activity = McpTraceContext.StartServerActivity(source, "tools/call", meta: null);

        Assert.NotNull(activity);
        Assert.Equal(default, activity.ParentSpanId);
    }

    [Fact]
    public void StartServerActivity_returns_null_when_nothing_is_sampling()
    {
        // The normal ActivitySource contract, and the reason every caller must null-check.
        using var source = new ActivitySource("Talent.Mcp.Tests.Trace.Unsampled");

        Assert.Null(McpTraceContext.StartServerActivity(source, "tools/call", meta: null));
    }

    [Fact]
    public void StartServerActivity_rejects_a_null_source() =>
        Assert.Throws<ArgumentNullException>(
            () => McpTraceContext.StartServerActivity(null!, "tools/call", meta: null));

    private static ActivityListener Listen(ActivitySource source)
    {
        var listener = new ActivityListener
        {
            ShouldListenTo = s => ReferenceEquals(s, source),
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
        };

        ActivitySource.AddActivityListener(listener);

        return listener;
    }

    private static Dictionary<string, JsonElement> Meta(params (string Key, string Value)[] entries)
    {
        var meta = new Dictionary<string, JsonElement>(StringComparer.Ordinal);

        foreach (var (key, value) in entries)
        {
            meta[key] = JsonSerializer.SerializeToElement(value);
        }

        return meta;
    }
}
