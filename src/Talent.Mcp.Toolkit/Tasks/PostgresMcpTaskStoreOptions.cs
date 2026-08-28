namespace Talent.Mcp.Toolkit.Tasks;

/// <summary>Tunables for <see cref="PostgresMcpTaskStore"/>.</summary>
public sealed class PostgresMcpTaskStoreOptions
{
    /// <summary>
    /// Poll interval advertised to clients, in milliseconds. Matches the SDK's in-memory default so
    /// swapping stores does not change client behaviour.
    /// </summary>
    public long DefaultPollIntervalMs { get; init; } = 1000;

    /// <summary>
    /// How long a task survives before the sweep deletes it, or <see langword="null"/> to keep tasks
    /// until something else removes them.
    /// <para>
    /// One hour by default. A task store is not an audit log: the point of persistence here is that a
    /// restart does not lose work in flight, not that results are kept forever. A durable record of what
    /// a bulk scoring run produced belongs in the domain tables.
    /// </para>
    /// </summary>
    public TimeSpan? DefaultTimeToLive { get; init; } = TimeSpan.FromHours(1);

    /// <summary>
    /// How often to delete expired tasks and re-deliver responses that may have been notified while no
    /// connection was listening.
    /// </summary>
    public TimeSpan SweepInterval { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// How far back the sweep looks for responses to re-deliver.
    /// <para>
    /// Bounded rather than unbounded: re-raising every response ever recorded would grow with the table
    /// and hand the SDK events for requests it stopped waiting on long ago. Five minutes comfortably
    /// covers a listener reconnect, which is the failure this exists for.
    /// </para>
    /// </summary>
    public TimeSpan ResponseRedeliveryWindow { get; init; } = TimeSpan.FromMinutes(5);

    /// <summary>How long to wait before re-establishing a dropped listening connection.</summary>
    public TimeSpan ListenerReconnectDelay { get; init; } = TimeSpan.FromSeconds(2);
}
