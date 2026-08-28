namespace Talent.Infrastructure.Tests;

using System.Text.Json;
using ModelContextProtocol.Extensions.Tasks;
using ModelContextProtocol.Protocol;
using Npgsql;
using Talent.Mcp.Toolkit.Tasks;
using Xunit;

/// <summary>
/// The Postgres-backed task store against real Postgres.
/// <para>
/// Lives in this project because it needs a container, even though the type ships in
/// <c>Talent.Mcp.Toolkit</c>: this project's job is "things whose answer only exists in the real
/// provider", and <c>LISTEN</c>/<c>NOTIFY</c> is the clearest example of that in the repo.
/// </para>
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class PostgresMcpTaskStoreTests : IAsyncLifetime
{
    private readonly PostgresFixture postgres;

    public PostgresMcpTaskStoreTests(PostgresFixture postgres) => this.postgres = postgres;

    public async Task InitializeAsync()
    {
        await PostgresTaskStoreSchema.EnsureCreatedAsync(this.postgres.ConnectionString);

        // Raw Npgsql, not the DbContext: the store under test uses no EF at all, so reaching for it
        // here would test the wrong thing and imply a dependency the toolkit deliberately avoids.
        await using var connection = new NpgsqlConnection(this.postgres.ConnectionString);
        await connection.OpenAsync();

        await using var truncate = new NpgsqlCommand(
            $"TRUNCATE TABLE {PostgresTaskStoreSchema.TasksTable} CASCADE", connection);

        await truncate.ExecuteNonQueryAsync();
    }

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task Creating_the_schema_is_idempotent() =>
        await PostgresTaskStoreSchema.EnsureCreatedAsync(this.postgres.ConnectionString);

    [Fact]
    public async Task A_created_task_is_readable_and_working()
    {
        await using var store = this.Store();

        var created = await store.CreateTaskAsync();
        var loaded = await store.GetTaskAsync(created.TaskId);

        Assert.NotNull(loaded);
        Assert.Equal(McpTaskStatus.Working, loaded.Status);
        Assert.Equal(created.TaskId, loaded.TaskId);
        Assert.Equal(1000, loaded.PollIntervalMs);
    }

    [Fact]
    public async Task An_unknown_task_reads_as_absent()
    {
        await using var store = this.Store();

        Assert.Null(await store.GetTaskAsync("does-not-exist"));
    }

    [Fact]
    public async Task A_task_survives_the_process_that_created_it()
    {
        // The reason this class exists. With InMemoryMcpTaskStore this test cannot be written, because
        // the second store would be a different dictionary.
        string taskId;

        await using (var first = this.Store())
        {
            var created = await first.CreateTaskAsync();
            taskId = created.TaskId;
            await first.SetCompletedAsync(taskId, Json("""{"scored":42}"""));
        }

        await using var second = this.Store();
        var loaded = await second.GetTaskAsync(taskId);

        Assert.NotNull(loaded);
        Assert.Equal(McpTaskStatus.Completed, loaded.Status);
        Assert.Equal(42, loaded.Result!.Value.GetProperty("scored").GetInt32());
    }

    [Fact]
    public async Task Completing_records_the_result_and_a_second_attempt_is_ignored()
    {
        await using var store = this.Store();
        var created = await store.CreateTaskAsync();

        await store.SetCompletedAsync(created.TaskId, Json("""{"round":1}"""));
        await store.SetCompletedAsync(created.TaskId, Json("""{"round":2}"""));

        var loaded = await store.GetTaskAsync(created.TaskId);

        // Terminal is terminal. The guard is in the WHERE clause rather than read-then-write, because
        // two nodes racing to finish the same task is exactly what a stateless server invites.
        Assert.Equal(1, loaded!.Result!.Value.GetProperty("round").GetInt32());
    }

    [Fact]
    public async Task Failing_records_the_error()
    {
        await using var store = this.Store();
        var created = await store.CreateTaskAsync();

        await store.SetFailedAsync(created.TaskId, Json("""{"code":-32603}"""));

        var loaded = await store.GetTaskAsync(created.TaskId);

        Assert.Equal(McpTaskStatus.Failed, loaded!.Status);
        Assert.Equal(-32603, loaded.Error!.Value.GetProperty("code").GetInt32());
    }

    [Fact]
    public async Task Cancelling_succeeds_once_and_reports_false_afterwards()
    {
        await using var store = this.Store();
        var created = await store.CreateTaskAsync();

        Assert.True(await store.SetCancelledAsync(created.TaskId));
        Assert.False(await store.SetCancelledAsync(created.TaskId));
    }

    [Fact]
    public async Task Cancelling_a_completed_task_reports_false()
    {
        await using var store = this.Store();
        var created = await store.CreateTaskAsync();

        await store.SetCompletedAsync(created.TaskId, Json("{}"));

        Assert.False(await store.SetCancelledAsync(created.TaskId));
    }

    [Fact]
    public async Task Setting_input_requests_moves_the_task_to_input_required()
    {
        await using var store = this.Store();
        var created = await store.CreateTaskAsync();

        await store.SetInputRequestsAsync(
            created.TaskId,
            new Dictionary<string, InputRequest> { ["r1"] = Elicitation("Confirm the rejection?") });

        var loaded = await store.GetTaskAsync(created.TaskId);

        Assert.Equal(McpTaskStatus.InputRequired, loaded!.Status);
        Assert.Single(loaded.InputRequests!);
        Assert.Equal("elicitation/create", loaded.InputRequests!["r1"].Method);
    }

    [Fact]
    public async Task Resolving_clears_the_request_and_returns_the_task_to_working()
    {
        await using var store = this.Store();
        var created = await store.CreateTaskAsync();

        await store.SetInputRequestsAsync(
            created.TaskId,
            new Dictionary<string, InputRequest> { ["r1"] = Elicitation("Confirm?") });

        await store.ResolveInputRequestsAsync(
            created.TaskId,
            new Dictionary<string, InputResponse> { ["r1"] = Response("""{"action":"accept"}""") });

        var loaded = await store.GetTaskAsync(created.TaskId);

        Assert.Equal(McpTaskStatus.Working, loaded!.Status);
        Assert.Null(loaded.InputRequests);
    }

    [Fact]
    public async Task The_task_stays_input_required_while_any_request_is_outstanding()
    {
        await using var store = this.Store();
        var created = await store.CreateTaskAsync();

        await store.SetInputRequestsAsync(
            created.TaskId,
            new Dictionary<string, InputRequest>
            {
                ["r1"] = Elicitation("First?"),
                ["r2"] = Elicitation("Second?"),
            });

        await store.ResolveInputRequestsAsync(
            created.TaskId,
            new Dictionary<string, InputResponse> { ["r1"] = Response("""{"action":"accept"}""") });

        var loaded = await store.GetTaskAsync(created.TaskId);

        Assert.Equal(McpTaskStatus.InputRequired, loaded!.Status);
        Assert.Single(loaded.InputRequests!);
        Assert.True(loaded.InputRequests!.ContainsKey("r2"));
    }

    [Fact]
    public async Task Resolving_raises_the_event_in_the_resolving_process()
    {
        await using var store = this.Store();
        var created = await store.CreateTaskAsync();
        var received = new TaskCompletionSource<InputResponseReceivedEventArgs>(
            TaskCreationOptions.RunContinuationsAsynchronously);

        store.InputResponseReceived += args => received.TrySetResult(args);

        await store.SetInputRequestsAsync(
            created.TaskId,
            new Dictionary<string, InputRequest> { ["r1"] = Elicitation("Confirm?") });

        await store.ResolveInputRequestsAsync(
            created.TaskId,
            new Dictionary<string, InputResponse> { ["r1"] = Response("""{"action":"accept"}""") });

        var args = await received.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(created.TaskId, args.TaskId);
        Assert.Equal("r1", args.RequestId);
        Assert.Equal("accept", args.Response.RawValue.GetProperty("action").GetString());
    }

    [Fact]
    public async Task A_response_resolved_on_another_instance_reaches_the_waiting_one()
    {
        // THE test this whole design exists for.
        //
        // The SDK subscribes InputResponseReceived in the process running the task and blocks on a
        // TaskCompletionSource. ResolveInputRequestsAsync is called by whichever process handles the
        // client's follow-up HTTP request, and this server is stateless by design — so with more than
        // one replica that is a different process. Persisting the response and raising the event
        // locally would leave the waiting node blocked until cancellation: the task would hang having
        // already received its answer.
        //
        // Two store instances stand in for two replicas. LISTEN/NOTIFY is what closes the gap.
        await using var waiting = this.Store();
        await using var resolving = this.Store();

        await waiting.StartAsync();

        var created = await waiting.CreateTaskAsync();
        var received = new TaskCompletionSource<InputResponseReceivedEventArgs>(
            TaskCreationOptions.RunContinuationsAsynchronously);

        waiting.InputResponseReceived += args => received.TrySetResult(args);

        await waiting.SetInputRequestsAsync(
            created.TaskId,
            new Dictionary<string, InputRequest> { ["r1"] = Elicitation("Confirm the rejection?") });

        await resolving.ResolveInputRequestsAsync(
            created.TaskId,
            new Dictionary<string, InputResponse> { ["r1"] = Response("""{"action":"accept"}""") });

        var args = await received.Task.WaitAsync(TimeSpan.FromSeconds(15));

        Assert.Equal(created.TaskId, args.TaskId);
        Assert.Equal("r1", args.RequestId);
        Assert.Equal("accept", args.Response.RawValue.GetProperty("action").GetString());
    }

    [Fact]
    public async Task A_missed_notification_is_recovered_by_the_sweep()
    {
        // A NOTIFY is lost outright if nothing is listening at that instant — during a listener
        // reconnect, or before a restarted node has attached. Here the waiting store never starts its
        // listener, so the notification cannot possibly arrive, and only the sweep can deliver.
        // This is what turns a missed notification into latency instead of a hang.
        await using var waiting = this.Store();
        await using var resolving = this.Store();

        var created = await waiting.CreateTaskAsync();
        var received = new TaskCompletionSource<InputResponseReceivedEventArgs>(
            TaskCreationOptions.RunContinuationsAsynchronously);

        waiting.InputResponseReceived += args => received.TrySetResult(args);

        await waiting.SetInputRequestsAsync(
            created.TaskId,
            new Dictionary<string, InputRequest> { ["r1"] = Elicitation("Confirm?") });

        await resolving.ResolveInputRequestsAsync(
            created.TaskId,
            new Dictionary<string, InputResponse> { ["r1"] = Response("""{"action":"accept"}""") });

        Assert.False(received.Task.IsCompleted);

        await waiting.SweepAsync();

        var args = await received.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal("r1", args.RequestId);
    }

    [Fact]
    public async Task Responses_for_a_terminal_task_are_dropped()
    {
        await using var store = this.Store();
        var created = await store.CreateTaskAsync();
        var raised = 0;

        store.InputResponseReceived += _ => Interlocked.Increment(ref raised);

        await store.SetInputRequestsAsync(
            created.TaskId,
            new Dictionary<string, InputRequest> { ["r1"] = Elicitation("Confirm?") });

        await store.SetCancelledAsync(created.TaskId);

        await store.ResolveInputRequestsAsync(
            created.TaskId,
            new Dictionary<string, InputResponse> { ["r1"] = Response("""{"action":"accept"}""") });

        // Nothing is waiting on a cancelled task, so recording the response would leave a row nobody
        // reads and an event nobody handles. Matches the in-memory store's behaviour.
        Assert.Equal(0, raised);
        Assert.Equal(McpTaskStatus.Cancelled, (await store.GetTaskAsync(created.TaskId))!.Status);
    }

    [Fact]
    public async Task Input_requests_are_not_accepted_for_a_terminal_task()
    {
        await using var store = this.Store();
        var created = await store.CreateTaskAsync();

        await store.SetCompletedAsync(created.TaskId, Json("{}"));

        await store.SetInputRequestsAsync(
            created.TaskId,
            new Dictionary<string, InputRequest> { ["r1"] = Elicitation("Too late?") });

        var loaded = await store.GetTaskAsync(created.TaskId);

        Assert.Equal(McpTaskStatus.Completed, loaded!.Status);
        Assert.Null(loaded.InputRequests);
    }

    [Fact]
    public async Task An_expired_task_reads_as_absent_and_is_swept_away()
    {
        var clock = new MovableClock(DateTimeOffset.Parse("2026-08-28T10:00:00Z", null));
        await using var store = this.Store(
            new PostgresMcpTaskStoreOptions { DefaultTimeToLive = TimeSpan.FromMinutes(10) }, clock);

        var created = await store.CreateTaskAsync();

        Assert.NotNull(await store.GetTaskAsync(created.TaskId));

        clock.Advance(TimeSpan.FromMinutes(11));

        // Reads as absent before the sweep runs: reporting an expired task would let a client poll a
        // result the store has already promised to forget.
        Assert.Null(await store.GetTaskAsync(created.TaskId));

        await store.SweepAsync();

        await using var connection = new NpgsqlConnection(this.postgres.ConnectionString);
        await connection.OpenAsync();

        await using var count = new NpgsqlCommand(
            $"SELECT COUNT(*) FROM {PostgresTaskStoreSchema.TasksTable}", connection);

        Assert.Equal(0L, (long)(await count.ExecuteScalarAsync())!);
    }

    [Fact]
    public async Task Resolving_the_same_request_twice_is_safe()
    {
        // At-least-once is the guarantee this store aims for, and it is safe because the SDK's handler
        // resolves a TaskCompletionSource with TrySetResult and then unsubscribes — a second event is a
        // no-op. Asserting it here means a future change to redelivery cannot quietly break that.
        await using var store = this.Store();
        var created = await store.CreateTaskAsync();

        await store.SetInputRequestsAsync(
            created.TaskId,
            new Dictionary<string, InputRequest> { ["r1"] = Elicitation("Confirm?") });

        var responses = new Dictionary<string, InputResponse> { ["r1"] = Response("""{"action":"accept"}""") };

        await store.ResolveInputRequestsAsync(created.TaskId, responses);
        await store.ResolveInputRequestsAsync(created.TaskId, responses);

        Assert.Equal(McpTaskStatus.Working, (await store.GetTaskAsync(created.TaskId))!.Status);
    }

    [Fact]
    public async Task Empty_batches_are_no_ops()
    {
        await using var store = this.Store();
        var created = await store.CreateTaskAsync();

        await store.SetInputRequestsAsync(created.TaskId, new Dictionary<string, InputRequest>());
        await store.ResolveInputRequestsAsync(created.TaskId, new Dictionary<string, InputResponse>());

        Assert.Equal(McpTaskStatus.Working, (await store.GetTaskAsync(created.TaskId))!.Status);
    }

    [Fact]
    public void A_missing_connection_string_is_rejected()
    {
        Assert.Throws<ArgumentException>(() => new PostgresMcpTaskStore("  "));
        Assert.Throws<ArgumentNullException>(() => new PostgresMcpTaskStore(null!));
    }

    private PostgresMcpTaskStore Store(
        PostgresMcpTaskStoreOptions? options = null,
        TimeProvider? timeProvider = null) =>
        new(this.postgres.ConnectionString, options, timeProvider);

    private static JsonElement Json(string json) => JsonDocument.Parse(json).RootElement;

    private static InputRequest Elicitation(string message) =>
        new()
        {
            Method = "elicitation/create",
            Params = Json($$"""{"message":{{JsonSerializer.Serialize(message)}}}"""),
        };

    private static InputResponse Response(string json) => new() { RawValue = Json(json) };

    private sealed class MovableClock(DateTimeOffset now) : TimeProvider
    {
        private DateTimeOffset now = now;

        public override DateTimeOffset GetUtcNow() => this.now;

        public void Advance(TimeSpan by) => this.now = this.now.Add(by);
    }
}
