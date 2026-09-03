namespace Talent.Mcp.E2E;

using System.Net;
using ModelContextProtocol.Client;
using Xunit;

/// <summary>
/// Scope enforcement over a real HTTP connection to the real host, against a real Keycloak: no token,
/// a token missing the required scope, and a token that carries it — the three cases
/// <c>ToolScopeAuthorizationHandler</c> exists to tell apart.
/// <para>
/// <c>reject_candidate</c> is the destructive tool and the one AGENTS.md's F3 checklist names
/// explicitly for the denial case. <c>search_jobs</c> stands in for the read case, so this suite is not
/// only ever exercising the one tool with the narrowest scope.
/// </para>
/// </summary>
[Collection(RealServerCollection.Name)]
public sealed class OAuthScopeE2ETests
{
    private static readonly Guid CandidateId = Guid.Parse("b0000005-0000-0000-0000-000000000000");

    private readonly RealServerFixture fixture;

    public OAuthScopeE2ETests(RealServerFixture fixture)
    {
        this.fixture = fixture;
    }

    [Fact]
    public async Task No_token_is_refused_before_any_tool_runs()
    {
        // McpClient.CreateAsync's own connection handshake (tools negotiation) is what surfaces the
        // 401 — there is no separate "connect, then call" step under stateless HTTP to fail later.
        var exception = await Assert.ThrowsAsync<HttpRequestException>(
            () => this.fixture.CreateClientAsync());

        Assert.Equal(HttpStatusCode.Unauthorized, exception.StatusCode);
    }

    [Fact]
    public async Task A_token_missing_the_required_scope_is_denied_for_the_destructive_tool()
    {
        // "openid" alone is what Keycloak always grants regardless of what is asked for (acr, basic,
        // profile, email, roles, web-origins are default client scopes) — none of the four talent.*
        // scopes are in it unless requested. See deploy/keycloak/README.md on why that is deliberate.
        //
        // The denial happens in ASP.NET Core's own authorization middleware, ahead of the MCP
        // endpoint — the same layer that produces the 401 in the no-token case above — so it never
        // becomes a JSON-RPC error result; it is a bare 403 the transport surfaces as an exception, the
        // same way the SDK's own ClientOAuthProvider.GetAccessTokenAsync treats 403 as an auth
        // challenge alongside 401 rather than as an ordinary tool failure.
        var token = await this.fixture.MintTokenAsync("openid");
        await using var client = await this.fixture.CreateClientAsync(token);

        var exception = await Assert.ThrowsAsync<HttpRequestException>(() => client.CallToolAsync(
            "reject_candidate",
            new Dictionary<string, object?>
            {
                ["candidateId"] = CandidateId,
                ["reason"] = "Testing scope denial, not a real rejection.",
            }).AsTask());

        Assert.Equal(HttpStatusCode.Forbidden, exception.StatusCode);
    }

    [Fact]
    public async Task A_token_missing_the_required_scope_is_denied_for_a_read_tool()
    {
        var token = await this.fixture.MintTokenAsync("openid talent.candidates.reject");
        await using var client = await this.fixture.CreateClientAsync(token);

        var exception = await Assert.ThrowsAsync<HttpRequestException>(
            () => client.CallToolAsync("search_jobs", new Dictionary<string, object?>()).AsTask());

        Assert.Equal(HttpStatusCode.Forbidden, exception.StatusCode);
    }

    [Fact]
    public async Task A_token_carrying_the_required_scope_is_allowed()
    {
        var token = await this.fixture.MintTokenAsync("openid talent.jobs.read");
        await using var client = await this.fixture.CreateClientAsync(token);

        var result = await client.CallToolAsync("search_jobs", new Dictionary<string, object?>());

        Assert.False(result.IsError is true);
    }
}
