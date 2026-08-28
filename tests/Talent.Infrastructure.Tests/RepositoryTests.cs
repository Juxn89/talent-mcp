namespace Talent.Infrastructure.Tests;

using Microsoft.EntityFrameworkCore;
using Talent.Application.Ports;
using Talent.Domain.Entities;
using Talent.Domain.Enums;
using Talent.Domain.ValueObjects;
using Talent.Infrastructure.Persistence;
using Talent.Infrastructure.Seeding;
using Xunit;

/// <summary>
/// The repository adapters against real Postgres — filters, ordering, and the destructive write path.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class RepositoryTests : IAsyncLifetime
{
    private static readonly int SeededJobCount = SeedData.CreateJobs().Count;
    private static readonly int SeededCandidateCount = SeedData.CreateCandidates().Count;

    private readonly PostgresFixture postgres;

    public RepositoryTests(PostgresFixture postgres) => this.postgres = postgres;

    public async Task InitializeAsync()
    {
        await this.postgres.ResetAsync();

        await using var context = this.postgres.CreateContext();
        await TalentSeeder.SeedWithoutMigratingAsync(context);
    }

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task Seeding_is_idempotent()
    {
        // docker compose up on an existing volume must not fail or duplicate.
        await using var context = this.postgres.CreateContext();

        var (jobs, candidates) = await TalentSeeder.SeedWithoutMigratingAsync(context);

        Assert.Equal(0, jobs);
        Assert.Equal(0, candidates);
        Assert.Equal(SeededJobCount, context.Jobs.Count());
        Assert.Equal(SeededCandidateCount, context.Candidates.Count());
    }

    [Fact]
    public async Task Seeding_converges_after_a_partial_insert()
    {
        // Checked per id rather than "is the table empty", so an interrupted first run or a dataset
        // that grew since the last deploy fills the gap instead of being skipped entirely.
        await using (var delete = this.postgres.CreateContext())
        {
            await delete.Database.ExecuteSqlRawAsync(
                "DELETE FROM jobs WHERE id = '{0}'".Replace("{0}", SeedData.CreateJobs()[0].Id.ToString(), StringComparison.Ordinal));
        }

        await using var context = this.postgres.CreateContext();
        var (jobs, _) = await TalentSeeder.SeedWithoutMigratingAsync(context);

        Assert.Equal(1, jobs);
        Assert.Equal(SeededJobCount, context.Jobs.Count());
    }

    [Fact]
    public async Task Full_text_query_matches_title_and_description_case_insensitively()
    {
        var repository = this.JobRepository();

        var byTitle = await repository.SearchAsync(Criteria(query: "SENIOR .NET"));
        var byDescription = await repository.SearchAsync(Criteria(query: "elasticsearch at scale"));

        Assert.Contains(byTitle.Jobs, j => j.Title.Contains("Senior .NET", StringComparison.Ordinal));
        Assert.Contains(byDescription.Jobs, j => j.Title.Contains("Search", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Like_wildcards_in_a_query_are_escaped()
    {
        // Without escaping, '%' would match every row — a search box silently becoming a full scan.
        var repository = this.JobRepository();

        var result = await repository.SearchAsync(Criteria(query: "%"));

        Assert.Equal(0, result.TotalMatches);
    }

    [Fact]
    public async Task Skill_filter_requires_every_requested_skill()
    {
        var repository = this.JobRepository();

        var both = await repository.SearchAsync(Criteria(skills: ["dotnet", "kafka"]));
        var one = await repository.SearchAsync(Criteria(skills: ["dotnet"]));

        Assert.All(both.Jobs, j =>
        {
            Assert.Contains("dotnet", j.RequiredSkillIds);
            Assert.Contains("kafka", j.RequiredSkillIds);
        });

        Assert.True(one.TotalMatches > both.TotalMatches);
    }

    [Fact]
    public async Task Country_and_arrangement_filters_apply()
    {
        var repository = this.JobRepository();

        var spanish = await repository.SearchAsync(Criteria(countryCode: "es"));
        var remote = await repository.SearchAsync(Criteria(arrangement: WorkArrangement.Remote));

        Assert.NotEmpty(spanish.Jobs);
        Assert.All(spanish.Jobs, j => Assert.Equal("ES", j.Location.CountryCode));

        Assert.NotEmpty(remote.Jobs);
        Assert.All(remote.Jobs, j => Assert.Equal(WorkArrangement.Remote, j.Arrangement));
    }

    [Fact]
    public async Task Paging_walks_every_row_exactly_once()
    {
        // The property that matters for handle-based pagination: without a total order, two rows with
        // equal titles can swap between the queries that make up a page boundary, and a caller skips
        // one while seeing the other twice. The Id tiebreaker in the repository is what prevents it.
        var repository = this.JobRepository();
        var seen = new List<Guid>();
        var skip = 0;

        while (true)
        {
            var page = await repository.SearchAsync(Criteria(skip: skip, take: 3));
            seen.AddRange(page.Jobs.Select(j => j.Id));

            if (page.NextSkip is not { } next)
            {
                break;
            }

            skip = next;
        }

        Assert.Equal(SeededJobCount, seen.Count);
        Assert.Equal(SeededJobCount, seen.Distinct().Count());
    }

    [Fact]
    public async Task Ordering_is_identical_across_repeated_queries()
    {
        var repository = this.JobRepository();

        var first = await repository.SearchAsync(Criteria(take: 50));
        var second = await repository.SearchAsync(Criteria(take: 50));

        Assert.Equal(first.Jobs.Select(j => j.Id), second.Jobs.Select(j => j.Id));
    }

    [Fact]
    public async Task Last_page_reports_no_continuation()
    {
        var repository = this.JobRepository();

        var page = await repository.SearchAsync(Criteria(take: 100));

        Assert.Null(page.NextSkip);
        Assert.Equal(SeededJobCount, page.Jobs.Count);
    }

    [Fact]
    public async Task Finding_by_id_returns_null_for_an_unknown_job()
    {
        var repository = this.JobRepository();

        Assert.Null(await repository.FindByIdAsync(Guid.Parse("99999999-9999-9999-9999-999999999999")));
    }

    [Fact]
    public async Task Candidates_are_fetched_in_bulk_skipping_unknown_ids()
    {
        await using var context = this.postgres.CreateContext();
        var repository = new EfCandidateRepository(context);
        var known = SeedData.CreateCandidates()[0].Id;
        var unknown = Guid.Parse("99999999-9999-9999-9999-999999999999");

        var found = await repository.FindByIdsAsync([known, unknown, known]);

        Assert.Single(found);
        Assert.Equal(known, found[0].Id);
    }

    [Fact]
    public async Task Bulk_fetch_of_an_empty_list_does_not_query()
    {
        await using var context = this.postgres.CreateContext();

        Assert.Empty(await new EfCandidateRepository(context).FindByIdsAsync([]));
    }

    [Fact]
    public async Task Rejecting_persists_the_reason_and_a_timestamp()
    {
        var clock = new FixedClock(DateTimeOffset.Parse("2026-08-28T10:00:00Z", null));
        var target = SeedData.CreateCandidates().First(c => c.Status == CandidateStatus.Active).Id;

        await using (var context = this.postgres.CreateContext())
        {
            var repository = new EfCandidateRepository(context, clock);

            Assert.True(await repository.RejectAsync(target, "Insufficient Kubernetes experience."));
        }

        await using var read = this.postgres.CreateContext();
        var candidate = await read.Candidates.FindAsync(target);

        Assert.NotNull(candidate);
        Assert.Equal(CandidateStatus.Rejected, candidate.Status);
        Assert.Equal("Insufficient Kubernetes experience.", candidate.RejectionReason);
        Assert.Equal(clock.GetUtcNow(), candidate.RejectedAt);
    }

    [Fact]
    public async Task Rejecting_twice_is_idempotent_and_keeps_the_original_reason()
    {
        // The destructive tool runs behind an MRTR confirmation. A client that retries after a dropped
        // response must not get an error for work that already succeeded, nor have the original reason
        // and timestamp overwritten by the retry.
        var first = new FixedClock(DateTimeOffset.Parse("2026-08-28T10:00:00Z", null));
        var second = new FixedClock(DateTimeOffset.Parse("2026-08-29T15:00:00Z", null));
        var target = SeedData.CreateCandidates().First(c => c.Status == CandidateStatus.Active).Id;

        await using (var context = this.postgres.CreateContext())
        {
            await new EfCandidateRepository(context, first).RejectAsync(target, "Original reason recorded.");
        }

        await using (var context = this.postgres.CreateContext())
        {
            Assert.True(await new EfCandidateRepository(context, second).RejectAsync(target, "A different reason."));
        }

        await using var read = this.postgres.CreateContext();
        var candidate = await read.Candidates.FindAsync(target);

        Assert.Equal("Original reason recorded.", candidate!.RejectionReason);
        Assert.Equal(first.GetUtcNow(), candidate.RejectedAt);
    }

    [Fact]
    public async Task Rejecting_an_unknown_candidate_reports_not_found()
    {
        await using var context = this.postgres.CreateContext();
        var repository = new EfCandidateRepository(context);

        Assert.False(await repository.RejectAsync(
            Guid.Parse("99999999-9999-9999-9999-999999999999"), "Some adequate reason here."));
    }

    [Fact]
    public async Task The_seeded_dataset_contains_the_cases_that_make_matching_interesting()
    {
        // The seeds exist to be evaluated against, so their shape is part of the contract with A1.
        // Asserted rather than assumed, because a well-meaning edit could flatten the dataset into
        // uniform rows and make a bad matcher look good.
        await using var context = this.postgres.CreateContext();

        Assert.Contains(SeedData.CreateJobs(), j => j.RequiredSkillIds.Count == 0);
        Assert.Contains(SeedData.CreateJobs(), j => !j.Salary.IsDisclosed);
        Assert.Contains(SeedData.CreateJobs(), j => j.Location.IsUnknown);
        Assert.Contains(SeedData.CreateJobs(), j => j.Arrangement == WorkArrangement.Remote);

        Assert.Contains(SeedData.CreateCandidates(), c => c.Seniority == SeniorityLevel.Unspecified);
        Assert.Contains(SeedData.CreateCandidates(), c => c.Location.IsUnknown);
        Assert.Contains(SeedData.CreateCandidates(), c => c.WillingToRelocate);
        Assert.Contains(SeedData.CreateCandidates(), c => c.Status == CandidateStatus.Rejected);

        Assert.All(SeedData.CreateJobs(), j => Assert.True(j.IsValid()));
        Assert.All(SeedData.CreateCandidates(), c => Assert.True(c.IsValid()));
    }

    private IJobRepository JobRepository() => new EfJobRepository(this.postgres.CreateContext());

    private static JobSearchCriteria Criteria(
        string query = "",
        string[]? skills = null,
        string countryCode = "",
        WorkArrangement arrangement = WorkArrangement.Unspecified,
        int skip = 0,
        int take = 20) =>
        new(query, skills ?? [], countryCode, arrangement, skip, take);

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
