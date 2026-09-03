namespace Talent.Mcp.E2E;

using System.Text.RegularExpressions;
using System.Web;
using ModelContextProtocol.Authentication;

/// <summary>
/// Drives Keycloak's real login form over plain <see cref="HttpClient"/> requests, standing in for the
/// browser a human would use — so <c>ClientOAuthOptions.AuthorizationCallbackHandler</c> gets exercised
/// against the actual authorization_code + PKCE flow rather than the password grant the other E2E tests
/// use for convenience (see <c>deploy/keycloak/README.md</c> on why that grant is dev-only and exists
/// for tests, not for this).
/// <para>
/// Verified by hand against the running realm before being written as code (3 Sep 2026): one GET to the
/// authorization endpoint returns the login page directly — no intermediate redirect — and one POST of
/// <c>username</c>/<c>password</c> to the form's own <c>action</c> URL returns a single 302 straight to
/// <c>redirect_uri</c>, carrying <c>code</c>, <c>state</c> and, because this realm's Keycloak advertises
/// <c>authorization_response_iss_parameter_supported</c>, <c>iss</c> per RFC 9207.
/// </para>
/// <para>
/// This scrapes Keycloak's default login theme (a <c>&lt;form id="kc-form-login"&gt;</c> posting
/// <c>username</c>/<c>password</c>), which is real fragility — a theme change would break it silently.
/// It is still the right trade-off here: a full browser (Playwright et al.) would be a heavyweight,
/// flaky dependency for one test class, and Keycloak's login form shape has been stable for years.
/// </para>
/// </summary>
internal static class KeycloakBrowserSimulator
{
    private static readonly Regex FormActionPattern = new(
        "<form[^>]*id=\"kc-form-login\"[^>]*action=\"([^\"]+)\"",
        RegexOptions.Singleline);

    /// <summary>
    /// Logs in as the given user and returns the authorization result Keycloak's redirect carried.
    /// </summary>
    /// <param name="context">The authorization and redirect URIs the SDK is asking to be resolved.</param>
    /// <param name="username">Keycloak username.</param>
    /// <param name="password">Keycloak password.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The parsed <c>code</c>, <c>state</c> and (if present) <c>iss</c>.</returns>
    /// <exception cref="InvalidOperationException">
    /// The login page did not have the expected form, or the login did not end in a redirect to
    /// <paramref name="context"/>'s redirect URI.
    /// </exception>
    public static async Task<AuthorizationResult> SimulateLoginAsync(
        AuthorizationCallbackContext context,
        string username,
        string password,
        CancellationToken cancellationToken)
    {
        // Not HttpClientHandler.CookieContainer: verified by hand (3 Sep 2026) that it silently drops
        // every cookie this flow depends on. Keycloak's auth-session cookies are marked Secure, and
        // .NET's CookieContainer enforces that attribute strictly — it will not attach a Secure cookie
        // to a request against a plain http:// URI, which is exactly what a Testcontainers-mapped
        // Keycloak is. The symptom was not "no cookie sent" but a confusing 400 with a freshly
        // restarted login form, because Keycloak could not find the auth session the POST claimed to
        // continue. A real browser has the same rule but also has HTTPS; this simulator does not, so
        // cookies are captured from Set-Cookie and replayed by hand instead of trusted to a jar.
        using var handler = new HttpClientHandler { AllowAutoRedirect = false };
        using var http = new HttpClient(handler);

        var loginPageResponse = await http
            .GetAsync(context.AuthorizationUri, cancellationToken)
            .ConfigureAwait(false);
        var cookies = ExtractCookies(loginPageResponse);
        var loginPageHtml = await loginPageResponse.Content
            .ReadAsStringAsync(cancellationToken)
            .ConfigureAwait(false);

        var formActionMatch = FormActionPattern.Match(loginPageHtml);
        if (!formActionMatch.Success)
        {
            throw new InvalidOperationException(
                "Keycloak's login page did not contain the expected <form id=\"kc-form-login\" "
                + $"action=\"...\"> — got status {(int)loginPageResponse.StatusCode} for GET "
                + $"{context.AuthorizationUri}, Location='{loginPageResponse.Headers.Location}', body: "
                + loginPageHtml[..Math.Min(loginPageHtml.Length, 2000)]);
        }

        var formAction = HttpUtility.HtmlDecode(formActionMatch.Groups[1].Value);

        using var loginRequest = new HttpRequestMessage(HttpMethod.Post, formAction)
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["username"] = username,
                ["password"] = password,
            }),
        };
        loginRequest.Headers.Add("Cookie", string.Join("; ", cookies.Select(static c => $"{c.Key}={c.Value}")));

        var loginResponse = await http.SendAsync(loginRequest, cancellationToken).ConfigureAwait(false);

        var redirectLocation = loginResponse.Headers.Location;
        if (redirectLocation is null
            || !string.Equals(
                redirectLocation.GetLeftPart(UriPartial.Path),
                context.RedirectUri.GetLeftPart(UriPartial.Path),
                StringComparison.Ordinal))
        {
            var body = await loginResponse.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            throw new InvalidOperationException(
                $"Expected a redirect to {context.RedirectUri}, got status "
                + $"{(int)loginResponse.StatusCode} with Location '{redirectLocation}' for POST "
                + $"{formAction}. Wrong credentials and a Keycloak-side validation error both land "
                + $"here. Body: {body[..Math.Min(body.Length, 2000)]}");
        }

        var query = HttpUtility.ParseQueryString(redirectLocation.Query);
        return new AuthorizationResult
        {
            Code = query["code"] ?? throw new InvalidOperationException(
                $"Redirect to {redirectLocation} carried no 'code' parameter."),
            State = query["state"],
            Iss = query["iss"],
        };
    }

    /// <summary>Parses <c>name=value</c> out of each <c>Set-Cookie</c> header, ignoring every attribute.</summary>
    private static Dictionary<string, string> ExtractCookies(HttpResponseMessage response)
    {
        var cookies = new Dictionary<string, string>(StringComparer.Ordinal);
        if (!response.Headers.TryGetValues("Set-Cookie", out var setCookieHeaders))
        {
            return cookies;
        }

        foreach (var header in setCookieHeaders)
        {
            var nameValue = header.Split(';', 2)[0];
            var separatorIndex = nameValue.IndexOf('=');
            if (separatorIndex > 0)
            {
                cookies[nameValue[..separatorIndex].Trim()] = nameValue[(separatorIndex + 1)..].Trim();
            }
        }

        return cookies;
    }
}
