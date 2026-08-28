namespace Talent.Mcp.Toolkit.Caching;

using ModelContextProtocol.Protocol;

/// <summary>
/// Default cache policies per MCP primitive, with the reasoning for each attached.
/// <para>
/// These are defaults, not law: a host binds its own values from configuration. They exist so the
/// decision is made once, in a place where it can be argued with, rather than as a magic number at
/// each call site.
/// </para>
/// <para>
/// The scope choices matter more than the durations. <see cref="CacheScope.Public"/> permits a shared
/// gateway to serve one user's response to another — so it is only correct when the response genuinely
/// cannot vary per caller. Getting that wrong is a data-leak class of bug, not a performance one.
/// </para>
/// </summary>
public static class CachePolicies
{
    /// <summary>
    /// <c>tools/list</c>: public and long-lived.
    /// <para>
    /// Public is correct <em>because this server returns all six tools to every caller</em> and
    /// enforces OAuth scopes per invocation instead. If the tool list were ever filtered by the
    /// caller's scopes, this would have to become <see cref="CacheScope.Private"/> — a per-user list
    /// served from a shared cache would show one tenant another tenant's capabilities. The conformance
    /// test that asserts all six tools are always listed is what keeps this policy honest.
    /// </para>
    /// </summary>
    public static CachePolicy ToolsList { get; } = new(TimeSpan.FromMinutes(15), CacheScope.Public);

    /// <summary>
    /// <c>prompts/list</c>: public, same reasoning as <see cref="ToolsList"/>.
    /// </summary>
    public static CachePolicy PromptsList { get; } = new(TimeSpan.FromMinutes(15), CacheScope.Public);

    /// <summary>
    /// <c>resources/list</c>: private and short.
    /// <para>
    /// Private because a resource listing reflects domain data the caller is authorized to see, and
    /// short because job postings open and close during a working day — a stale listing is a recruiter
    /// looking at a vacancy that no longer exists.
    /// </para>
    /// </summary>
    public static CachePolicy ResourcesList { get; } = new(TimeSpan.FromMinutes(1), CacheScope.Private);

    /// <summary>
    /// <c>resources/templates/list</c>: public and long-lived. Templates are part of the server's
    /// shape, not its data, so they are identical for every caller and change only on deploy.
    /// </summary>
    public static CachePolicy ResourceTemplatesList { get; } = new(TimeSpan.FromHours(1), CacheScope.Public);

    /// <summary>
    /// <c>resources/read</c>: private and short.
    /// <para>
    /// Private is the load-bearing part, and the reason is specific to this server:
    /// <c>get_job</c> promotes region routing to a header via <c>[McpHeader("Region")]</c>, so its
    /// result depends on a request header. <c>cacheScope</c> has no <c>Vary</c> mechanism — there is no
    /// way to tell a shared cache "this is public, but keyed by Region" — so a shared cache could serve
    /// an EU response to an APAC caller. Private is the only safe answer while any read varies by
    /// header.
    /// </para>
    /// </summary>
    public static CachePolicy ResourceRead { get; } = new(TimeSpan.FromMinutes(5), CacheScope.Private);

    /// <summary>
    /// <c>server/discover</c>: public and long-lived. It advertises protocol versions, capabilities and
    /// identity — the server's own shape, which is the same for everyone and changes only on deploy.
    /// </summary>
    public static CachePolicy ServerDiscover { get; } = new(TimeSpan.FromHours(1), CacheScope.Public);

    /// <summary>Every default policy, for tests that assert all of them are coherent.</summary>
    public static IReadOnlyDictionary<string, CachePolicy> All { get; } =
        new Dictionary<string, CachePolicy>(StringComparer.Ordinal)
        {
            ["tools/list"] = ToolsList,
            ["prompts/list"] = PromptsList,
            ["resources/list"] = ResourcesList,
            ["resources/templates/list"] = ResourceTemplatesList,
            ["resources/read"] = ResourceRead,
            ["server/discover"] = ServerDiscover,
        };
}
