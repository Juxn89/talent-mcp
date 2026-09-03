namespace Talent.Mcp.E2E;

using ModelContextProtocol.Authentication;
using ModelContextProtocol.Client;
using Xunit;

/// <summary>
/// The real authorization_code + PKCE flow, driven end to end by the MCP client SDK against a real
/// Keycloak — not the password grant <see cref="RealServerFixture.MintTokenAsync"/> uses elsewhere in
/// this suite for convenience (see its own remarks on why that grant is dev-only).
/// <para>
/// This is what AGENTS.md's F3 checklist means by "use <c>ClientOAuthOptions.AuthorizationCallbackHandler</c>
/// in the demo client": <c>Talent.Mcp.E2E</c> <em>is</em> that demo client for this project — there is no
/// separately published one (see the "Published artifacts" table in AGENTS.md) — and this test is where
/// the callback handler, the PKCE S256 challenge (ADR requires it; the realm refuses <c>plain</c>), and
/// RFC 9207 issuer validation actually run, rather than being asserted only by reading the SDK's source.
/// </para>
/// </summary>
[Collection(RealServerCollection.Name)]
public sealed class AuthorizationCodeE2ETests
{
    private readonly RealServerFixture fixture;

    public AuthorizationCodeE2ETests(RealServerFixture fixture)
    {
        this.fixture = fixture;
    }

    [Fact]
    public async Task The_authorization_code_flow_reaches_a_scoped_tool_and_records_the_RFC9207_issuer()
    {
        string? capturedIssuer = null;

        var oauthOptions = new ClientOAuthOptions
        {
            ClientId = "talent-mcp-client",

            // Never actually bound to a listener — nothing needs to receive this redirect over the
            // network, because AuthorizationCallbackHandler below intercepts Keycloak's redirect
            // response directly and never lets the HTTP client follow it. Any URI the realm's
            // "http://127.0.0.1:*" pattern (deploy/keycloak/realm.json) accepts will do.
            RedirectUri = new Uri("http://127.0.0.1:44444/callback"),

            // Fallback only — Keycloak's WWW-Authenticate / protected-resource-metadata response
            // supplies the real scope list (see McpAuthenticationEvents.OnResourceMetadataRequest in
            // TalentAuthenticationServiceCollectionExtensions), so this is what gets requested only if
            // that discovery step is skipped for some reason.
            Scopes = ["talent.jobs.read"],

            // The SDK appends offline_access automatically whenever the authorization server's own
            // metadata advertises it as supported — true for this realm, since it is a built-in
            // optional scope realm-wide. But talent-mcp-client's own optionalClientScopes
            // (deploy/keycloak/realm.json) does not include it — no refresh-token flow is demonstrated
            // here — so requesting it produces invalid_scope for the whole request, all four talent.*
            // scopes included. Filtering it back out client-side is the SDK's own documented escape
            // hatch for exactly this mismatch (see ClientOAuthOptions.ScopeSelector).
            ScopeSelector = static candidates =>
                candidates?.Where(static scope => scope != "offline_access"),

            AuthorizationCallbackHandler = async (context, cancellationToken) =>
            {
                var result = await KeycloakBrowserSimulator
                    .SimulateLoginAsync(context, "recruiter", "recruiter", cancellationToken)
                    .ConfigureAwait(false);

                // RFC 9207: the redirect carried its own issuer, independent of what this test already
                // knows the issuer to be. Capturing it here, rather than asserting inside the handler,
                // keeps the assertion below in the test body where a failure is easy to find.
                capturedIssuer = result.Iss;
                return result;
            },
        };

        var transport = new HttpClientTransport(new HttpClientTransportOptions
        {
            Endpoint = this.fixture.McpEndpoint,
            OAuth = oauthOptions,
        });

        // McpClient.CreateAsync itself drives the 401 -> discover -> authorize -> exchange -> retry
        // sequence; by the time this returns, the whole flow already ran.
        await using var client = await McpClient.CreateAsync(transport);

        var result = await client.CallToolAsync("search_jobs", new Dictionary<string, object?>());

        Assert.False(result.IsError is true);
        Assert.Equal(this.fixture.Authority.ToString(), capturedIssuer);
    }
}
