namespace Talent.Mcp.Conformance;

using System.Text.Json;
using System.Web;
using Xunit;

/// <summary>
/// The two S256 assertions <c>deploy/keycloak/README.md</c> calls out as the ones that actually matter,
/// against a real Keycloak importing the real realm — not assumptions about how PKCE enforcement in
/// Keycloak 26.7.2 behaves.
/// <para>
/// <b>Why not assert <c>code_challenge_methods_supported == ["S256"]</c>.</b> Keycloak advertises
/// <c>["plain", "S256"]</c> realm-wide and does not let a realm remove <c>plain</c> from its own
/// discovery document; enforcement is per-client instead. So the meaningful pair of assertions is: S256
/// is present in the realm's advertised list (the SDK's actual requirement — it refuses an authorization
/// server whose metadata omits it), and separately, <c>talent-mcp-client</c> specifically refuses a
/// <c>plain</c> challenge even though the realm still lists it as an option in general.
/// </para>
/// </summary>
[Collection(KeycloakOnlyCollection.Name)]
public sealed class KeycloakMetadataConformanceTests
{
    private readonly KeycloakOnlyFixture fixture;
    private readonly HttpClient httpClient = new();

    public KeycloakMetadataConformanceTests(KeycloakOnlyFixture fixture)
    {
        this.fixture = fixture;
    }

    [Fact]
    public async Task The_realm_advertises_S256_alongside_plain()
    {
        var response = await this.httpClient
            .GetAsync(new Uri($"{this.fixture.Authority}/.well-known/openid-configuration"));
        response.EnsureSuccessStatusCode();

        using var document = JsonDocument.Parse(await response.Content.ReadAsStreamAsync());
        var methods = document.RootElement
            .GetProperty("code_challenge_methods_supported")
            .EnumerateArray()
            .Select(static e => e.GetString())
            .ToArray();

        // The SDK's own requirement (verified F0, deploy/keycloak/README.md): it refuses an
        // authorization server whose metadata omits S256. It does not require the list to be exactly
        // ["S256"] — Keycloak never advertises that, so asserting equality would fail against a
        // correctly configured realm rather than a broken one.
        Assert.Contains("S256", methods);
    }

    [Fact]
    public async Task Talent_mcp_client_refuses_a_plain_challenge_even_though_the_realm_lists_it()
    {
        var redirectUri = "http://127.0.0.1:44444/callback";
        var query = new Dictionary<string, string?>
        {
            ["client_id"] = "talent-mcp-client",
            ["response_type"] = "code",
            ["redirect_uri"] = redirectUri,
            ["scope"] = "openid",
            ["state"] = "conformance-plain-challenge",

            // A syntactically valid S256-length code_challenge value; its content is irrelevant here —
            // pkce.code.challenge.method: S256 on the client (deploy/keycloak/realm.json) is what
            // authorization_code_challenge_method_supported has to reject before token exchange is
            // ever reached, no matter what the challenge value itself is.
            ["code_challenge"] = "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQ",
            ["code_challenge_method"] = "plain",
        };

        var authorizationEndpoint = new Uri($"{this.fixture.Authority}/protocol/openid-connect/auth");
        var requestUri = new Uri(authorizationEndpoint + "?" + string.Join(
            '&',
            query.Select(static kv => $"{Uri.EscapeDataString(kv.Key)}={Uri.EscapeDataString(kv.Value!)}")));

        using var handler = new HttpClientHandler { AllowAutoRedirect = false };
        using var client = new HttpClient(handler);
        var response = await client.GetAsync(requestUri);

        // Keycloak rejects a code_challenge_method that does not match the client's configured one
        // before ever rendering a login page — a redirect straight back to redirect_uri carrying an
        // OAuth error, not a 200 login form and not a server error.
        Assert.Equal(System.Net.HttpStatusCode.Found, response.StatusCode);
        Assert.NotNull(response.Headers.Location);

        var errorQuery = HttpUtility.ParseQueryString(response.Headers.Location!.Query);
        Assert.Equal("invalid_request", errorQuery["error"]);
        Assert.Contains("code challenge method", errorQuery["error_description"], StringComparison.OrdinalIgnoreCase);
    }
}
