namespace Talent.Mcp.Conformance;

using System.Net;
using Talent.Mcp.Tools.Constants;
using Xunit;

/// <summary>
/// ADR-0001's "one conformance test that must be rewritten before it is written": the plan asked for
/// "downgrade negotiation against a 2025-11-25 client", but under <c>SessionMode.Stateless</c> there is
/// no downgrade — that client is served exactly like any other. What is actually observable, and what
/// this suite asserts instead, is the ADR's own consequence list: no <c>Mcp-Session-Id</c> is minted or
/// echoed, GET and DELETE on <c>/mcp</c> both answer <c>405</c>, and a 2025-11-25 client still gets the
/// full six-tool surface rather than a reduced one.
/// </summary>
[Collection(ConformanceServerCollection.Name)]
public sealed class StatelessTransportConformanceTests
{
    private readonly ConformanceServerFixture fixture;

    public StatelessTransportConformanceTests(ConformanceServerFixture fixture)
    {
        this.fixture = fixture;
    }

    [Fact]
    public async Task A_2025_11_25_client_is_served_the_full_tool_surface_statelessly()
    {
        await using var client = await this.fixture
            .CreateClientAsync(protocolVersion: Mcp.ProtocolVersions.Interop[0]);

        var result = await client.ListToolsAsync(new ModelContextProtocol.Protocol.ListToolsRequestParams());

        Assert.Equal(Mcp.ToolNames.All.Length, result.Tools.Count);
    }

    [Fact]
    public async Task No_session_id_is_minted_or_echoed_for_a_valid_call()
    {
        using var http = this.fixture.CreateHttpClient();
        using var request = RawJsonRpc.ToolsCall(
            this.fixture.McpEndpoint,
            "extract_skills",
            new Dictionary<string, object?> { ["text"] = "C#" },
            nameHeader: "extract_skills");

        using var response = await http.SendAsync(request);

        // SEP-2567 removed the header outright — under Stateless there is nothing to echo, for a
        // 2026-07-28 request or an interop one.
        Assert.False(response.Headers.Contains("Mcp-Session-Id"));
    }

    [Fact]
    public async Task GET_on_the_mcp_endpoint_is_refused_with_405()
    {
        using var http = this.fixture.CreateHttpClient();

        using var response = await http.GetAsync(this.fixture.McpEndpoint);

        Assert.Equal(HttpStatusCode.MethodNotAllowed, response.StatusCode);
    }

    [Fact]
    public async Task DELETE_on_the_mcp_endpoint_is_refused_with_405()
    {
        using var http = this.fixture.CreateHttpClient();

        using var response = await http.DeleteAsync(this.fixture.McpEndpoint);

        Assert.Equal(HttpStatusCode.MethodNotAllowed, response.StatusCode);
    }
}
