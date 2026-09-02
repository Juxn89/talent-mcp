namespace Talent.Mcp.Conformance;

using System.Text.Json;
using Xunit;

/// <summary>
/// AGENTS.md pitfall #24, turned into a pinned assertion: a raw <c>tools/call</c> over Streamable HTTP
/// needs <c>MCP-Protocol-Version</c>, <c>Mcp-Method</c> and <c>Mcp-Name</c> headers, plus
/// <c>_meta/io.modelcontextprotocol/protocolVersion</c> and
/// <c>_meta/io.modelcontextprotocol/clientCapabilities</c> in the body — none of which a hand-rolled
/// request gets for free, unlike the SDK client used by every other suite in this repo.
/// <para>
/// <c>extract_skills</c> is the tool under test throughout: it touches no database and needs no
/// candidate or job to exist, so a request that fails for the wrong reason here can only be the header
/// or <c>_meta</c> omission under test, never a missing seed row.
/// </para>
/// </summary>
[Collection(ConformanceServerCollection.Name)]
public sealed class RawRequestHeaderConformanceTests
{
    private static readonly IReadOnlyDictionary<string, object?> ExtractSkillsArguments =
        new Dictionary<string, object?> { ["text"] = "Five years of C# and PostgreSQL." };

    private readonly ConformanceServerFixture fixture;

    public RawRequestHeaderConformanceTests(ConformanceServerFixture fixture)
    {
        this.fixture = fixture;
    }

    [Fact]
    public async Task A_fully_formed_raw_request_succeeds()
    {
        // The control case: proves the helper builds a request the host actually accepts, so a failure
        // in the tests below is attributable to the one thing each of them omits.
        using var http = this.fixture.CreateHttpClient();
        using var request = RawJsonRpc.ToolsCall(
            this.fixture.McpEndpoint, "extract_skills", ExtractSkillsArguments, nameHeader: "extract_skills");

        using var response = await http.SendAsync(request);
        var (code, _) = await ReadJsonRpcAsync(response);

        Assert.Null(code);
    }

    [Fact]
    public async Task Omitting_the_protocol_version_header_answers_HeaderMismatch()
    {
        using var http = this.fixture.CreateHttpClient();
        using var request = RawJsonRpc.ToolsCall(
            this.fixture.McpEndpoint,
            "extract_skills",
            ExtractSkillsArguments,
            nameHeader: "extract_skills",
            protocolVersionHeader: null);

        using var response = await http.SendAsync(request);
        var (code, message) = await ReadJsonRpcAsync(response);

        Assert.Equal(-32020, code);
        Assert.False(string.IsNullOrWhiteSpace(message));
    }

    [Fact]
    public async Task Omitting_the_Mcp_Name_header_answers_HeaderMismatch()
    {
        using var http = this.fixture.CreateHttpClient();
        using var request = RawJsonRpc.ToolsCall(
            this.fixture.McpEndpoint, "extract_skills", ExtractSkillsArguments, nameHeader: null);

        using var response = await http.SendAsync(request);
        var (code, _) = await ReadJsonRpcAsync(response);

        Assert.Equal(-32020, code);
    }

    [Fact]
    public async Task Omitting_the_meta_protocol_version_field_answers_InvalidParams()
    {
        using var http = this.fixture.CreateHttpClient();
        using var request = RawJsonRpc.ToolsCall(
            this.fixture.McpEndpoint,
            "extract_skills",
            ExtractSkillsArguments,
            nameHeader: "extract_skills",
            metaProtocolVersion: false);

        using var response = await http.SendAsync(request);
        var (code, _) = await ReadJsonRpcAsync(response);

        Assert.Equal(-32602, code);
    }

    [Fact]
    public async Task Omitting_the_meta_client_capabilities_field_answers_InvalidParams()
    {
        using var http = this.fixture.CreateHttpClient();
        using var request = RawJsonRpc.ToolsCall(
            this.fixture.McpEndpoint,
            "extract_skills",
            ExtractSkillsArguments,
            nameHeader: "extract_skills",
            metaClientCapabilities: false);

        using var response = await http.SendAsync(request);
        var (code, _) = await ReadJsonRpcAsync(response);

        Assert.Equal(-32602, code);
    }

    /// <summary>Reads a JSON-RPC response, whether framed as plain JSON or as a single SSE event.</summary>
    /// <param name="response">The HTTP response.</param>
    /// <returns>The error code when the response carries one, and the message; both null on success.</returns>
    private static async Task<(int? Code, string? Message)> ReadJsonRpcAsync(HttpResponseMessage response)
    {
        var raw = await response.Content.ReadAsStringAsync();

        var json = raw.Contains("data:", StringComparison.Ordinal)
            ? string.Join(
                Environment.NewLine,
                raw.Split('\n')
                    .Where(static line => line.StartsWith("data:", StringComparison.Ordinal))
                    .Select(static line => line["data:".Length..].Trim()))
            : raw;

        using var document = JsonDocument.Parse(json);

        if (!document.RootElement.TryGetProperty("error", out var error))
        {
            return (null, null);
        }

        return (
            error.GetProperty("code").GetInt32(),
            error.TryGetProperty("message", out var message) ? message.GetString() : null);
    }
}
