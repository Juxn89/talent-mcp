namespace Talent.Mcp.Toolkit;

using System.Text.Json.Nodes;
using ModelContextProtocol.Protocol;
using Talent.Mcp.Toolkit.Constants;

/// <summary>
/// Reads what a client says it can do, from wherever this revision actually puts it.
/// <para>
/// <b>Why this exists.</b> Under 2026-07-28 there is no <c>initialize</c> handshake (SEP-2575), so
/// there is no session in which to remember a client's capabilities — they arrive as per-request
/// metadata under <see cref="McpMetaKeys.ClientCapabilities"/>. Measured 2 Sep 2026 against the
/// stateless Streamable HTTP host: <c>McpServer.ClientCapabilities</c> is <see langword="null"/> for
/// every request, <em>even when the body declares capabilities in <c>_meta</c></em>, because that
/// property is the pre-2026 session-level notion. Only <c>IsMrtrSupported</c> is populated, and it
/// answers a different question.
/// </para>
/// <para>
/// A server that asks <c>ClientCapabilities</c> alone therefore concludes that every HTTP client can do
/// nothing — which silently disables any capability-gated feature on the host that matters most. Hence
/// this reader: consult the session-level property when the transport populates it, and the request's
/// own <c>_meta</c> when it does not.
/// </para>
/// </summary>
public static class McpClientCapabilityReader
{
    /// <summary>Capability name for elicitation, as it appears in <c>_meta</c>.</summary>
    public const string ElicitationCapability = "elicitation";

    /// <summary>
    /// Whether the client can be asked a question through elicitation.
    /// </summary>
    /// <param name="sessionCapabilities">
    /// The transport's session-level view, or <see langword="null"/>. Populated over stdio, and null
    /// under stateless HTTP.
    /// </param>
    /// <param name="requestMeta">The request's <c>_meta</c> object.</param>
    /// <returns>Whether elicitation was declared by either source.</returns>
    public static bool DeclaresElicitation(
        ClientCapabilities? sessionCapabilities,
        JsonObject? requestMeta) =>
        sessionCapabilities?.Elicitation is not null || Declares(requestMeta, ElicitationCapability);

    /// <summary>
    /// Whether the request's <c>_meta</c> declares a named capability.
    /// </summary>
    /// <param name="requestMeta">The request's <c>_meta</c> object.</param>
    /// <param name="capability">Capability name, for example <c>elicitation</c>.</param>
    /// <returns>Whether the capability is present.</returns>
    /// <remarks>
    /// Presence of the key is the test, not its value. The protocol shape is an object per capability
    /// (<c>"elicitation": {}</c>), so an empty object means "supported, no options" — reading it as
    /// falsy would reject the most common form.
    /// </remarks>
    public static bool Declares(JsonObject? requestMeta, string capability)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(capability);

        if (requestMeta is null
            || !requestMeta.TryGetPropertyValue(McpMetaKeys.ClientCapabilities, out var node)
            || node is not JsonObject capabilities)
        {
            return false;
        }

        // Malformed metadata costs the caller a capability, never the request. A client that sends
        // nonsense here gets the degraded path, which is the safe direction to fail in.
        return capabilities.TryGetPropertyValue(capability, out var declared) && declared is not null;
    }
}
