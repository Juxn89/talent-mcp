namespace Talent.Mcp.Tests;

using Talent.Application.Configuration;
using Talent.Application.Ports;
using Talent.Application.UseCases;
using Talent.Domain.Entities;
using Talent.Domain.Enums;
using Talent.Domain.ValueObjects;
using Talent.Infrastructure.Handles;
using Xunit;

/// <summary>
/// Use-case tests against fake ports — no mocking framework, no container.
/// <para>
/// The handle codec is the real one, not a fake. Pagination is the interaction between the use case
/// and the signing, so faking the codec would test the half that has no bugs in it.
/// </para>
/// </summary>
public sealed class SearchJobsUseCaseTests
{
    private static readonly byte[] Key = Enumerable.Range(0, 32).Select(i => (byte)i).ToArray();

    [Fact]
    public async Task First_page_returns_a_handle_when_more_results_exist()
    {
        var (useCase, _) = Build(jobCount: 25, pageSize: 10);

        var result = await useCase.ExecuteAsync("engineer", null, null);

        Assert.Equal(10, result.Jobs.Count);
        Assert.Equal(25, result.TotalMatches);
        Assert.NotNull(result.NextPageHandle);
    }

    [Fact]
    public async Task Last_page_returns_no_handle()
    {
        var (useCase, _) = Build(jobCount: 5, pageSize: 10);

        var result = await useCase.ExecuteAsync("engineer", null, null);

        Assert.Equal(5, result.Jobs.Count);
        Assert.Null(result.NextPageHandle);
    }

    [Fact]
    public async Task Paginating_with_handles_walks_every_result_exactly_once()
    {
        var (useCase, _) = Build(jobCount: 25, pageSize: 10);

        var seen = new List<Guid>();
        var page = await useCase.ExecuteAsync("engineer", null, null);
        seen.AddRange(page.Jobs.Select(j => j.Id));

        var pages = 1;
        while (page.NextPageHandle is { } handle)
        {
            var (next, failure) = await useCase.ContinueAsync(handle);

            Assert.Equal(SearchJobsFailure.None, failure);
            page = next!;
            seen.AddRange(page.Jobs.Select(j => j.Id));
            pages++;
        }

        Assert.Equal(3, pages);
        Assert.Equal(25, seen.Count);
        Assert.Equal(25, seen.Distinct().Count());
    }

    [Fact]
    public async Task The_handle_carries_the_criteria_so_a_continuation_cannot_change_them()
    {
        // The bug this prevents: if only the offset travelled, page 2 could be an offset into a
        // different result set, silently skipping and repeating rows.
        var (useCase, repository) = Build(jobCount: 25, pageSize: 10);

        var first = await useCase.ExecuteAsync("engineer", ["dotnet"], "ES", WorkArrangement.Hybrid);
        await useCase.ContinueAsync(first.NextPageHandle!);

        var continuation = repository.Calls[^1];

        Assert.Equal("engineer", continuation.Query);
        Assert.Equal(["dotnet"], continuation.RequiredSkillIds);
        Assert.Equal("ES", continuation.CountryCode);
        Assert.Equal(WorkArrangement.Hybrid, continuation.Arrangement);
        Assert.Equal(10, continuation.Skip);
    }

    [Theory]
    [InlineData("not-a-handle")]
    [InlineData("")]
    [InlineData(null)]
    public async Task A_foreign_or_malformed_handle_is_refused(string? handle)
    {
        var (useCase, _) = Build(jobCount: 25, pageSize: 10);

        var (result, failure) = await useCase.ContinueAsync(handle);

        Assert.Null(result);
        Assert.Equal(SearchJobsFailure.InvalidOrExpiredHandle, failure);
    }

    [Fact]
    public async Task A_handle_minted_by_another_server_is_refused()
    {
        var (mine, _) = Build(jobCount: 25, pageSize: 10);
        var (theirs, _) = Build(jobCount: 25, pageSize: 10, key: Enumerable.Range(90, 32).Select(i => (byte)i).ToArray());

        var foreign = await theirs.ExecuteAsync("engineer", null, null);

        var (result, failure) = await mine.ContinueAsync(foreign.NextPageHandle!);

        Assert.Null(result);
        Assert.Equal(SearchJobsFailure.InvalidOrExpiredHandle, failure);
    }

