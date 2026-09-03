namespace Talent.Mcp.E2E;

using ModelContextProtocol.Protocol;
using Xunit;

/// <summary>
/// <c>search_jobs</c> paginated by a signed handle, over a real HTTP connection to the real host —
/// the pattern the 2026-07-28 revision requires in place of <c>Mcp-Session-Id</c> (SEP-2567, SEP-2575).
/// <para>
/// Run against the in-memory transport, this would only prove the handle codec works; run here, it also
/// proves the handle survives real JSON-RPC serialization over a real socket, and — in the second test —
/// that it means the same thing to a connection that never made the original call, which is the actual
/// point of moving state out of the session and into the handle.
/// </para>
/// </summary>
[Collection(RealServerCollection.Name)]
public sealed class SearchJobsPaginationE2ETests
{
    private readonly RealServerFixture fixture;

    public SearchJobsPaginationE2ETests(RealServerFixture fixture)
    {
        this.fixture = fixture;
    }

    [Fact]
    public async Task Paginating_by_handle_covers_every_seeded_job_exactly_once()
    {
        var token = await this.fixture.MintTokenAsync("openid talent.jobs.read");
        await using var client = await this.fixture.CreateClientAsync(token);

        var seenIds = new List<string>();
        string? pageHandle = null;
        var pagesRead = 0;

        do
        {
            var arguments = pageHandle is null
                ? new Dictionary<string, object?>()
                : new Dictionary<string, object?> { ["pageHandle"] = pageHandle };

            var result = await client.CallToolAsync("search_jobs", arguments);
            Assert.False(result.IsError is true, TextOf(result));

            var payload = result.StructuredContent!.Value;
            seenIds.AddRange(payload.GetProperty("jobs").EnumerateArray()
                .Select(static j => j.GetProperty("id").GetString()!));

            pagesRead++;

            // Absent, not null, on the last page — the same omission behaviour documented for every
            // other optional field on the wire. hasMore is the explicit signal a caller checks instead.
            pageHandle = payload.TryGetProperty("nextPageHandle", out var handle) ? handle.GetString() : null;
            Assert.Equal(pageHandle is not null, payload.GetProperty("hasMore").GetBoolean());
        }
        while (pageHandle is not null);

        // 12 seeded jobs (SeedData.CreateJobs) at the fixture's DefaultPageSize of 5: 5 + 5 + 2.
        Assert.Equal(3, pagesRead);
        Assert.Equal(12, seenIds.Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public async Task A_handle_minted_for_one_connection_is_honoured_by_a_different_one()
    {
        // Under SessionMode.Stateless there is no session tying a handle to the connection that
        // requested it — a retry landing on a different node in production must still work. Two
        // independent client connections is how that gets exercised here.
        var token = await this.fixture.MintTokenAsync("openid talent.jobs.read");
        await using var first = await this.fixture.CreateClientAsync(token);

        var firstPage = await first.CallToolAsync("search_jobs", new Dictionary<string, object?>());
        var firstHandle = firstPage.StructuredContent!.Value.GetProperty("nextPageHandle").GetString();
        Assert.NotNull(firstHandle);

        await using var second = await this.fixture.CreateClientAsync(token);
        var secondPage = await second
            .CallToolAsync("search_jobs", new Dictionary<string, object?> { ["pageHandle"] = firstHandle });

        Assert.False(secondPage.IsError is true, TextOf(secondPage));
        Assert.NotEmpty(secondPage.StructuredContent!.Value.GetProperty("jobs").EnumerateArray());
    }

    [Fact]
    public async Task A_page_beyond_the_last_one_is_refused_not_returned_empty()
    {
        var token = await this.fixture.MintTokenAsync("openid talent.jobs.read");
        await using var client = await this.fixture.CreateClientAsync(token);

        string? pageHandle = null;
        do
        {
            var arguments = pageHandle is null
                ? new Dictionary<string, object?>()
                : new Dictionary<string, object?> { ["pageHandle"] = pageHandle };
            var result = await client.CallToolAsync("search_jobs", arguments);
            var payload = result.StructuredContent!.Value;
            pageHandle = payload.TryGetProperty("nextPageHandle", out var handle) ? handle.GetString() : null;
        }
        while (pageHandle is not null);

        // Re-minting a handle that already carried "no more pages" and replaying it must not be treated
        // as a fresh, filter-less search — that would silently hand back page one instead of surfacing
        // that the handle has nothing left to continue.
        var replay = await client
            .CallToolAsync("search_jobs", new Dictionary<string, object?> { ["pageHandle"] = "not-a-real-handle" });

        Assert.True(replay.IsError);
        Assert.Contains("not valid or has expired", TextOf(replay), StringComparison.Ordinal);
    }

    private static string TextOf(CallToolResult result) =>
        string.Join(
            Environment.NewLine,
            result.Content.OfType<TextContentBlock>().Select(static block => block.Text));
}
