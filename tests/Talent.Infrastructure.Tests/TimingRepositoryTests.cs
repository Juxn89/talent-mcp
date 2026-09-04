namespace Talent.Infrastructure.Tests;

using Talent.Infrastructure.Persistence;
using Talent.Infrastructure.Seeding;
using Talent.Mcp.Toolkit.Tracing;
using Xunit;

/// <summary>
/// <c>TimingJobRepository</c>/<c>TimingCandidateRepository</c> against real Postgres — the
/// <c>db.query_time</c> span tag's actual signal, not a fake repository's zero-cost call.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class TimingRepositoryTests : IAsyncLifetime
{
    private readonly PostgresFixture postgres;

    public TimingRepositoryTests(PostgresFixture postgres) => this.postgres = postgres;

    public async Task InitializeAsync()
    {
        await this.postgres.ResetAsync();

        await using var context = this.postgres.CreateContext();
        await TalentSeeder.SeedWithoutMigratingAsync(context);
    }

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task A_job_search_reports_elapsed_time_to_the_ambient_scope()
    {
        var repository = new TimingJobRepository(new EfJobRepository(this.postgres.CreateContext()));
        var scope = new ToolTelemetryScope();

        using (ToolTelemetryScope.Push(scope))
        {
            await repository.SearchAsync(new(string.Empty, [], string.Empty, default, 0, 20));
        }

        Assert.True(scope.TotalDbQueryTime > TimeSpan.Zero);
    }

    [Fact]
    public async Task A_candidate_lookup_reports_elapsed_time_to_the_ambient_scope()
    {
        var repository = new TimingCandidateRepository(new EfCandidateRepository(this.postgres.CreateContext()));
        var scope = new ToolTelemetryScope();

        using (ToolTelemetryScope.Push(scope))
        {
            await repository.FindByIdAsync(SeedData.CreateCandidates()[0].Id);
        }

        Assert.True(scope.TotalDbQueryTime > TimeSpan.Zero);
    }

    [Fact]
    public async Task Multiple_calls_accumulate_rather_than_overwrite()
    {
        var repository = new TimingJobRepository(new EfJobRepository(this.postgres.CreateContext()));
        var scope = new ToolTelemetryScope();

        using (ToolTelemetryScope.Push(scope))
        {
            await repository.SearchAsync(new(string.Empty, [], string.Empty, default, 0, 5));
            var afterFirst = scope.TotalDbQueryTime;

            await repository.SearchAsync(new(string.Empty, [], string.Empty, default, 5, 5));

            Assert.True(scope.TotalDbQueryTime >= afterFirst);
        }
    }

    [Fact]
    public async Task Outside_a_scope_the_call_still_succeeds()
    {
        // Talent.Infrastructure.Tests calls repositories directly, with no tool call and therefore no
        // ambient scope — RecordDbQueryTime must be a safe no-op, not a NullReferenceException.
        Assert.Null(ToolTelemetryScope.Current);

        var repository = new TimingJobRepository(new EfJobRepository(this.postgres.CreateContext()));

        var page = await repository.SearchAsync(new(string.Empty, [], string.Empty, default, 0, 5));

        Assert.NotNull(page);
    }
}
