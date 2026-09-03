namespace Talent.Mcp.Server.Authentication;

using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using ModelContextProtocol.Protocol;
using Talent.Mcp.Server.Constants;

/// <summary>
/// Marks an endpoint as needing per-tool scope enforcement. Carries no data — the requirement is the
/// same everywhere it is used, and what scope applies depends on the request being evaluated, not on
/// how the policy was declared.
/// </summary>
public sealed class ToolScopeRequirement : IAuthorizationRequirement
{
    /// <summary>The single instance; the requirement has no configurable state.</summary>
    public static readonly ToolScopeRequirement Instance = new();

    private ToolScopeRequirement()
    {
    }
}

/// <summary>
/// Enforces <see cref="OAuth.RequiredScopeByToolName"/> against the caller's token.
/// <para>
/// <b>Why a hand-rolled header check and not the SDK's own mechanisms.</b> Two were considered and
/// ruled out, both verified against SDK 2.2.0 rather than assumed:
/// </para>
/// <list type="number">
/// <item><description>
/// <c>McpRequestFilters.CallToolFilters</c> only wraps a call to a tool the SDK did <em>not</em> find in
/// the registered <c>McpServerTool</c> collection — our six tools are all in it, so those filters never
/// run for them.
/// </description></item>
/// <item><description>
/// <c>HttpMcpServerBuilderExtensions.AddAuthorizationFilters()</c> plus <c>[Authorize(Policy = ...)]</c>
/// on each tool method is the SDK's purpose-built answer to exactly this problem, and it is a better
/// answer than this class — it also filters <c>tools/list</c> to what the caller's token allows, and it
/// runs ahead of task creation for <c>bulk_score_shortlist</c>. It was tried and reverted: the attribute
/// needs <c>Microsoft.AspNetCore.Authorization</c> on the tool methods, which live in
/// <c>Talent.Mcp.Tools</c> — and <c>Talent.Architecture.Tests.ForbiddenAssemblyReferences</c> forbids any
/// <c>Microsoft.AspNetCore.*</c> assembly there, deliberately, because the stdio host loads the same
/// assembly and cold start is the metric ADR-0004 protects.
/// </description></item>
/// </list>
/// <para>
/// What the 2026-07-28 revision does put on every <c>tools/call</c> request is the <c>Mcp-Name</c>
/// header (the pitfall recorded in AGENTS.md #24) — promoted from <c>params.name</c> specifically so a
/// resource server can make this decision without parsing the JSON-RPC body. This handler is that
/// decision, wired in as an ASP.NET Core authorization requirement so it composes with the framework's
/// own 401/403 handling instead of hand-rolling either — and it stays entirely inside
/// <c>Talent.Mcp.Server</c>, respecting the same boundary the SDK's own mechanism could not.
/// </para>
/// </summary>
public sealed class ToolScopeAuthorizationHandler : IAuthorizationHandler
{
    /// <inheritdoc />
    public Task HandleAsync(AuthorizationHandlerContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        foreach (var requirement in context.PendingRequirements.OfType<ToolScopeRequirement>().ToList())
        {
            if (context.Resource is not HttpContext httpContext)
            {
                // No request to read a tool name from — nothing to check, so nothing to allow either.
                // Leaving the requirement pending fails the policy, which is the safe direction.
                continue;
            }

            var method = httpContext.Request.Headers[McpRequestHeaders.Method].ToString();
            if (!string.Equals(method, RequestMethods.ToolsCall, StringComparison.Ordinal))
            {
                // Every other method (tools/list, server/discover, ...) needs only the base
                // authentication this policy is combined with, not a tool-specific scope.
                context.Succeed(requirement);
                continue;
            }

            var toolName = httpContext.Request.Headers[McpRequestHeaders.Name].ToString();
            if (!OAuth.RequiredScopeByToolName.TryGetValue(toolName, out var requiredScope)
                || HasScope(httpContext.User, requiredScope))
            {
                // An unrecognized tool name is not this handler's failure to report: the SDK answers
                // -32601 for it, and blocking on a scope requirement we have no record of would turn a
                // "no such tool" into a misleading 403.
                context.Succeed(requirement);
            }
        }

        return Task.CompletedTask;
    }

    private static bool HasScope(ClaimsPrincipal user, string scope) =>
        user
            .FindAll(OAuth.ScopeClaimType)
            .SelectMany(static claim => claim.Value.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            .Contains(scope, StringComparer.Ordinal);
}