    [Fact]
    public async Task An_expired_handle_is_refused()
    {
        var clock = new MutableClock(DateTimeOffset.Parse("2026-08-27T12:00:00Z", null));
        var options = new TalentOptions { DefaultPageSize = 10, PaginationHandleTimeToLive = TimeSpan.FromMinutes(10) };
        var repository = new FakeJobRepository(25);
        using var codec = new SignedHandleCodec(Key, clock);
        var useCase = new SearchJobsUseCase(repository, codec, options);

        var first = await useCase.ExecuteAsync("engineer", null, null);
        clock.Advance(TimeSpan.FromMinutes(11));

        var (result, failure) = await useCase.ContinueAsync(first.NextPageHandle!);

        Assert.Null(result);
        Assert.Equal(SearchJobsFailure.InvalidOrExpiredHandle, failure);
    }

    [Fact]
    public async Task An_oversized_page_request_is_clamped_rather_than_rejected()
    {
        var (useCase, repository) = Build(jobCount: 500, pageSize: 10, maxPageSize: 50);

        await useCase.ExecuteAsync("engineer", null, null, pageSize: 10_000);

        Assert.Equal(50, repository.Calls[^1].Take);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    [InlineData(null)]
    public async Task A_missing_or_nonsensical_page_size_falls_back_to_the_default(int? requested)
    {
        var (useCase, repository) = Build(jobCount: 100, pageSize: 20);

        await useCase.ExecuteAsync("engineer", null, null, pageSize: requested);

        Assert.Equal(20, repository.Calls[^1].Take);
    }

    [Fact]
    public void Null_dependencies_are_rejected()
    {
        var options = new TalentOptions();
        var repository = new FakeJobRepository(0);
        using var codec = new SignedHandleCodec(Key);

        Assert.Throws<ArgumentNullException>(() => new SearchJobsUseCase(null!, codec, options));
        Assert.Throws<ArgumentNullException>(() => new SearchJobsUseCase(repository, null!, options));
        Assert.Throws<ArgumentNullException>(() => new SearchJobsUseCase(repository, codec, null!));
    }

    private static (SearchJobsUseCase UseCase, FakeJobRepository Repository) Build(
        int jobCount,
        int pageSize,
        int maxPageSize = 100,
        byte[]? key = null)
    {
        var repository = new FakeJobRepository(jobCount);
        var options = new TalentOptions { DefaultPageSize = pageSize, MaxPageSize = maxPageSize };
        var codec = new SignedHandleCodec(key ?? Key);

        return (new SearchJobsUseCase(repository, codec, options), repository);
    }

    private sealed class FakeJobRepository(int jobCount) : IJobRepository
    {
        private readonly Job[] all = Enumerable.Range(0, jobCount)
            .Select(i => new Job(
                Guid.Parse($"00000000-0000-0000-0000-{i:D12}"),
                $"Engineer {i:D3}",
                "Description",
                ["dotnet"],
                SeniorityLevel.Senior,
                new Location("Madrid", "ES"),
                WorkArrangement.Hybrid,
                SalaryRange.NotDisclosed))
            .ToArray();

        public List<JobSearchCriteria> Calls { get; } = [];

        public Task<Job?> FindByIdAsync(Guid id, CancellationToken cancellationToken = default) =>
            Task.FromResult(this.all.FirstOrDefault(j => j.Id == id));

        public Task<JobPage> SearchAsync(JobSearchCriteria criteria, CancellationToken cancellationToken = default)
        {
            this.Calls.Add(criteria);

            var page = this.all.Skip(criteria.Skip).Take(criteria.Take).ToArray();
            var consumed = criteria.Skip + page.Length;
            int? nextSkip = consumed < this.all.Length ? consumed : null;

            return Task.FromResult(new JobPage(page, this.all.Length, nextSkip));
        }
    }

    private sealed class MutableClock(DateTimeOffset now) : TimeProvider
    {
        private DateTimeOffset now = now;

        public override DateTimeOffset GetUtcNow() => this.now;

        public void Advance(TimeSpan by) => this.now = this.now.Add(by);
    }
}
