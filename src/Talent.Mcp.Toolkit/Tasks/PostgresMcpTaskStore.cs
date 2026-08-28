namespace Talent.Mcp.Toolkit.Tasks;

using System.Text.Json;
using ModelContextProtocol.Extensions.Tasks;
using ModelContextProtocol.Protocol;
using Npgsql;
using NpgsqlTypes;

/// <summary>
/// A Postgres-backed <see cref="IMcpTaskStore"/>.
/// <para>
/// The SDK ships only <c>InMemoryMcpTaskStore</c>, so a restart loses every in-flight task. For
/// <c>bulk_score_shortlist</c> that is the difference between a long-running operation and a
/// long-running operation you can trust.
/// </para>
///
/// <para><strong>The hard part is not persistence, it is the event.</strong></para>
/// <para>
/// <see cref="IMcpTaskStore"/> exposes <see cref="InputResponseReceived"/>, and the SDK subscribes to it
/// <em>in the process running the task</em>, then blocks on a <c>TaskCompletionSource</c> until an event
/// arrives with a matching task and request id. But
/// <see cref="ResolveInputRequestsAsync"/> is called by whichever process handles the client's follow-up
/// HTTP request — and this server is stateless by design (ADR-0001), so with more than one replica that
/// is usually a <em>different</em> process. Persisting the response and raising the event locally would
/// leave the waiting node blocked until its cancellation token fired: the task would hang, having
/// received the answer it was waiting for.
/// </para>
/// <para>
/// So the store carries the signal across nodes with Postgres <c>LISTEN</c>/<c>NOTIFY</c>. The resolving
/// node writes the response row and issues a <c>NOTIFY</c>; every node holds a dedicated listening
/// connection and re-raises <see cref="InputResponseReceived"/> locally when it hears one.
/// </para>
/// <para>
/// <strong>The notification is a hint, not the record.</strong> A <c>NOTIFY</c> payload is capped at
/// 8000 bytes and is lost outright if no connection is listening at that instant — during a listener
/// reconnect, for example. So the response is persisted first and the payload carries only ids; a woken
/// node reads the row. A periodic sweep re-checks for responses that arrived while nobody was listening,
/// which turns a missed notification into added latency instead of a hang.
/// </para>
/// </summary>
public sealed class PostgresMcpTaskStore : IMcpTaskStore, IAsyncDisposable
{
    private readonly string connectionString;
    private readonly PostgresMcpTaskStoreOptions options;
    private readonly TimeProvider timeProvider;
    private readonly CancellationTokenSource shutdown = new();
    private readonly SemaphoreSlim listenerStartGate = new(1, 1);

    private Task? listenerLoop;
    private Task? sweepLoop;
    private bool disposed;

    /// <summary>Creates the store.</summary>
    /// <param name="connectionString">Postgres connection string.</param>
    /// <param name="options">Tunables, or <see langword="null"/> for the defaults.</param>
    /// <param name="timeProvider">Clock, injected so expiry is testable without sleeping.</param>
    /// <exception cref="ArgumentException">The connection string was missing.</exception>
    public PostgresMcpTaskStore(
        string connectionString,
        PostgresMcpTaskStoreOptions? options = null,
        TimeProvider? timeProvider = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);

        this.connectionString = connectionString;
        this.options = options ?? new PostgresMcpTaskStoreOptions();
        this.timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <inheritdoc />
    public event Action<InputResponseReceivedEventArgs>? InputResponseReceived;

    /// <summary>
    /// Starts the cross-node listener and the safety sweep.
    /// <para>
    /// Explicit rather than started from the constructor: a store that opens connections as a
    /// side effect of construction cannot be built in a DI container without surprising anyone, and a
    /// consumer that only reads task status has no need for either loop.
    /// </para>
    /// </summary>
    /// <param name="cancellationToken">Cancellation token for the initial connection.</param>
    /// <returns>A task that completes once the listening connection is attached.</returns>
    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(this.disposed, this);

        await this.listenerStartGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (this.listenerLoop is not null)
            {
                return;
            }

