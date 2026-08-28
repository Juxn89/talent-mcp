namespace Talent.Mcp.Toolkit.Caching;

using ModelContextProtocol.Protocol;

/// <summary>
/// A caching hint for one MCP primitive: how long a client may consider a response fresh, and who is
/// allowed to cache it.
/// </summary>
/// <param name="TimeToLive">
/// Freshness window, serialized by the SDK as <c>ttlMs</c>. <see cref="TimeSpan.Zero"/> means treat
/// the response as immediately stale.
/// </param>
/// <param name="Scope">
/// Who may cache it. <see cref="CacheScope.Public"/> permits shared caches and gateways;
/// <see cref="CacheScope.Private"/> restricts caching to the requesting user's client.
/// </param>
public sealed record CachePolicy(TimeSpan TimeToLive, CacheScope Scope)
{
    /// <summary>A policy that disables caching by declaring the response immediately stale.</summary>
    public static CachePolicy NoCache { get; } = new(TimeSpan.Zero, CacheScope.Private);

    /// <summary>The time-to-live in whole milliseconds, which is the unit the wire format uses.</summary>
    public long TimeToLiveMilliseconds => (long)this.TimeToLive.TotalMilliseconds;

    /// <summary>
    /// Whether the policy is coherent. A negative time-to-live is meaningless on the wire — the spec
    /// says a client receiving one should treat the response as immediately stale, so producing one is
    /// a server bug rather than a way to express "do not cache".
    /// </summary>
    /// <returns><see langword="true"/> when the policy can be applied.</returns>
    public bool IsValid() => this.TimeToLive >= TimeSpan.Zero;

    /// <summary>
    /// Stamps this policy onto a cacheable result.
    /// <para>
    /// The 2026-07-28 revision requires <c>ttlMs</c> and <c>cacheScope</c> on every
    /// <c>*/list</c> response, on <c>resources/read</c> and on <c>server/discover</c>. Omitting them is
    /// a conformance failure, and a conformance test asserts their presence on every list response —
    /// which is why this is a single call rather than two properties set by hand at each call site.
    /// </para>
    /// </summary>
    /// <typeparam name="TResult">The result type.</typeparam>
    /// <param name="result">The result to stamp.</param>
    /// <returns>The same result, for chaining.</returns>
    /// <exception cref="ArgumentNullException">The result was <see langword="null"/>.</exception>
    /// <exception cref="InvalidOperationException">The policy was not valid.</exception>
    public TResult ApplyTo<TResult>(TResult result)
        where TResult : ICacheableResult
    {
        ArgumentNullException.ThrowIfNull(result);

        if (!this.IsValid())
        {
            throw new InvalidOperationException(
                $"A cache policy cannot have a negative time-to-live; got {this.TimeToLive}.");
        }

        result.TimeToLive = this.TimeToLive;
        result.CacheScope = this.Scope;

        return result;
    }
}
