namespace Talent.Mcp.Conformance;

using ModelContextProtocol.Protocol;
using Talent.Mcp.Tools;
using Talent.Mcp.Tools.Constants;
using Xunit;

/// <summary>
/// <c>server/discover</c> over a real HTTP connection.
/// <para>
/// Per <c>docs/verification/sdk-2.0.0-to-2.2.0-review.md</c> (Finding 2), a client that never calls
/// <c>server/discover</c> at all must not be treated as broken — SDK 2.1.0 added an <c>initialize</c>
/// fallback specifically so an older client is not penalised for skipping it. So this suite asserts the
/// response's shape when a client does call it, not that a client fails without it.
/// </para>
/// </summary>
[Collection(ConformanceServerCollection.Name)]
public sealed class ServerDiscoverConformanceTests
{
    private readonly ConformanceServerFixture fixture;

    public ServerDiscoverConformanceTests(ConformanceServerFixture fixture)
    {
        this.fixture = fixture;
    }

    [Fact]
    public async Task Discover_reports_the_supported_revision_and_the_servers_identity()
    {
        await using var client = await this.fixture.CreateClientAsync();

        var discover = await client.SendRequestAsync<DiscoverRequestParams, DiscoverResult>(
            RequestMethods.ServerDiscover, new DiscoverRequestParams());

        Assert.Contains(Mcp.ProtocolVersions.Supported, discover.SupportedVersions);
        Assert.NotNull(discover.Capabilities.Tools);

        // Populated by the SDK client during connect from the same identity discover reports — so
        // asserting both here pins the one place a rename of TalentServerInfo would be caught on the
        // wire, not just in a unit test that reads the static value back to itself.
        Assert.Equal(Mcp.ServerName, client.ServerInfo?.Name);
        Assert.Equal(TalentServerInfo.Value.Title, client.ServerInfo?.Title);
        Assert.False(string.IsNullOrWhiteSpace(client.ServerInfo?.Version));
    }

    [Fact]
    public async Task Discover_carries_ttlMs_and_cacheScope_even_though_they_are_the_SDKs_defaults()
    {
        await using var client = await this.fixture.CreateClientAsync();

        var discover = await client.SendRequestAsync<DiscoverRequestParams, DiscoverResult>(
            RequestMethods.ServerDiscover, new DiscoverRequestParams());

        // The revision requires these fields to be present, which is what this test pins. Their values
        // are deliberately NOT CachePolicies.ServerDiscover (one hour, public): the SDK has no
        // AddServerDiscoverFilter equivalent to AddListToolsFilter, so nothing in this codebase sets
        // them — see the "Open, and deliberately left for the conformance work" section of
        // docs/verification/sdk-2.2.0-tool-surface-behaviour.md. Asserting the SDK's actual current
        // defaults, rather than the policy this server would prefer, is the honest thing to pin: it
        // fails the day either the SDK changes its default or this codebase adds the missing filter,
        // and either is worth noticing.
        Assert.True(discover.TimeToLive.HasValue);
        Assert.Equal(TimeSpan.Zero, discover.TimeToLive!.Value);
        Assert.Equal(CacheScope.Private, discover.CacheScope);
    }
}
