namespace Talent.Mcp.Toolkit.Tracing;

using System.Diagnostics;
using System.Text.Json;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

/// <summary>
/// Turns every <c>tools/call</c> into an OpenTelemetry span, as an incoming/outgoing message-filter
/// pair rather than per-tool code.
/// <para>
/// <b>Why a message filter and not <c>AddCallToolFilter</c>.</b> That request filter's own XML doc
/// says it only wraps a call to a tool "that isn't found in the <c>McpServerTool</c> collection" — the
/// six tools here are all registered, so it never fires for them (the same reason
/// <c>ToolScopeAuthorizationHandler</c> reads the <c>Mcp-Name</c> HTTP header instead — an ASP.NET-only
/// trick that would not exist on the stdio host). A message filter, registered once in
/// <c>TalentTools.AddTalentTools</c>, wraps every JSON-RPC message on both transports.
/// </para>
/// <para>
/// <b>Why both an incoming and an outgoing filter.</b> <c>McpMessageHandler</c> returns a bare
/// <see cref="Task"/> — the incoming pipeline never hands a filter the response value, because the
/// SDK dispatches incoming requests and sends outgoing responses through two separate filter lists
/// (<c>McpMessageFilters.IncomingFilters</c>/<c>OutgoingFilters</c>). The incoming filter starts the
/// span (it has the request); the outgoing filter reads whether the result was an error and tags
/// <c>tool.output_tokens</c> (it has the response). <c>db.query_time</c> is visible to neither filter
/// directly — see <see cref="ToolTelemetryScope"/>.
/// </para>
/// <para>
/// <b>Correlation is <see cref="MessageContext.Items"/>, not <see cref="RequestId"/> or
/// <see cref="McpServer"/>.</b> Two things were measured (a throwaway probe against
/// <c>WithStreamServerTransport</c>, since there is no public source for either claim): a JSON-RPC
/// request id is scoped to its own client connection, so two different clients are free to both send
/// <c>id: 1</c> — a static dictionary keyed by <see cref="RequestId"/> alone let concurrent calls from
/// different connections collide. Pairing the id with <see cref="MessageContext.Server"/> looked like
/// the fix, but the probe showed <c>context.Server</c> is a <em>different</em>
/// <see cref="McpServer"/> instance for the incoming request than for its own outgoing response — so
/// that key never matched at all, not even for a single connection with no concurrency. What actually
/// is stable across both legs of one call is <see cref="MessageContext.Items"/>: the same dictionary
/// instance flows from the incoming context to the outgoing one for the response it produced,
/// confirmed by the same probe. Stashing the activity there needs no static state and cannot collide
/// across connections, because every request gets its own <c>Items</c> instance.
/// </para>
/// <para>
/// Also records two metrics on <see cref="TalentMeter"/> — <c>talent.tool.duration</c> and
/// <c>talent.tool.errors</c>, both tagged by <c>tool.name</c> — for the "latency per tool" and "tasa de
/// error" Grafana panels. These are Talent-owned rather than a reuse of the SDK's own internal
/// GenAI-semconv instrumentation, which is undocumented in 2.2.0.
/// </para>
/// </summary>
public static class ToolExecutionTelemetry
{
    /// <summary>Cap on the serialized <c>tool.input</c> tag, so a bulk-shortlist call cannot blow up span size.</summary>
    private const int MaxInputTagLength = 2048;

    /// <summary><see cref="MessageContext.Items"/> key the activity is stashed under, incoming to outgoing.</summary>
    private const string ActivityItemKey = "Talent.Mcp.Toolkit.Tracing.ToolExecutionTelemetry.Activity";

    /// <summary>Register with <c>WithMessageFilters(f =&gt; f.AddIncomingFilter(ToolExecutionTelemetry.Incoming))</c>.</summary>
    public static McpMessageFilter Incoming { get; } = static next => async (context, cancellationToken) =>
    {
        if (context.JsonRpcMessage is not JsonRpcRequest { Method: RequestMethods.ToolsCall } request)
        {
            await next(context, cancellationToken).ConfigureAwait(false);
            return;
        }

        var parameters = request.Params?.Deserialize<CallToolRequestParams>(McpJsonUtilities.DefaultOptions);
        var toolName = parameters?.Name ?? "unknown";

        var activity = McpTraceContext.StartServerActivity(
            TalentActivitySource.Instance,
            $"tool.{toolName}",
            parameters?.Meta);

        activity?.SetTag("tool.name", toolName);

        if (activity is not null && parameters?.Arguments is { Count: > 0 } arguments)
        {
            var serialized = JsonSerializer.Serialize(arguments, McpJsonUtilities.DefaultOptions);
            activity.SetTag("tool.input", Truncate(serialized, MaxInputTagLength));
        }

        if (activity is not null)
        {
            context.Items[ActivityItemKey] = activity;
        }

        var scope = new ToolTelemetryScope();
        using var scopeHandle = ToolTelemetryScope.Push(scope);

        try
        {
            await next(context, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // No clean response is coming for the outgoing filter to finish this span from, so this
            // path finishes it itself rather than leaking it.
            if (activity is not null)
            {
                activity.SetStatus(ActivityStatusCode.Error, ex.Message);
                activity.AddException(ex);
                activity.SetTag("db.query_time", scope.TotalDbQueryTime.TotalMilliseconds);
                activity.Dispose();
                context.Items.Remove(ActivityItemKey);

                TalentMeter.ToolErrors.Add(1, new KeyValuePair<string, object?>("tool.name", toolName));
                TalentMeter.ToolDuration.Record(
                    activity.Duration.TotalMilliseconds,
                    new KeyValuePair<string, object?>("tool.name", toolName));
            }

            throw;
        }

        // The happy path — including a business-error CallToolResult with IsError: true, which never
        // throws — leaves the activity in context.Items. The outgoing filter tags and disposes it once
        // the actual response is available.
        activity?.SetTag("db.query_time", scope.TotalDbQueryTime.TotalMilliseconds);
    };

    /// <summary>Register with <c>WithMessageFilters(f =&gt; f.AddOutgoingFilter(ToolExecutionTelemetry.Outgoing))</c>.</summary>
    public static McpMessageFilter Outgoing { get; } = static next => async (context, cancellationToken) =>
    {
        if (context.Items.Remove(ActivityItemKey, out var stashed) && stashed is Activity activity)
        {
            var isError = false;

            switch (context.JsonRpcMessage)
            {
                case JsonRpcError:
                    isError = true;
                    activity.SetStatus(ActivityStatusCode.Error);
                    break;

                case JsonRpcResponse response:
                    var result = response.Result?.Deserialize<CallToolResult>(McpJsonUtilities.DefaultOptions);
                    if (result?.IsError == true)
                    {
                        isError = true;
                        activity.SetStatus(ActivityStatusCode.Error);
                    }

                    activity.SetTag("tool.output_tokens", TokenEstimate.Approximate(response.Result?.ToJsonString()));
                    break;
            }

            var toolName = activity.GetTagItem("tool.name") as string ?? "unknown";
            activity.Dispose();

            TalentMeter.ToolDuration.Record(
                activity.Duration.TotalMilliseconds,
                new KeyValuePair<string, object?>("tool.name", toolName));

            if (isError)
            {
                TalentMeter.ToolErrors.Add(1, new KeyValuePair<string, object?>("tool.name", toolName));
            }
        }

        await next(context, cancellationToken).ConfigureAwait(false);
    };

    private static string Truncate(string value, int maxLength) =>
        value.Length <= maxLength ? value : string.Concat(value.AsSpan(0, maxLength), "…");
}
