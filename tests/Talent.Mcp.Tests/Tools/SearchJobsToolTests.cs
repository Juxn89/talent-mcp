namespace Talent.Mcp.Tests.Tools;

using System.Text.Json;
using Talent.Application.Configuration;
using Talent.Application.UseCases;
using Talent.Domain.Enums;
using Talent.Mcp.Tools.Constants;
using Xunit;

/// <summary>
/// <c>search_jobs</c> over the real transport: the tool whose whole job is to prove that a signed
/// handle can replace a session.
/// </summary>
public sealed class SearchJobsToolTests
{
    [Fact]
    public async Task A_first_page_returns_a_handle_and_the_total()
    {
        var jobs = new FakeJobRepository(ToolTestData.Many(12));
        await using var harness = await ToolHarness.StartAsync(jobs);

        var page = await harness.SearchAsync(pageSize: 5);

        Assert.Equal(5, page.GetProperty("jobs").GetArrayLength());
        Assert.Equal(12, page.GetProperty("totalMatches").GetInt32());
        Assert.True(page.GetProperty("hasMore").GetBoolean());
        Assert.False(string.IsNullOrWhiteSpace(page.GetProperty("nextPageHandle").GetString()));
    }

    [Fact]
    public async Task Paging_through_with_handles_visits_every_row_exactly_once()
    {
        var jobs = new FakeJobRepository(ToolTestData.Many(12));
        await using var harness = await ToolHarness.StartAsync(jobs);

        var seen = new List<string>();
        var page = await harness.SearchAsync(pageSize: 5);
        var pages = 0;

        while (true)
        {
            pages++;
            seen.AddRange(page.GetProperty("jobs").EnumerateArray().Select(j => j.GetProperty("id").GetString()!));

            if (!page.TryGetProperty("nextPageHandle", out var handleElement))
            {
                break;
            }

            var handle = handleElement.GetString();

            page = await harness.SearchAsync(pageHandle: handle);
        }

        Assert.Equal(3, pages);
        Assert.Equal(12, seen.Count);

        // The assertion that matters: no duplicates and no gaps. Both are what a non-total sort order
        // produces at a page boundary, and neither shows up in a single-page test.
        Assert.Equal(12, seen.Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public async Task The_last_page_reports_no_more()
    {
        var jobs = new FakeJobRepository(ToolTestData.Many(4));
        await using var harness = await ToolHarness.StartAsync(jobs);

        var page = await harness.SearchAsync(pageSize: 10);

        Assert.False(page.GetProperty("hasMore").GetBoolean());

        // The field is ABSENT, not null: the SDK's serializer omits nulls. Which is precisely why the
        // response also carries hasMore — a model reasoning about a missing property is a model guessing.
        Assert.False(page.TryGetProperty("nextPageHandle", out _));
    }

    [Fact]
    public async Task A_handle_carries_the_criteria_so_resent_filters_cannot_change_the_result_set()
    {
        var jobs = new FakeJobRepository(
            [.. ToolTestData.Many(6), ToolTestData.BerlinPlatform]);
        await using var harness = await ToolHarness.StartAsync(jobs);

        var first = await harness.SearchAsync(countryCode: "ES", pageSize: 3);
        var handle = first.GetProperty("nextPageHandle").GetString();

        // The client sends a contradictory filter alongside the handle. The handle must win: page 2 of
        // an ES search is not page 2 of a DE search, and honouring the argument here is the classic
        // pagination bug that silently skips and repeats rows.
        await harness.SearchAsync(pageHandle: handle, countryCode: "DE");

        var continued = jobs.Searches[^1];
        Assert.Equal("ES", continued.CountryCode);
        Assert.Equal(3, continued.Skip);
    }

    [Fact]
    public async Task A_forged_handle_is_refused_with_an_actionable_message()
    {
        await using var harness = await ToolHarness.StartAsync(new FakeJobRepository(ToolTestData.Many(4)));

        var result = await harness.CallAsync(
            Mcp.ToolNames.SearchJobs,
            new Dictionary<string, object?> { ["pageHandle"] = "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA" });

        Assert.True(result.IsError);
        Assert.Contains("not valid or has expired", harness.TextOf(result), StringComparison.Ordinal);

        // Actionable: it says what to do next. An error a model cannot recover from turns a stale handle
        // into a dead conversation.
        Assert.Contains("without pageHandle", harness.TextOf(result), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_handle_minted_for_another_payload_type_is_refused()
    {
        await using var harness = await ToolHarness.StartAsync(new FakeJobRepository(ToolTestData.Many(4)));

        // Signed by this very server, unexpired, and carrying the wrong payload type. Without the
        // payload-type marker in the signed region, System.Text.Json's leniency would deserialize this
        // into a cursor with a null query and Skip 0 — a silently wrong page rather than a refusal.
        var foreign = harness.Mint(new { CandidateIds = new[] { Guid.NewGuid() } }, TimeSpan.FromMinutes(5));

        var result = await harness.CallAsync(
            Mcp.ToolNames.SearchJobs,
            new Dictionary<string, object?> { ["pageHandle"] = foreign });

        Assert.True(result.IsError);
        Assert.Contains("not valid or has expired", harness.TextOf(result), StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_expired_handle_is_refused()
    {
        var clock = new MutableClock(new DateTimeOffset(2026, 9, 1, 12, 0, 0, TimeSpan.Zero));
        var options = new TalentOptions { PaginationHandleTimeToLive = TimeSpan.FromMinutes(10) };

        await using var harness = await ToolHarness.StartAsync(
            new FakeJobRepository(ToolTestData.Many(12)),
            options: options,
            timeProvider: clock);

        var first = await harness.SearchAsync(pageSize: 5);
        var handle = first.GetProperty("nextPageHandle").GetString();

        clock.Advance(TimeSpan.FromMinutes(11));

        var result = await harness.CallAsync(
            Mcp.ToolNames.SearchJobs,
            new Dictionary<string, object?> { ["pageHandle"] = handle });

        Assert.True(result.IsError);
    }

    [Fact]
    public async Task A_page_size_beyond_the_maximum_is_clamped_rather_than_refused()
    {
        var jobs = new FakeJobRepository(ToolTestData.Many(12));
        var options = new TalentOptions { DefaultPageSize = 5, MaxPageSize = 6 };
        await using var harness = await ToolHarness.StartAsync(jobs, options: options);

        var page = await harness.SearchAsync(pageSize: 1000);

        // A caller asking for too much gets the most it may have. More useful than an error, and it
        // still caps the response — which is the point of the maximum.
        Assert.Equal(6, page.GetProperty("jobs").GetArrayLength());
    }

    [Fact]
    public async Task Filters_reach_the_repository_as_typed_criteria()
    {
        var jobs = new FakeJobRepository([ToolTestData.MadridBackend, ToolTestData.BerlinPlatform]);
        await using var harness = await ToolHarness.StartAsync(jobs);

        var page = await harness.SearchAsync(
            query: "Platform",
            requiredSkillIds: ["kubernetes"],
            countryCode: "DE",
            arrangement: WorkArrangement.Remote);

        var criteria = Assert.Single(jobs.Searches);
        Assert.Equal("Platform", criteria.Query);
        Assert.Equal(["kubernetes"], criteria.RequiredSkillIds);
        Assert.Equal("DE", criteria.CountryCode);
        Assert.Equal(WorkArrangement.Remote, criteria.Arrangement);

        var only = Assert.Single(page.GetProperty("jobs").EnumerateArray().ToArray());
        Assert.Equal("Platform Engineer", only.GetProperty("title").GetString());
    }

    [Fact]
    public async Task Search_results_omit_the_description_and_keep_the_salary_flag()
    {
        var jobs = new FakeJobRepository([ToolTestData.MadridBackend, ToolTestData.BerlinPlatform]);
        await using var harness = await ToolHarness.StartAsync(jobs);

        var page = await harness.SearchAsync();
        var summaries = page.GetProperty("jobs").EnumerateArray().ToArray();

        Assert.All(summaries, job => Assert.False(job.TryGetProperty("description", out _)));

        var undisclosed = summaries.Single(j => j.GetProperty("title").GetString() == "Platform Engineer");
        Assert.False(undisclosed.GetProperty("salary").GetProperty("isDisclosed").GetBoolean());
    }

    [Fact]
    public async Task A_handle_is_opaque_and_reveals_nothing_about_the_cursor()
    {
        var jobs = new FakeJobRepository(ToolTestData.Many(12));
        await using var harness = await ToolHarness.StartAsync(jobs);

        var page = await harness.SearchAsync(query: "Engineer", countryCode: "ES", pageSize: 5);
        var handle = page.GetProperty("nextPageHandle").GetString()!;

        // Signed, not encrypted — so this is not a confidentiality claim about the payload. It asserts
        // the handle is not a readable structure a caller could hand-edit into a different offset.
        Assert.DoesNotContain("Engineer", handle, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("skip", handle, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task The_cursor_type_is_what_the_handle_actually_carries()
    {
        var jobs = new FakeJobRepository(ToolTestData.Many(12));
        await using var harness = await ToolHarness.StartAsync(jobs);

        var page = await harness.SearchAsync(query: "Engineer", pageSize: 5);
        var handle = page.GetProperty("nextPageHandle").GetString()!;

        Assert.True(harness.TryRead<JobSearchCursor>(handle, out var cursor));
        Assert.Equal("Engineer", cursor!.Query);
        Assert.Equal(5, cursor.Skip);
        Assert.Equal(5, cursor.Take);
    }
}

/// <summary>A clock a test can move, so handle expiry is exercised without sleeping.</summary>
internal sealed class MutableClock(DateTimeOffset now) : TimeProvider
{
    private DateTimeOffset current = now;

    /// <inheritdoc />
    public override DateTimeOffset GetUtcNow() => this.current;

    /// <summary>Moves the clock forward.</summary>
    /// <param name="by">How far.</param>
    public void Advance(TimeSpan by) => this.current = this.current.Add(by);
}
