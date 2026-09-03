namespace Talent.Mcp.Server.Authentication;

using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.AspNetCore.Authentication;
using ModelContextProtocol.Authentication;
using Talent.Mcp.Server.Constants;
using Talent.Mcp.Tools.Constants;

/// <summary>
/// Wires this host as an OAuth 2.1 resource server against the realm in
/// <c>deploy/keycloak/realm.json</c>, plus the <see cref="RequireToolScopePolicy"/> authorization policy
/// <see cref="ToolScopeAuthorizationHandler"/> enforces per tool call.
/// </summary>
public static class TalentAuthenticationServiceCollectionExtensions
{
    /// <summary>
    /// Configuration key holding the authorization server's issuer.
    /// <para>
    /// Not a constant in <see cref="OAuth"/>: the issuer differs between <c>docker compose</c>,
    /// Testcontainers and production (dev default: <c>http://localhost:8080/realms/talent</c>), so it is
    /// configuration the same way the Postgres connection string is. There is deliberately no in-code
    /// default — a server that silently trusted a hard-coded issuer would keep validating tokens after
    /// being pointed at a different Keycloak, which is a worse failure than refusing to start.
    /// </para>
    /// </summary>
    public const string AuthorityPath = "Talent:Auth:Authority";

    /// <summary>Policy name applied to the <c>/mcp</c> endpoint.</summary>
    public const string RequireToolScopePolicy = "RequireToolScope";

    /// <summary>
    /// Registers JWT bearer authentication against the configured issuer, the MCP-aware wrapper scheme
    /// that publishes RFC 9728 protected resource metadata (so a client discovers <em>this</em> issuer
    /// from a 401 rather than needing to be told it out of band), and the
    /// <see cref="RequireToolScopePolicy"/> policy that combines "the caller is authenticated" with
    /// <see cref="ToolScopeAuthorizationHandler"/>'s per-tool check.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="configuration">Configuration to read the issuer from.</param>
    /// <param name="requireHttpsMetadata">
    /// Whether the OpenID discovery document and JWKS must be fetched over HTTPS. <see langword="false"/>
    /// in local development, where Keycloak serves plain HTTP; <see langword="true"/> everywhere else —
    /// see the "HTTPS only in production" rule in AGENTS.md.
    /// </param>
    /// <returns>The same collection, for chaining.</returns>
    /// <exception cref="ArgumentNullException">An argument was <see langword="null"/>.</exception>
    /// <exception cref="InvalidOperationException">No issuer was configured at <see cref="AuthorityPath"/>.</exception>
    public static IServiceCollection AddTalentAuthentication(
        this IServiceCollection services,
        IConfiguration configuration,
        bool requireHttpsMetadata)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        var authority = ReadAuthority(configuration);

        services
            .AddAuthentication(McpAuthenticationDefaults.AuthenticationScheme)
            .AddJwtBearer(options =>
            {
                options.Authority = authority;
                options.Audience = OAuth.ClientId;
                options.RequireHttpsMetadata = requireHttpsMetadata;

                // Keep claim types exactly as Keycloak sends them ("scope", "sub", "aud", ...) rather
                // than remapped to the legacy XML schema URIs the JWT handler defaults to. The scope
                // check in ToolScopeAuthorizationHandler reads the literal "scope" claim.
                options.MapInboundClaims = false;

                // Otherwise every rejected token is an unexplained 401 with nothing in the server's
                // own logs to say why (bad audience vs. expired vs. wrong issuer vs. JWKS fetch
                // failure all look identical from the client). This is the difference between "a
                // token was rejected" and "which validation step rejected it".
                options.Events = new JwtBearerEvents
                {
                    OnAuthenticationFailed = context =>
                    {
                        context.HttpContext.RequestServices
                            .GetRequiredService<ILoggerFactory>()
                            .CreateLogger("Talent.Mcp.Server.Authentication")
                            .LogWarning(context.Exception, "Bearer token rejected.");
                        return Task.CompletedTask;
                    },
                };
            })
            .AddMcp(options =>
            {
                // The wrapper scheme adds the RFC 9728 protected-resource-metadata endpoint and the
                // resource_metadata parameter on a 401's WWW-Authenticate header; it forwards the
                // actual bearer-token cryptography to the JwtBearer scheme just registered rather than
                // reimplementing it.
                options.ForwardAuthenticate = JwtBearerDefaults.AuthenticationScheme;

                options.Events = new McpAuthenticationEvents
                {
                    OnResourceMetadataRequest = context =>
                    {
                        // Computed per-request, not once at startup: both test fixtures bind to
                        // "http://127.0.0.1:0" (an OS-assigned port), so there is no fixed external
                        // URL to bake in ahead of time, and production behind a reverse proxy would
                        // face the same problem the other way around.
                        var request = context.HttpContext.Request;
                        var metadata = new ProtectedResourceMetadata
                        {
                            Resource = $"{request.Scheme}://{request.Host}/mcp",
                            AuthorizationServers = { authority },
                        };
                        foreach (var scope in Mcp.OAuthScopes.All)
                        {
                            metadata.ScopesSupported.Add(scope);
                        }

                        context.ResourceMetadata = metadata;
                        return Task.CompletedTask;
                    },
                };
            });

        services.AddAuthorizationBuilder()
            .AddPolicy(RequireToolScopePolicy, policy => policy
                .RequireAuthenticatedUser()
                .AddRequirements(ToolScopeRequirement.Instance));

        services.AddSingleton<IAuthorizationHandler, ToolScopeAuthorizationHandler>();

        return services;
    }

    private static string ReadAuthority(IConfiguration configuration)
    {
        var authority = configuration[AuthorityPath];

        if (string.IsNullOrWhiteSpace(authority))
        {
            throw new InvalidOperationException(
                $"No OAuth issuer was configured at '{AuthorityPath}'. Set Talent__Auth__Authority in "
                + "the environment, or Talent:Auth:Authority in configuration — e.g. "
                + "http://localhost:8080/realms/talent for the compose stack. See "
                + "deploy/keycloak/realm.json for the realm this must point at.");
        }

        return authority;
    }
}
