# ADR-0003 · Cross-node delivery of task input responses via `LISTEN`/`NOTIFY`

| | |
|---|---|
| **Status** | Accepted |
| **Date** | 28 Aug 2026 |
| **Phase** | F1 (toolkit) |
| **Depends on** | [ADR-0001](./0001-streamable-http-session-mode.md) — the server is stateless, which is what creates this problem |

## Context

`Talent.Mcp.Toolkit` ships a `PostgresMcpTaskStore` because the SDK provides only
`InMemoryMcpTaskStore`, and a restart with in-memory state loses every in-flight `bulk_score_shortlist`
run. Persisting task rows is straightforward. **The event on the interface is not.**

`IMcpTaskStore` (read at tag `v2.2.0`) declares:

```csharp
event Action<InputResponseReceivedEventArgs>? InputResponseReceived;
```

And `McpTasksServerExtensions` consumes it like this — the shape matters, so it is quoted rather than
summarised:

```csharp
var tcs = new TaskCompletionSource<InputResponse>(TaskCreationOptions.RunContinuationsAsynchronously);

void handler(InputResponseReceivedEventArgs args)
{
    if (args.TaskId == taskId && args.RequestId == requestId) { tcs.TrySetResult(args.Response); }
}

store.InputResponseReceived += handler;
try
{
    await store.SetInputRequestsAsync(taskId, new() { [requestId] = inputRequest }, cancellationToken);
    var response = await tcs.Task.WaitAsync(cancellationToken);
    return JsonNode.Parse(response.RawValue.GetRawText());
}
finally { store.InputResponseReceived -= handler; }
```

So the subscriber lives **in the process running the task**, blocked on a `TaskCompletionSource`.

`ResolveInputRequestsAsync` is called by whichever process handles the client's follow-up HTTP request.
Per ADR-0001 this server is stateless with no session affinity — that is the whole point — so with more
than one replica **that is usually a different process**.

The naive Postgres store therefore has a specific, silent failure: node B persists the response and
raises the event on *its own* store instance, while node A stays blocked until its cancellation token
fires. The task hangs having already received the answer it was waiting for. Nothing errors, and the
row in the database looks correct.

## Decision

Carry the signal between nodes with Postgres **`LISTEN`/`NOTIFY`**.

The resolving node persists the response, commits, then issues `pg_notify`. Every node holds a
dedicated listening connection and re-raises `InputResponseReceived` locally when it hears a
notification for a task.

Four details that are load-bearing rather than incidental:

**1. Responses are persisted, not merely signalled.** A `NOTIFY` payload is capped at 8000 bytes and is
lost outright if no connection is attached at that instant. So the payload carries only
`taskId:requestId` and the row is the durable record; a woken node reads it back. The notification is a
wake-up hint, never the data.

**2. `pg_notify()`, not `NOTIFY channel, 'payload'`.** `NOTIFY` takes a string literal, so building one
by concatenation would be an injection point on ids the store does not fully control.
`pg_notify(@channel, @payload)` parameterises both.

**3. A periodic sweep re-delivers recent responses.** A notification issued while a listener is
reconnecting is gone. The sweep turns that into added latency rather than a hang, and it is bounded to
a five-minute window and to non-terminal tasks — replaying every response ever recorded would grow
with the table and hand the SDK events for requests it stopped waiting on.

**4. At-least-once, deliberately.** Duplicate delivery is safe because the SDK's handler uses
`TrySetResult` and then unsubscribes, so a second event is a no-op. Aiming for exactly-once would mean
tracking delivery state per node for no benefit. A test asserts the duplicate path stays safe, so a
future change to redelivery cannot quietly break the assumption this rests on.

The store also raises the event locally on resolve, so a single-node deployment never pays the round
trip through Postgres, and a task waiting in the same process is resolved without listener latency.

## Alternatives considered

**Polling only.** Each node polls its own outstanding requests for responses. Simpler — no listening
connection, no reconnect logic — but it trades a dedicated connection for latency on every MRTR
round-trip, and MRTR is interactive: `reject_candidate` is waiting for a human. Rejected as the primary
mechanism, and kept as the fallback, which is what the sweep is.

**Session affinity at the load balancer.** Would make the in-process event work. Rejected because it
contradicts ADR-0001 and the revision's design: the reason sessions were removed is so that requests
can be served by any process. Reintroducing affinity to work around a store is the tail wagging the
dog.

**A dedicated message broker.** Correct at scale and disproportionate here. It adds a service to
`compose.yaml` for one notification type, and the catalog's constraint is that the whole stack comes up
with one `docker compose up`. Postgres is already a hard dependency; the broker would not be.

**EF Core instead of raw Npgsql.** Rejected. The toolkit ships to NuGet, so an EF dependency would be
imposed on every consumer for four tables the store fully owns — and `LISTEN`/`NOTIFY` needs a raw
connection that EF has no abstraction for. The schema is exposed as plain DDL so a consumer that does
use EF can fold it into their own migration; the table shape is the contract, not the mechanism.

## Consequences

- Each store instance holds **one long-lived connection** beyond the pool. Worth stating in
  deployment notes: a replica count of *n* means *n* extra connections.
- The listener reconnects on failure and never takes the store down. `StartAsync` unblocks even when
  the first attach fails, because a store that cannot listen still persists correctly, and refusing to
  start would be a worse failure than degraded latency.
- `StartAsync` is explicit rather than called from the constructor: a type that opens connections as a
  side effect of construction cannot be registered in a container without surprising someone, and a
  consumer that only reads task status needs neither loop.
- **Tasks are not an audit log.** Default TTL is one hour and the sweep deletes expired rows. The point
  of persistence here is that a restart does not lose work in flight; a durable record of what a bulk
  scoring run produced belongs in the domain tables.

## Verification

`tests/Talent.Infrastructure.Tests/PostgresMcpTaskStoreTests.cs`, against real Postgres via
Testcontainers. The two tests that justify the design:

- `A_response_resolved_on_another_instance_reaches_the_waiting_one` — two store instances stand in for
  two replicas. The resolving instance raises the event only on itself, so the waiting instance can
  only learn through Postgres. The sweep interval (30s) is far longer than the assertion timeout (15s),
  so a pass cannot be the fallback quietly covering for a broken listener.
- `A_missed_notification_is_recovered_by_the_sweep` — the waiting store never starts its listener, so
  the notification cannot arrive. The test asserts the event has *not* fired, then drives the sweep
  and asserts it does.

Plus the state machine: terminal transitions guarded in the `WHERE` clause rather than
read-then-write (two nodes racing to finish one task is what a stateless server invites), responses for
terminal tasks dropped, `input_required` held while any request is outstanding, and expired tasks
reading as absent before the sweep physically removes them.