            // The first LISTEN is awaited so a caller knows the store is attached before it starts
            // handing out tasks; the loop then runs unobserved until shutdown.
            var attached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

            this.listenerLoop = Task.Run(() => this.ListenLoopAsync(attached), CancellationToken.None);
            this.sweepLoop = Task.Run(this.SweepLoopAsync, CancellationToken.None);

            await attached.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            this.listenerStartGate.Release();
        }
    }

    /// <inheritdoc />
    public async Task<McpTaskInfo> CreateTaskAsync(CancellationToken cancellationToken = default)
    {
        var now = this.timeProvider.GetUtcNow();
        var taskId = Guid.NewGuid().ToString("N");
        var expiresAt = this.options.DefaultTimeToLive is { } ttl ? now.Add(ttl) : (DateTimeOffset?)null;

        await using var connection = await this.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = new NpgsqlCommand(
            $"""
            INSERT INTO {PostgresTaskStoreSchema.TasksTable}
                (task_id, status, created_at, last_updated_at, expires_at, poll_interval_ms)
            VALUES (@taskId, @status, @now, @now, @expiresAt, @pollIntervalMs)
            """,
            connection);

        command.Parameters.AddWithValue("taskId", taskId);
        command.Parameters.AddWithValue("status", ToWire(McpTaskStatus.Working));
        command.Parameters.AddWithValue("now", now);
        command.Parameters.AddWithValue("expiresAt", NpgsqlDbType.TimestampTz, expiresAt ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("pollIntervalMs", this.options.DefaultPollIntervalMs);

        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

        return new McpTaskInfo(
            taskId,
            McpTaskStatus.Working,
            now,
            now,
            this.options.DefaultTimeToLive,
            this.options.DefaultPollIntervalMs);
    }

    /// <inheritdoc />
    public async Task<McpTaskInfo?> GetTaskAsync(string taskId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(taskId);

        await using var connection = await this.OpenAsync(cancellationToken).ConfigureAwait(false);

        await using var command = new NpgsqlCommand(
            $"""
            SELECT status, created_at, last_updated_at, expires_at, poll_interval_ms,
                   status_message, result, error
            FROM {PostgresTaskStoreSchema.TasksTable}
            WHERE task_id = @taskId
            """,
            connection);

        command.Parameters.AddWithValue("taskId", taskId);

        McpTaskInfo info;

        await using (var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
        {
            if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                return null;
            }

            var status = FromWire(reader.GetString(0));
            var createdAt = reader.GetFieldValue<DateTimeOffset>(1);
            var lastUpdatedAt = reader.GetFieldValue<DateTimeOffset>(2);
            var expiresAt = reader.IsDBNull(3) ? (DateTimeOffset?)null : reader.GetFieldValue<DateTimeOffset>(3);

            // Expired tasks read as absent rather than as data. Reporting an expired task would let a
            // client poll a result the store has already promised to forget.
            if (expiresAt is { } expiry && this.timeProvider.GetUtcNow() > expiry)
            {
                return null;
            }

            info = new McpTaskInfo(
                taskId,
                status,
                createdAt,
                lastUpdatedAt,
                expiresAt is null ? null : expiresAt.Value - createdAt,
                reader.IsDBNull(4) ? null : reader.GetInt64(4),
                reader.IsDBNull(5) ? null : reader.GetString(5),
                reader.IsDBNull(6) ? null : JsonDocument.Parse(reader.GetString(6)).RootElement,
                reader.IsDBNull(7) ? null : JsonDocument.Parse(reader.GetString(7)).RootElement);
        }

        var requests = await this.ReadInputRequestsAsync(connection, taskId, cancellationToken)
            .ConfigureAwait(false);

        return requests.Count == 0 ? info : info with { InputRequests = requests };
    }

    /// <inheritdoc />
    public Task SetCompletedAsync(string taskId, JsonElement result, CancellationToken cancellationToken = default) =>
        this.SetTerminalAsync(taskId, McpTaskStatus.Completed, "result", result, cancellationToken);

    /// <inheritdoc />
    public Task SetFailedAsync(string taskId, JsonElement error, CancellationToken cancellationToken = default) =>
        this.SetTerminalAsync(taskId, McpTaskStatus.Failed, "error", error, cancellationToken);

    /// <inheritdoc />
    public async Task<bool> SetCancelledAsync(string taskId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(taskId);

        await using var connection = await this.OpenAsync(cancellationToken).ConfigureAwait(false);

        // The terminal guard is in the WHERE clause, not read-then-write. Two nodes completing and
        // cancelling the same task concurrently is exactly the race a stateless server invites, and the
        // row count tells the caller which one won without a transaction.
        await using var command = new NpgsqlCommand(
            $"""
            UPDATE {PostgresTaskStoreSchema.TasksTable}
            SET status = @status, last_updated_at = @now
            WHERE task_id = @taskId AND status NOT IN ('completed', 'cancelled', 'failed')
            """,
            connection);

        command.Parameters.AddWithValue("taskId", taskId);
        command.Parameters.AddWithValue("status", ToWire(McpTaskStatus.Cancelled));
        command.Parameters.AddWithValue("now", this.timeProvider.GetUtcNow());

        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) == 1;
    }

    /// <inheritdoc />
    public async Task SetInputRequestsAsync(
        string taskId,
        IDictionary<string, InputRequest> inputRequests,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(taskId);
        ArgumentNullException.ThrowIfNull(inputRequests);

        if (inputRequests.Count == 0)
        {
            return;
        }

        await using var connection = await this.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        var now = this.timeProvider.GetUtcNow();

        foreach (var (requestId, request) in inputRequests)
        {
            await using var upsert = new NpgsqlCommand(
                $"""
                INSERT INTO {PostgresTaskStoreSchema.InputRequestsTable} (task_id, request_id, request, created_at)
                SELECT @taskId, @requestId, @request::jsonb, @now
                WHERE EXISTS (
                    SELECT 1 FROM {PostgresTaskStoreSchema.TasksTable}
                    WHERE task_id = @taskId AND status NOT IN ('completed', 'cancelled', 'failed'))
                ON CONFLICT (task_id, request_id) DO UPDATE SET request = EXCLUDED.request
                """,
                connection,
                transaction);

            upsert.Parameters.AddWithValue("taskId", taskId);
            upsert.Parameters.AddWithValue("requestId", requestId);
            upsert.Parameters.AddWithValue("request", JsonSerializer.Serialize(request));
            upsert.Parameters.AddWithValue("now", now);

            await upsert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await using (var status = new NpgsqlCommand(
            $"""
            UPDATE {PostgresTaskStoreSchema.TasksTable}
            SET status = @status, last_updated_at = @now
            WHERE task_id = @taskId AND status NOT IN ('completed', 'cancelled', 'failed')
            """,
            connection,
            transaction))
        {
            status.Parameters.AddWithValue("taskId", taskId);
            status.Parameters.AddWithValue("status", ToWire(McpTaskStatus.InputRequired));
            status.Parameters.AddWithValue("now", now);

            await status.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task ResolveInputRequestsAsync(
        string taskId,
        IDictionary<string, InputResponse> inputResponses,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(taskId);
        ArgumentNullException.ThrowIfNull(inputResponses);

        if (inputResponses.Count == 0)
        {
            return;
        }

        var now = this.timeProvider.GetUtcNow();
        var delivered = new List<(string RequestId, InputResponse Response)>();

        await using var connection = await this.OpenAsync(cancellationToken).ConfigureAwait(false);

        await using (var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false))
        {
            // Responses aimed at a terminal task are dropped, matching the in-memory store: nothing is
            // listening for them and recording them would leave rows nobody ever reads.
            await using (var guard = new NpgsqlCommand(
                $"SELECT status FROM {PostgresTaskStoreSchema.TasksTable} WHERE task_id = @taskId FOR UPDATE",
                connection,
                transaction))
            {
                guard.Parameters.AddWithValue("taskId", taskId);

                var current = await guard.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
                if (current is not string statusText || IsTerminal(FromWire(statusText)))
                {
                    return;
                }
            }

            foreach (var (requestId, response) in inputResponses)
            {
                await using var insert = new NpgsqlCommand(
                    $"""
                    INSERT INTO {PostgresTaskStoreSchema.InputResponsesTable} (task_id, request_id, response, created_at)
                    VALUES (@taskId, @requestId, @response::jsonb, @now)
                    ON CONFLICT (task_id, request_id) DO NOTHING
                    """,
                    connection,
                    transaction);

                insert.Parameters.AddWithValue("taskId", taskId);
                insert.Parameters.AddWithValue("requestId", requestId);
                insert.Parameters.AddWithValue("response", JsonSerializer.Serialize(response));
                insert.Parameters.AddWithValue("now", now);

                await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

                await using var remove = new NpgsqlCommand(
                    $"""
                    DELETE FROM {PostgresTaskStoreSchema.InputRequestsTable}
                    WHERE task_id = @taskId AND request_id = @requestId
                    """,
                    connection,
                    transaction);

                remove.Parameters.AddWithValue("taskId", taskId);
                remove.Parameters.AddWithValue("requestId", requestId);

                await remove.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

                delivered.Add((requestId, response));
            }

            // Back to working once nothing is outstanding, mirroring the in-memory store's transition.
            await using (var status = new NpgsqlCommand(
                $"""
                UPDATE {PostgresTaskStoreSchema.TasksTable}
                SET last_updated_at = @now,
                    status = CASE
                        WHEN NOT EXISTS (
                            SELECT 1 FROM {PostgresTaskStoreSchema.InputRequestsTable} WHERE task_id = @taskId)
                        THEN @working
                        ELSE status
                    END
                WHERE task_id = @taskId AND status NOT IN ('completed', 'cancelled', 'failed')
                """,
                connection,
                transaction))
            {
                status.Parameters.AddWithValue("taskId", taskId);
                status.Parameters.AddWithValue("now", now);
                status.Parameters.AddWithValue("working", ToWire(McpTaskStatus.Working));

                await status.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }

        // Notified only AFTER the commit. A notification sent inside the transaction would be delivered
        // on commit anyway, but a woken node reads the response row — so signalling before the row is
        // visible is a race that would look like a lost notification.
        foreach (var (requestId, _) in delivered)
        {
            await this.NotifyAsync(connection, taskId, requestId, cancellationToken).ConfigureAwait(false);
        }

        // Raised locally too, so a single-node deployment never depends on the round trip through
        // Postgres, and a task waiting in this very process is resolved without listener latency.
        foreach (var (requestId, response) in delivered)
        {
            this.Raise(taskId, requestId, response);
        }
    }

    /// <summary>Stops the background loops and releases the listening connection.</summary>
    /// <returns>A task that completes when both loops have stopped.</returns>
    public async ValueTask DisposeAsync()
    {
        if (this.disposed)
        {
            return;
        }

        this.disposed = true;

        await this.shutdown.CancelAsync().ConfigureAwait(false);

        foreach (var loop in new[] { this.listenerLoop, this.sweepLoop })
        {
            if (loop is null)
            {
                continue;
            }

            try
            {
                await loop.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Expected: shutdown is how the loops end.
            }
        }

        this.shutdown.Dispose();
        this.listenerStartGate.Dispose();
    }

    private static bool IsTerminal(McpTaskStatus status) =>
        status is McpTaskStatus.Completed or McpTaskStatus.Cancelled or McpTaskStatus.Failed;

    private static string ToWire(McpTaskStatus status) => status switch
    {
        McpTaskStatus.Working => "working",
        McpTaskStatus.InputRequired => "input_required",
        McpTaskStatus.Completed => "completed",
        McpTaskStatus.Cancelled => "cancelled",
        McpTaskStatus.Failed => "failed",
        _ => throw new ArgumentOutOfRangeException(nameof(status), status, "Unknown task status."),
    };

    private static McpTaskStatus FromWire(string status) => status switch
    {
        "working" => McpTaskStatus.Working,
        "input_required" => McpTaskStatus.InputRequired,
        "completed" => McpTaskStatus.Completed,
        "cancelled" => McpTaskStatus.Cancelled,
        "failed" => McpTaskStatus.Failed,
        _ => throw new InvalidOperationException($"Unrecognised task status '{status}' in the store."),
    };

    private async Task<NpgsqlConnection> OpenAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(this.disposed, this);

        var connection = new NpgsqlConnection(this.connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

        return connection;
    }

    private async Task<Dictionary<string, InputRequest>> ReadInputRequestsAsync(
        NpgsqlConnection connection,
        string taskId,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            $"""
            SELECT request_id, request
            FROM {PostgresTaskStoreSchema.InputRequestsTable}
            WHERE task_id = @taskId
            ORDER BY created_at, request_id
            """,
            connection);

        command.Parameters.AddWithValue("taskId", taskId);

        var requests = new Dictionary<string, InputRequest>(StringComparer.Ordinal);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var request = JsonSerializer.Deserialize<InputRequest>(reader.GetString(1));
            if (request is not null)
            {
                requests[reader.GetString(0)] = request;
            }
        }

        return requests;
    }

    private async Task SetTerminalAsync(
        string taskId,
        McpTaskStatus status,
        string payloadColumn,
        JsonElement payload,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(taskId);

        await using var connection = await this.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = new NpgsqlCommand(
            $"""
            UPDATE {PostgresTaskStoreSchema.TasksTable}
            SET status = @status, {payloadColumn} = @payload::jsonb, last_updated_at = @now
            WHERE task_id = @taskId AND status NOT IN ('completed', 'cancelled', 'failed')
            """,
            connection);

        command.Parameters.AddWithValue("taskId", taskId);
        command.Parameters.AddWithValue("status", ToWire(status));
        command.Parameters.AddWithValue("payload", payload.GetRawText());
        command.Parameters.AddWithValue("now", this.timeProvider.GetUtcNow());

        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task NotifyAsync(
        NpgsqlConnection connection,
        string taskId,
        string requestId,
        CancellationToken cancellationToken)
    {
        // pg_notify() rather than `NOTIFY channel, 'payload'`: NOTIFY takes a literal, so building it by
        // string concatenation would be an injection point on ids this store does not fully control.
        await using var command = new NpgsqlCommand("SELECT pg_notify(@channel, @payload)", connection);

        command.Parameters.AddWithValue("channel", PostgresTaskStoreSchema.InputResponseChannel);
        command.Parameters.AddWithValue("payload", $"{taskId}:{requestId}");

        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private void Raise(string taskId, string requestId, InputResponse response) =>
        this.InputResponseReceived?.Invoke(new InputResponseReceivedEventArgs
        {
            TaskId = taskId,
            RequestId = requestId,
            Response = response,
        });

    private async Task ListenLoopAsync(TaskCompletionSource attached)
    {
        var token = this.shutdown.Token;
        var everAttached = false;

        while (!token.IsCancellationRequested)
        {
            try
            {
                await using var connection = new NpgsqlConnection(this.connectionString);

                connection.Notification += this.OnNotification;

                await connection.OpenAsync(token).ConfigureAwait(false);

                await using (var listen = new NpgsqlCommand(
                    $"LISTEN {PostgresTaskStoreSchema.InputResponseChannel}", connection))
                {
                    await listen.ExecuteNonQueryAsync(token).ConfigureAwait(false);
                }

                if (!everAttached)
                {
                    everAttached = true;
                    attached.TrySetResult();
                }

                // WaitAsync is what actually surfaces notifications: without a read in flight, Npgsql
                // has no reason to touch the socket and the Notification event never fires.
                while (!token.IsCancellationRequested)
                {
                    await connection.WaitAsync(token).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                break;
            }
            catch (Exception) when (!token.IsCancellationRequested)
            {
                // A dropped listening connection must not take the store down: reconnect and rely on the
                // sweep to pick up whatever was notified while detached. Unblocking the first caller
                // even on failure is deliberate — a store that cannot listen still persists correctly,
                // and refusing to start would be a worse failure than degraded latency.
                attached.TrySetResult();

                try
                {
                    await Task.Delay(this.options.ListenerReconnectDelay, token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }

        attached.TrySetResult();
    }

    private void OnNotification(object sender, NpgsqlNotificationEventArgs args)
    {
        var separator = args.Payload.LastIndexOf(':');
        if (separator <= 0 || separator == args.Payload.Length - 1)
        {
            return;
        }

        var taskId = args.Payload[..separator];
        var requestId = args.Payload[(separator + 1)..];

        // Fire and forget: this runs on Npgsql's notification callback, and blocking it would stall
        // every other notification on the connection.
        _ = Task.Run(() => this.DeliverAsync(taskId, requestId), CancellationToken.None);
    }

    private async Task DeliverAsync(string taskId, string requestId)
    {
        try
        {
            await using var connection = await this.OpenAsync(this.shutdown.Token).ConfigureAwait(false);
            await using var command = new NpgsqlCommand(
                $"""
                SELECT response FROM {PostgresTaskStoreSchema.InputResponsesTable}
                WHERE task_id = @taskId AND request_id = @requestId
                """,
                connection);

            command.Parameters.AddWithValue("taskId", taskId);
            command.Parameters.AddWithValue("requestId", requestId);

            var raw = await command.ExecuteScalarAsync(this.shutdown.Token).ConfigureAwait(false);
            if (raw is not string json)
            {
                return;
            }

            var response = JsonSerializer.Deserialize<InputResponse>(json);
            if (response is not null)
            {
                this.Raise(taskId, requestId, response);
            }
        }
        catch (Exception) when (!this.shutdown.IsCancellationRequested)
        {
            // A failed delivery leaves the response row in place, so the sweep retries it. Throwing
            // here would go unobserved on a background task.
        }
    }

    private async Task SweepLoopAsync()
    {
        var token = this.shutdown.Token;

        while (!token.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(this.options.SweepInterval, token).ConfigureAwait(false);
                await this.SweepAsync(token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception) when (!token.IsCancellationRequested)
            {
                // Swept again next interval.
            }
        }
    }

    /// <summary>
    /// Re-delivers recent responses and deletes expired tasks.
    /// <para>
    /// Exposed so a test can drive it directly instead of waiting out the interval, and so a host can
    /// trigger it after a restart when a notification may have been missed while nothing was listening.
    /// </para>
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A task that completes when the sweep is done.</returns>
    public async Task SweepAsync(CancellationToken cancellationToken = default)
    {
        var now = this.timeProvider.GetUtcNow();

        await using var connection = await this.OpenAsync(cancellationToken).ConfigureAwait(false);

        await using (var expired = new NpgsqlCommand(
            $"DELETE FROM {PostgresTaskStoreSchema.TasksTable} WHERE expires_at IS NOT NULL AND expires_at < @now",
            connection))
        {
            expired.Parameters.AddWithValue("now", now);

            await expired.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        // Only responses for tasks still awaiting something are re-delivered. Re-raising every response
        // ever recorded would grow unboundedly and hand the SDK events for requests it stopped waiting
        // on long ago.
        await using var pending = new NpgsqlCommand(
            $"""
            SELECT r.task_id, r.request_id, r.response
            FROM {PostgresTaskStoreSchema.InputResponsesTable} r
            JOIN {PostgresTaskStoreSchema.TasksTable} t ON t.task_id = r.task_id
            WHERE t.status NOT IN ('completed', 'cancelled', 'failed')
              AND r.created_at > @since
            ORDER BY r.created_at
            """,
            connection);

        pending.Parameters.AddWithValue("since", now - this.options.ResponseRedeliveryWindow);

        var replay = new List<(string TaskId, string RequestId, InputResponse Response)>();

        await using (var reader = await pending.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
        {
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                var response = JsonSerializer.Deserialize<InputResponse>(reader.GetString(2));
                if (response is not null)
                {
                    replay.Add((reader.GetString(0), reader.GetString(1), response));
                }
            }
        }

        foreach (var (taskId, requestId, response) in replay)
        {
            // Duplicate delivery is safe by construction: the SDK's handler resolves a
            // TaskCompletionSource with TrySetResult and unsubscribes, so a second event is a no-op.
            // That is what makes at-least-once the right guarantee to aim for here.
            this.Raise(taskId, requestId, response);
        }
    }
}
