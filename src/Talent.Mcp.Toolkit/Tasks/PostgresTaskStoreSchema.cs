namespace Talent.Mcp.Toolkit.Tasks;

using Npgsql;

/// <summary>
/// The tables <see cref="PostgresMcpTaskStore"/> owns, and the DDL that creates them.
/// <para>
/// Plain SQL rather than an EF Core migration on purpose. The toolkit ships to NuGet, and taking an EF
/// dependency would force that on every consumer for four tables it fully owns — while
/// <c>LISTEN</c>/<c>NOTIFY</c> needs a raw connection that EF has no abstraction for anyway. A consumer
/// that already uses EF is free to fold this DDL into its own migration instead of calling
/// <see cref="EnsureCreatedAsync"/>; the table shape is the contract, not the mechanism.
/// </para>
/// </summary>
public static class PostgresTaskStoreSchema
{
    /// <summary>Table holding one row per task.</summary>
    public const string TasksTable = "mcp_tasks";

    /// <summary>Table holding outstanding input requests for a task.</summary>
    public const string InputRequestsTable = "mcp_task_input_requests";

    /// <summary>Table holding delivered input responses for a task.</summary>
    public const string InputResponsesTable = "mcp_task_input_responses";

    /// <summary>
    /// The <c>NOTIFY</c> channel used to wake a node waiting on an input response.
    /// <para>
    /// Lower-case and unquoted: Postgres folds unquoted identifiers to lower case, so a mixed-case
    /// channel name would be listened to and notified under different names.
    /// </para>
    /// </summary>
    public const string InputResponseChannel = "mcp_task_input_response";

    private const string Ddl = $"""
        CREATE TABLE IF NOT EXISTS {TasksTable} (
            task_id           text        PRIMARY KEY,
            status            text        NOT NULL,
            created_at        timestamptz NOT NULL,
            last_updated_at   timestamptz NOT NULL,
            expires_at        timestamptz NULL,
            poll_interval_ms  bigint      NULL,
            status_message    text        NULL,
            result            jsonb       NULL,
            error             jsonb       NULL
        );

        -- Expiry is stored as an absolute instant rather than the TTL the protocol exposes. A TTL plus
        -- created_at would have to be recomputed on every read and could not be indexed; an absolute
        -- column makes the sweep a single range scan.
        CREATE INDEX IF NOT EXISTS ix_{TasksTable}_expires_at
            ON {TasksTable} (expires_at) WHERE expires_at IS NOT NULL;

        CREATE TABLE IF NOT EXISTS {InputRequestsTable} (
            task_id     text        NOT NULL,
            request_id  text        NOT NULL,
            request     jsonb       NOT NULL,
            created_at  timestamptz NOT NULL,
            PRIMARY KEY (task_id, request_id),
            FOREIGN KEY (task_id) REFERENCES {TasksTable} (task_id) ON DELETE CASCADE
        );

        -- Responses are PERSISTED, not merely signalled. The NOTIFY payload is capped at 8000 bytes and
        -- a notification is lost if the listening connection is not attached at that instant, so the
        -- notification is only a wake-up hint: the row is the durable record the woken node reads.
        CREATE TABLE IF NOT EXISTS {InputResponsesTable} (
            task_id      text        NOT NULL,
            request_id   text        NOT NULL,
            response     jsonb       NOT NULL,
            created_at   timestamptz NOT NULL,
            PRIMARY KEY (task_id, request_id),
            FOREIGN KEY (task_id) REFERENCES {TasksTable} (task_id) ON DELETE CASCADE
        );

        CREATE INDEX IF NOT EXISTS ix_{InputResponsesTable}_created_at
            ON {InputResponsesTable} (created_at);
        """;

    /// <summary>
    /// Creates the tables if they are absent. Idempotent, so it is safe to call on every start.
    /// </summary>
    /// <param name="connectionString">Postgres connection string.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A task that completes when the schema exists.</returns>
    /// <exception cref="ArgumentException">The connection string was missing.</exception>
    public static async Task EnsureCreatedAsync(
        string connectionString,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);

        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

        await using var command = new NpgsqlCommand(Ddl, connection);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }
}
