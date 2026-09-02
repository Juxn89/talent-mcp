namespace Talent.Mcp.Conformance;

using ModelContextProtocol.Protocol;
using Talent.Mcp.Toolkit.Caching;
using Talent.Mcp.Tools.Constants;
using Xunit;

/// <summary>
/// <c>tools/list</c> conformance: all six tools, by name, in a stable, deterministic order, carrying the
/// cache fields the revision requires.
/// <para>
/// This is the test ADR-0002 exists to be caught by: a trimmed <c>WithToolsFromAssembly()</c> build
/// starts cleanly and answers <c>tools/list</c> with an empty or partial set — no crash, no error log.
/// A test that only asserts the server starts is worthless here; this one asserts the actual tool names.
/// </para>
/// </summary>
[Collection(ConformanceServerCollection.Name)]
public sealed class ToolsListConformanceTests
{
    private readonly ConformanceServerFixture fixture;

    public ToolsListConformanceTests(ConformanceServerFixture fixture)
    {
        this.fixture = fixture;
    }

    [Fact]
    public async Task Tools_list_returns_exactly_the_six_registered_tools_by_name()
    {
        await using var client = await this.fixture.CreateClientAsync();

        var result = await client.ListToolsAsync(new ListToolsRequestParams());

        var names = result.Tools.Select(static t => t.Name).ToArray();
        Assert.Equal(Mcp.ToolNames.All.Length, names.Length);
        Assert.Equal(Mcp.ToolNames.All.OrderBy(static n => n, StringComparer.Ordinal),
            names.OrderBy(static n => n, StringComparer.Ordinal));
    }

    [Fact]
    public async Task Tools_list_order_is_canonical_and_stable_across_repeated_calls()
    {
        await using var client = await this.fixture.CreateClientAsync();

        var first = await client.ListToolsAsync(new ListToolsRequestParams());
        var second = await client.ListToolsAsync(new ListToolsRequestParams());

        // Canonical, not merely "the same as last time": TalentTools.AddTalentTools imposes
        // Mcp.ToolNames.All order specifically because the tools live in a concurrent collection whose
        // own enumeration order is not a documented guarantee — see ADR-0002.
        Assert.Equal(Mcp.ToolNames.All, first.Tools.Select(static t => t.Name));
        Assert.Equal(Mcp.ToolNames.All, second.Tools.Select(static t => t.Name));
    }

    [Fact]
    public async Task Tools_list_carries_the_configured_ttlMs_and_cacheScope()
    {
        await using var client = await this.fixture.CreateClientAsync();

        var result = await client.ListToolsAsync(new ListToolsRequestParams());

        Assert.Equal(CachePolicies.ToolsList.TimeToLive, result.TimeToLive);
        Assert.Equal(CachePolicies.ToolsList.Scope, result.CacheScope);
    }
}
