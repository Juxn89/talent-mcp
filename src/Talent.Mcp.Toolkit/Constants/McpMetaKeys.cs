namespace Talent.Mcp.Toolkit.Constants;

/// <summary>
/// Well-known keys inside an MCP request's <c>_meta</c> object.
/// <para>
/// These live in the toolkit rather than beside the recruitment tool names, because they are protocol
/// vocabulary: nothing here mentions a job or a candidate, and a second MCP server would want the same
/// strings. Tool names stay in the presentation layer, where the wire contract they pin belongs.
/// </para>
/// </summary>
public static class McpMetaKeys
{
    /// <summary>The reverse-DNS prefix the specification reserves for its own <c>_meta</c> keys.</summary>
    public const string SpecificationPrefix = "io.modelcontextprotocol/";

    /// <summary>Protocol revision the client is speaking.</summary>
    public const string ProtocolVersion = SpecificationPrefix + "protocolVersion";

    /// <summary>Capabilities the client declares, now that there is no <c>initialize</c> handshake.</summary>
    public const string ClientCapabilities = SpecificationPrefix + "clientCapabilities";

    /// <summary>
    /// Per-request log level. This is where the level arrives now that the MCP Logging API is
    /// deprecated (<c>MCP9005</c>, SEP-2577).
    /// </summary>
    public const string LogLevel = SpecificationPrefix + "logLevel";

    /// <summary>
    /// W3C Trace Context <c>traceparent</c>. Unprefixed on purpose: the specification's tracing
    /// convention reuses the W3C header names verbatim rather than namespacing them.
    /// </summary>
    public const string TraceParent = "traceparent";

    /// <summary>W3C Trace Context <c>tracestate</c>.</summary>
    public const string TraceState = "tracestate";

    /// <summary>W3C Baggage.</summary>
    public const string Baggage = "baggage";
}
