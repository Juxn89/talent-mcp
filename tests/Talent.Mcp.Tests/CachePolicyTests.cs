namespace Talent.Mcp.Tests;

using ModelContextProtocol.Protocol;
using Talent.Mcp.Toolkit.Caching;
using Xunit;

/// <summary>
/// Tests for the cache-hint policies the 2026-07-28 revision requires on every <c>*/list</c> response,
/// on <c>resources/read</c> and on <c>server/discover</c>.
/// </summary>
public sealed class CachePolicyTests
{
    private sealed class FakeCacheableResult : ICacheableResult
    {
        public TimeSpan? TimeToLive { get; set; }

        public CacheScope? CacheScope { get; set; }
    }

    [Fact]
    public void Applying_a_policy_stamps_both_fields()
    {
        // Both, always. Omitting either is a conformance failure, which is why this is one call
        // rather than two properties set by hand at each call site.
        var result = CachePolicies.ToolsList.ApplyTo(new FakeCacheableResult());

        Assert.Equal(TimeSpan.FromMinutes(15), result.TimeToLive);
        Assert.Equal(CacheScope.Public, result.CacheScope);
    }

    [Fact]
    public void Ttl_is_exposed_in_milliseconds_because_that_is_the_wire_unit()
    {
        Assert.Equal(900_000, CachePolicies.ToolsList.TimeToLiveMilliseconds);
        Assert.Equal(0, CachePolicy.NoCache.TimeToLiveMilliseconds);
    }

    [Fact]
    public void A_negative_ttl_is_rejected_rather_than_treated_as_no_cache()
    {
        // The spec says a client receiving a negative ttlMs should treat the response as immediately
        // stale, so emitting one is a server bug, not a way to say "do not cache". NoCache is.
        var policy = new CachePolicy(TimeSpan.FromSeconds(-1), CacheScope.Private);

        Assert.False(policy.IsValid());
        Assert.Throws<InvalidOperationException>(() => policy.ApplyTo(new FakeCacheableResult()));
    }

    [Fact]
    public void NoCache_is_valid_and_immediately_stale()
    {
        Assert.True(CachePolicy.NoCache.IsValid());
        Assert.Equal(TimeSpan.Zero, CachePolicy.NoCache.TimeToLive);
        Assert.Equal(CacheScope.Private, CachePolicy.NoCache.Scope);
    }

    [Fact]
    public void Applying_to_null_is_rejected() =>
        Assert.Throws<ArgumentNullException>(() => CachePolicies.ToolsList.ApplyTo<FakeCacheableResult>(null!));

    [Fact]
    public void Every_default_policy_is_coherent() =>
        Assert.All(CachePolicies.All, entry => Assert.True(
            entry.Value.IsValid(),
            $"Default policy for '{entry.Key}' is not valid."));

    [Fact]
    public void Anything_reflecting_domain_data_is_private()
    {
        // The scope choices are the load-bearing part: Public lets a shared gateway serve one user's
        // response to another. resources/read is Private specifically because get_job varies by the
        // Region header and cacheScope has no Vary mechanism.
        Assert.Equal(CacheScope.Private, CachePolicies.ResourcesList.Scope);
        Assert.Equal(CacheScope.Private, CachePolicies.ResourceRead.Scope);
    }

    [Fact]
    public void Anything_reflecting_only_the_servers_shape_is_public()
    {
        Assert.Equal(CacheScope.Public, CachePolicies.ToolsList.Scope);
        Assert.Equal(CacheScope.Public, CachePolicies.PromptsList.Scope);
        Assert.Equal(CacheScope.Public, CachePolicies.ResourceTemplatesList.Scope);
        Assert.Equal(CacheScope.Public, CachePolicies.ServerDiscover.Scope);
    }

    [Fact]
    public void Domain_data_is_cached_for_less_time_than_the_servers_own_shape()
    {
        // Postings open and close during a working day; the tool set changes on deploy.
        Assert.True(CachePolicies.ResourcesList.TimeToLive < CachePolicies.ToolsList.TimeToLive);
        Assert.True(CachePolicies.ResourceRead.TimeToLive < CachePolicies.ServerDiscover.TimeToLive);
    }
}
