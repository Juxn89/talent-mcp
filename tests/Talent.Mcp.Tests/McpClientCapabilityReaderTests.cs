namespace Talent.Mcp.Tests;

using System.Text.Json.Nodes;
using ModelContextProtocol.Protocol;
using Talent.Mcp.Toolkit;
using Talent.Mcp.Toolkit.Constants;
using Xunit;

/// <summary>
/// Reading client capabilities from where 2026-07-28 actually puts them.
/// <para>
/// These exist because of a measured host divergence, not for completeness. Under the stateless HTTP
/// host <c>McpServer.ClientCapabilities</c> is null for every request even when the body declares
/// capabilities, so a server consulting only that property concludes every HTTP client can do nothing —
/// and any capability-gated feature silently stops working on the host that matters. See
/// docs/verification/sdk-2.2.0-tool-surface-behaviour.md.
/// </para>
/// </summary>
public sealed class McpClientCapabilityReaderTests
{
    private static JsonObject MetaDeclaring(params string[] capabilities)
    {
        var declared = new JsonObject();
        foreach (var capability in capabilities)
        {
            declared[capability] = new JsonObject();
        }

        return new JsonObject { [McpMetaKeys.ClientCapabilities] = declared };
    }

    [Fact]
    public void Session_capabilities_alone_are_enough()
    {
        var session = new ClientCapabilities { Elicitation = new ElicitationCapability() };

        Assert.True(McpClientCapabilityReader.DeclaresElicitation(session, requestMeta: null));
    }

    [Fact]
    public void Request_metadata_alone_is_enough()
    {
        // The stateless HTTP case: no session to have learned anything, and the client declares itself
        // in each request instead.
        Assert.True(McpClientCapabilityReader.DeclaresElicitation(
            sessionCapabilities: null,
            MetaDeclaring("elicitation")));
    }

    [Fact]
    public void Neither_source_means_no()
    {
        Assert.False(McpClientCapabilityReader.DeclaresElicitation(
            new ClientCapabilities(),
            MetaDeclaring("sampling")));
    }

    [Fact]
    public void An_empty_capability_object_still_counts_as_declared()
    {
        // "elicitation": {} is the normal form — supported, no options. Reading the value as falsy
        // would reject the most common shape on the wire.
        var meta = new JsonObject
        {
            [McpMetaKeys.ClientCapabilities] = new JsonObject { ["elicitation"] = new JsonObject() },
        };

        Assert.True(McpClientCapabilityReader.Declares(meta, "elicitation"));
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("""{"io.modelcontextprotocol/clientCapabilities":null}""")]
    [InlineData("""{"io.modelcontextprotocol/clientCapabilities":"elicitation"}""")]
    [InlineData("""{"io.modelcontextprotocol/clientCapabilities":[]}""")]
    [InlineData("""{"io.modelcontextprotocol/clientCapabilities":{"elicitation":null}}""")]
    public void Malformed_metadata_costs_a_capability_not_the_request(string metaJson)
    {
        var meta = JsonNode.Parse(metaJson)!.AsObject();

        // Every one of these returns false rather than throwing. Failing towards the degraded path is
        // the safe direction: a client that sends nonsense gets asked for an explicit confirmation
        // instead of having a destructive operation silently gated open or the request torn down.
        Assert.False(McpClientCapabilityReader.Declares(meta, "elicitation"));
        Assert.False(McpClientCapabilityReader.DeclaresElicitation(null, meta));
    }

    [Fact]
    public void A_null_meta_object_is_not_a_declaration()
    {
        Assert.False(McpClientCapabilityReader.Declares(requestMeta: null, "elicitation"));
    }

    [Fact]
    public void The_capability_name_must_be_supplied()
    {
        Assert.Throws<ArgumentException>(
            () => McpClientCapabilityReader.Declares(MetaDeclaring("elicitation"), "  "));
    }
}
