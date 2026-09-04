namespace Talent.Mcp.Toolkit.Tracing;

using System.Diagnostics;
using System.Text.Json.Nodes;
using Talent.Mcp.Toolkit.Constants;

/// <summary>
/// Extracts W3C Trace Context out of an MCP request's <c>_meta</c> so a server span becomes a child of
/// the client's span.
/// <para>
/// This is how a single trace spans client to server to Postgres. It matters more under this revision
/// than it used to: the MCP Logging API is deprecated, so OpenTelemetry is the only observability
/// channel an HTTP host has, and without propagation every request starts a fresh, unrelated trace.
/// </para>
/// <para>
/// Nothing here is recruitment-specific, which is why it is in the toolkit.
/// </para>
/// <para>
/// <b>Takes <see cref="JsonObject"/>, not a <c>JsonElement</c> dictionary.</b> An earlier revision of
/// this type was built against <c>IReadOnlyDictionary&lt;string, JsonElement&gt;</c>, which does not
/// match how <c>_meta</c> actually reaches any caller: <see cref="ModelContextProtocol.Protocol.RequestParams.Meta"/>
/// — what a tool sees as <c>context.Params?.Meta</c> — is a <see cref="JsonObject"/>, the same type
/// <see cref="McpClientCapabilityReader"/> already consumes. That mismatch is why this type had unit
/// tests but zero production callers until F4 wired it into <c>ToolExecutionTelemetry</c>.
/// </para>
/// </summary>
public static class McpTraceContext
{
    /// <summary>
    /// Reads <c>traceparent</c> and <c>tracestate</c> from a <c>_meta</c> object.
    /// </summary>
    /// <param name="meta">The request's <c>_meta</c>, or <see langword="null"/> when absent.</param>
    /// <param name="context">The parsed context when the metadata carried a usable one.</param>
    /// <returns>
    /// <see langword="true"/> when a valid <c>traceparent</c> was present. A missing or malformed value
    /// returns <see langword="false"/> rather than throwing: a client that sends a bad header should get
    /// its tool call served with a fresh trace, not an error. Losing a trace link is an observability
    /// problem; failing the call would make it a correctness one.
    /// </returns>
    public static bool TryExtract(JsonObject? meta, out ActivityContext context)
    {
        context = default;

        if (meta is null)
        {
            return false;
        }

        var traceParent = ReadString(meta, McpMetaKeys.TraceParent);
        if (string.IsNullOrWhiteSpace(traceParent))
        {
            return false;
        }

        var traceState = ReadString(meta, McpMetaKeys.TraceState);

        // isRemote: true — the parent came from another process, which is what makes the resulting
        // server span a child rather than a continuation of a local one.
        return ActivityContext.TryParse(traceParent, traceState, isRemote: true, out context);
    }

    /// <summary>
    /// Reads W3C Baggage entries from a <c>_meta</c> object.
    /// </summary>
    /// <param name="meta">The request's <c>_meta</c>, or <see langword="null"/> when absent.</param>
    /// <returns>
    /// The parsed key/value pairs, or an empty list. Malformed entries are skipped individually rather
    /// than discarding the whole header, so one bad pair does not cost the rest.
    /// </returns>
    public static IReadOnlyList<KeyValuePair<string, string>> ExtractBaggage(JsonObject? meta)
    {
        if (meta is null)
        {
            return [];
        }

        var raw = ReadString(meta, McpMetaKeys.Baggage);
        if (string.IsNullOrWhiteSpace(raw))
        {
            return [];
        }

        var entries = new List<KeyValuePair<string, string>>();

        foreach (var member in raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            // Per the W3C Baggage spec a member may carry properties after a ';'. They are metadata
            // about the entry, not part of its value, so they are dropped.
            var withoutProperties = member.Split(';', 2)[0];

            var separator = withoutProperties.IndexOf('=', StringComparison.Ordinal);
            if (separator <= 0 || separator == withoutProperties.Length - 1)
            {
                continue;
            }

            var key = withoutProperties[..separator].Trim();
            var value = withoutProperties[(separator + 1)..].Trim();

            if (key.Length == 0 || value.Length == 0)
            {
                continue;
            }

            entries.Add(new KeyValuePair<string, string>(key, Uri.UnescapeDataString(value)));
        }

        return entries;
    }

    /// <summary>
    /// Starts an activity linked to the client's trace when <c>_meta</c> carried one, and an unparented
    /// activity otherwise.
    /// </summary>
    /// <param name="source">The activity source to start from.</param>
    /// <param name="name">Span name.</param>
    /// <param name="meta">The request's <c>_meta</c>.</param>
    /// <param name="kind">Span kind; server by default, which is what a tool invocation is.</param>
    /// <returns>
    /// The started activity, or <see langword="null"/> when no listener is sampling — the normal
    /// contract of <see cref="ActivitySource"/>, and the reason every caller must null-check.
    /// </returns>
    /// <exception cref="ArgumentNullException">The source was <see langword="null"/>.</exception>
    public static Activity? StartServerActivity(
        ActivitySource source,
        string name,
        JsonObject? meta,
        ActivityKind kind = ActivityKind.Server)
    {
        ArgumentNullException.ThrowIfNull(source);

        var activity = TryExtract(meta, out var parent)
            ? source.StartActivity(name, kind, parent)
            : source.StartActivity(name, kind);

        if (activity is null)
        {
            return null;
        }

        foreach (var entry in ExtractBaggage(meta))
        {
            activity.SetBaggage(entry.Key, entry.Value);
        }

        return activity;
    }

    private static string? ReadString(JsonObject meta, string key)
    {
        if (!meta.TryGetPropertyValue(key, out var node) || node is not JsonValue value)
        {
            return null;
        }

        return value.TryGetValue<string>(out var text) ? text : null;
    }
}
