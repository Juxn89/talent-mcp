namespace Talent.Infrastructure.Tests;

using System.Text.Json;
using ModelContextProtocol.Extensions.Tasks;
using Npgsql;
using Talent.Mcp.Toolkit.Tasks;
using Testcontainers.PostgreSql;
using Xunit;

/// <summary>
/// The check the plan names as the one that justifies the Postgres task store existing at all:
/// <em>"<c>bulk_score_shortlist</c>, reiniciar el contenedor, y la task sigue consultable"</em>.
/// <para>
/// <see cref="PostgresMcpTaskStoreTests.A_task_survives_the_process_that_created_it"/> already proves
/// the data outlives the process that wrote it, which is the half that <c>InMemoryMcpTaskStore</c>
/// cannot do. The half nobody had exercised is the database going away and coming back: durability is
/// Postgres's job, but <strong>recovering the connections is ours</strong>, and the store holds a
/// long-lived <c>LISTEN</c> connection plus a pool that a restart leaves full of dead sockets.
/// </para>
/// <para>
/// Its own container, because <see cref="PostgresFixture"/> is shared by a collection and restarting
/// it underneath the other classes would be indefensible.
/// </para>
/// </summary>
public sealed class TaskStoreSurvivesDatabaseRestartTests : IAsyncLifetime
{
    /// <summary>
    /// A fixed host port, which is unusual for Testcontainers and deliberate here.
    /// <para>
    /// With the default random mapping, stopping and starting the container can hand back a different
    /// host port. The store would then be unable to reconnect for a reason that has nothing to do with
    /// its recovery logic, and the test would fail as an artefact of the harness rather than as a
    /// finding. Pinning the port keeps the endpoint stable across the restart, which is what a real
    /// database restart looks like to a client.
    /// </para>
    /// </summary>
    private const int HostPort = 55433;

    private readonly PostgreSqlContainer container = new PostgreSqlBuilder("postgres:18.6-alpine")
        .WithDatabase("talent_restart")
        .WithUsername("talent")
        .WithPassword("talent")
        .WithPortBinding(HostPort, 5432)
        .Build();

    public async Task InitializeAsync()
    {
        await this.container.StartAsync();
        await PostgresTaskStoreSchema.EnsureCreatedAsync(this.container.GetConnectionString());
    }

    public async Task DisposeAsync() => await this.container.DisposeAsync();

    [Fact]
    public async Task A_task_survives_a_database_restart_and_the_store_keeps_working()
    {
        // One test and one restart, not two. An earlier revision split reading and writing into
        // separate [Fact]s, which gave xUnit two instances of this class -- and therefore two
        // containers competing for the pinned port, the second connecting to the first as it shut
        // down: "57P01: terminating connection due to unexpected postmaster exit". Both assertions
        // are about the same event, so one restart carries both and the collision cannot happen.
        await using var store = new PostgresMcpTaskStore(this.container.GetConnectionString());

        // Started, not merely constructed: StartAsync opens the cross-node LISTEN connection, and
        // that is the connection a restart kills. Skipping it would test the pool and miss the
        // listener.
        await store.StartAsync();

        var before = await store.CreateTaskAsync();
        await store.SetCompletedAsync(
            before.TaskId,
            JsonDocument.Parse("""{"scoredCount":42}""").RootElement);

        await this.container.StopAsync();
        await this.container.StartAsync();

        // Retried rather than asserted once. The first attempt after a restart is expected to fail:
        // Npgsql hands out a pooled connection whose socket died with the server, notices, and
        // discards it. Recovery is the property under test, so what matters is that it happens
        // unaided and within a bound -- not that the very first call succeeds. Exhausting the bound
        // is a real failure, not a flake.
        var loaded = await WithRecoveryAsync(
            () => store.GetTaskAsync(before.TaskId), TimeSpan.FromSeconds(60));

        Assert.NotNull(loaded);
        Assert.Equal(McpTaskStatus.Completed, loaded!.Status);
        Assert.Equal(42, loaded.Result!.Value.GetProperty("scoredCount").GetInt32());

        // Reading an old row could succeed while the store is left effectively read-only, and that
        // would pass for recovery. Writing afterwards is what makes it complete.
        var after = await WithRecoveryAsync(
            async () => await store.CreateTaskAsync(), TimeSpan.FromSeconds(60));

        Assert.NotNull(after);
        var reloaded = await store.GetTaskAsync(after!.TaskId);
        Assert.NotNull(reloaded);
        Assert.Equal(McpTaskStatus.Working, reloaded!.Status);
    }

    /// <summary>
    /// Retries only the transport failures a restart causes, and lets everything else through.
    /// <para>
    /// Catching every exception would turn a genuine defect into a timeout with no diagnosis. A
    /// <see cref="NpgsqlException"/> is what a dead pooled socket or a server still starting up
    /// produces; anything else means the store is broken in a way a restart did not cause, and it
    /// should surface immediately with its own message.
    /// </para>
    /// </summary>
    private static async Task<T?> WithRecoveryAsync<T>(Func<Task<T?>> operation, TimeSpan within)
        where T : class
    {
        var deadline = DateTimeOffset.UtcNow + within;
        Exception? last = null;

        while (DateTimeOffset.UtcNow < deadline)
        {
            try
            {
                return await operation();
            }
            catch (NpgsqlException ex)
            {
                last = ex;
                await Task.Delay(TimeSpan.FromSeconds(1));
            }
        }

        throw new Xunit.Sdk.XunitException(
            $"The task store never recovered from the database restart within {within.TotalSeconds:F0}s. "
            + $"Last transport error: {last?.Message ?? "none — the operation kept returning null"}");
    }
}
