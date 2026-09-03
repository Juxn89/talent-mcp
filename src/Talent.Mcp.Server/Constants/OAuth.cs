namespace Talent.Mcp.Server.Constants;

using Talent.Mcp.Tools.Constants;

/// <summary>
/// OAuth 2.1 identity for this server, and the scope-per-tool lookup
/// <see cref="Talent.Mcp.Server.Authentication.ToolScopeAuthorizationHandler"/> checks a caller's token
/// against.
/// <para>
/// Only what identifies <em>this</em> resource server is a constant here. The issuer is not: it
/// differs between <c>docker compose</c>, Testcontainers and production, so it lives in configuration
/// (see <see cref="Talent.Mcp.Server.Authentication.TalentAuthenticationServiceCollectionExtensions"/>).
/// The scope values themselves are not duplicated here either — they are
/// <see cref="Talent.Mcp.Tools.Constants.Mcp.OAuthScopes"/>, a fact about the tool surface that both
/// hosts load; this class only maps each tool name to the scope it needs.
/// </para>
/// </summary>
public static class OAuth
{
    /// <summary>
    /// The audience this server validates incoming tokens against — the client id Keycloak's
    /// <c>oidc-audience-mapper</c> puts into <c>aud</c> for <c>talent-mcp-client</c>. See
    /// <c>deploy/keycloak/realm.json</c>.
    /// </summary>
    public const string ClientId = "talent-mcp-server";

    /// <summary>The only PKCE challenge method this server's realm accepts from its client. Mandatory per the 2026-07-28 revision.</summary>
    public const string CodeChallengeMethod = "S256";

    /// <summary>Claim type carrying the space-delimited scope list, per RFC 8693 and Keycloak's default mapper.</summary>
    public const string ScopeClaimType = "scope";

    /// <summary>
    /// The scope required to call each tool, by wire name.
    /// <para>
    /// One blanket scope for the whole server would make the destructive-tool denial test meaningless —
    /// see <c>deploy/keycloak/README.md</c> on why scopes are optional rather than default. This map is
    /// what <see cref="Talent.Mcp.Server.Authentication.ToolScopeAuthorizationHandler"/> checks a
    /// caller's token against.
    /// </para>
    /// </summary>
    public static readonly IReadOnlyDictionary<string, string> RequiredScopeByToolName =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [Mcp.ToolNames.SearchJobs] = Mcp.OAuthScopes.JobsRead,
            [Mcp.ToolNames.GetJob] = Mcp.OAuthScopes.JobsRead,
            [Mcp.ToolNames.ExtractSkills] = Mcp.OAuthScopes.JobsRead,
            [Mcp.ToolNames.ScoreCandidateFit] = Mcp.OAuthScopes.CandidatesRead,
            [Mcp.ToolNames.RejectCandidate] = Mcp.OAuthScopes.CandidatesReject,
            [Mcp.ToolNames.BulkScoreShortlist] = Mcp.OAuthScopes.CandidatesWrite,
        };
}

/// <summary>
/// The standard headers the 2026-07-28 revision requires on a Streamable HTTP <c>tools/call</c>.
/// <para>
/// The SDK has its own <c>ModelContextProtocol.Protocol.McpHttpHeaders</c> with the same two names —
/// found in <c>ModelContextProtocol.Core.xml</c> — but that type is <c>internal</c>: referencing it
/// fails the build with <c>CS0122</c> (verified 3 Sep 2026 against SDK 2.2.0). Hence this repo's own
/// copy, rather than a public constant this project could have reused.
/// </para>
/// </summary>
public static class McpRequestHeaders
{
    /// <summary>The JSON-RPC method being invoked, e.g. <c>tools/call</c>. Required on every POST.</summary>
    public const string Method = "Mcp-Method";

    /// <summary>
    /// The target's name: the tool name for <c>tools/call</c>, the prompt name for <c>prompts/get</c>, the
    /// task id for <c>tasks/get</c> and <c>tasks/cancel</c> — see AGENTS.md pitfall #21.
    /// </summary>
    public const string Name = "Mcp-Name";
}
