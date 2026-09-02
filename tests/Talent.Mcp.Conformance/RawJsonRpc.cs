namespace Talent.Mcp.Conformance;

using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Talent.Mcp.Toolkit.Constants;
using Talent.Mcp.Tools.Constants;

/// <summary>
/// Builds hand-rolled Streamable HTTP JSON-RPC requests against <c>/mcp</c>.
/// <para>
/// The SDK client always sets every header and <c>_meta</c> field a request needs; the point of this
/// helper is to withhold one deliberately, which only a raw request can do. See AGENTS.md pitfall #24.
/// </para>
/// </summary>
internal static class RawJsonRpc
{
    /// <summary>
    /// Builds a <c>tools/call</c> request, with every required header and <c>_meta</c> field present by
    /// default so a caller can omit exactly one thing at a time.
    /// </summary>
    /// <param name="endpoint">The <c>/mcp</c> endpoint.</param>
    /// <param name="toolName">Wire name of the tool to call.</param>
    /// <param name="arguments">Tool arguments.</param>
    /// <param name="nameHeader">
    /// The <c>Mcp-Name</c> header value. No default: every caller has an opinion about it, since it is
    /// the header the missing-header test omits.
    /// </param>
    /// <param name="protocolVersionHeader">
    /// The <c>MCP-Protocol-Version</c> header value, or <see langword="null"/> to omit it.
    /// </param>
    /// <param name="methodHeader">
    /// The <c>Mcp-Method</c> header value, or <see langword="null"/> to omit it.
    /// </param>
    /// <param name="metaProtocolVersion">
    /// Whether to include <c>_meta/io.modelcontextprotocol/protocolVersion</c> in the body.
    /// </param>
    /// <param name="metaClientCapabilities">
    /// Whether to include <c>_meta/io.modelcontextprotocol/clientCapabilities</c> in the body.
    /// </param>
    /// <returns>The request, ready to send.</returns>
    public static HttpRequestMessage ToolsCall(
        Uri endpoint,
        string toolName,
        IReadOnlyDictionary<string, object?> arguments,
        string? nameHeader,
        string? protocolVersionHeader = Mcp.ProtocolVersions.Supported,
        string? methodHeader = "tools/call",
        bool metaProtocolVersion = true,
        bool metaClientCapabilities = true)
    {
        var meta = new JsonObject();
        if (metaProtocolVersion)
        {
            meta[McpMetaKeys.ProtocolVersion] = Mcp.ProtocolVersions.Supported;
        }

        if (metaClientCapabilities)
        {
            meta[McpMetaKeys.ClientCapabilities] = new JsonObject();
        }

        var argumentsNode = new JsonObject();
        foreach (var (key, value) in arguments)
        {
            argumentsNode[key] = value switch
            {
                null => null,
                string s => s,
                bool b => b,
                int i => i,
                _ => JsonSerializer.SerializeToNode(value),
            };
        }

        var body = new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = 1,
            ["method"] = "tools/call",
            ["params"] = new JsonObject
            {
                ["name"] = toolName,
                ["arguments"] = argumentsNode,
                ["_meta"] = meta,
            },
        };

        var request = new HttpRequestMessage(HttpMethod.Post, endpoint)
        {
            Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"),
        };

        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));

        if (protocolVersionHeader is not null)
        {
            request.Headers.Add("MCP-Protocol-Version", protocolVersionHeader);
        }

        if (methodHeader is not null)
        {
            request.Headers.Add("Mcp-Method", methodHeader);
        }

        if (nameHeader is not null)
        {
            request.Headers.Add("Mcp-Name", nameHeader);
        }

        return request;
    }
}
