namespace Talent.Infrastructure.Tests;

using Microsoft.EntityFrameworkCore;
using Talent.Domain.Entities;
using Talent.Domain.Enums;
using Talent.Domain.ValueObjects;
using Xunit;

/// <summary>
/// Does the EF mapping actually survive a round trip against real Postgres?
/// <para>
/// The question these answer is narrow and worth answering early: the entities have private
/// constructors and private setters so EF has somewhere to put values, and owned types flattened onto
/// the parent table. All of that either works or silently produces entities with default fields. Finding
/// that out here costs a container start; finding it out in the E2E suite costs an afternoon.
/// </para>
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class MappingRoundTripTests : IAsyncLifetime
{
    private readonly PostgresFixture postgres;

    public MappingRoundTripTests(PostgresFixture postgres) => this.postgres = postgres;

    public Task InitializeAsync() => this.postgres.ResetAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task A_job_round_trips_with_every_field_intact()
    {
        var id = Guid.Parse("11111111-1111-1111-1111-111111111111");
        var original = new Job(
            id,
            "Senior .NET Engineer",
            "Own the matching platform.",
            ["dotnet", "postgresql", "kubernetes"],
            SeniorityLevel.Senior,
            new Location("Madrid", "ES"),
            WorkArrangement.Hybrid,
            new SalaryRange(65_000, 85_000, "EUR"));

        await using (var write = this.postgres.CreateContext())
        {
            write.Jobs.Add(original);
            await write.SaveChangesAsync();
        }

        await using var read = this.postgres.CreateContext();
        var loaded = await read.Jobs.FindAsync(id);

        Assert.NotNull(loaded);
        Assert.Equal("Senior .NET Engineer", loaded.Title);
        Assert.Equal("Own the matching platform.", loaded.Description);
        Assert.Equal(["dotnet", "postgresql", "kubernetes"], loaded.RequiredSkillIds);
        Assert.Equal(SeniorityLevel.Senior, loaded.Seniority);
        Assert.Equal(WorkArrangement.Hybrid, loaded.Arrangement);

        // The owned types are the part most likely to come back as nulls or defaults.
        Assert.Equal("Madrid", loaded.Location.City);
        Assert.Equal("ES", loaded.Location.CountryCode);
        Assert.Equal(65_000, loaded.Salary.Minimum);
        Assert.Equal(85_000, loaded.Salary.Maximum);
        Assert.Equal("EUR", loaded.Salary.CurrencyCode);
    }

    [Fact]
    public async Task A_job_with_no_skills_and_undisclosed_salary_round_trips()
    {
        // The shape a scraped or partially-filled posting has. It must survive rather than be
        // assumed away, because the scorer has a branch for exactly this.
        var id = Guid.Parse("22222222-2222-2222-2222-222222222222");

        await using (var write = this.postgres.CreateContext())
        {
            write.Jobs.Add(new Job(
                id, "Generalist Engineer", string.Empty, [],
                SeniorityLevel.Unspecified, Location.Unknown,
                WorkArrangement.Unspecified, SalaryRange.NotDisclosed));

            await write.SaveChangesAsync();
        }

        await using var read = this.postgres.CreateContext();
        var loaded = await read.Jobs.FindAsync(id);

        Assert.NotNull(loaded);
        Assert.Empty(loaded.RequiredSkillIds);
        Assert.True(loaded.Location.IsUnknown);
        Assert.False(loaded.Salary.IsDisclosed);
        Assert.Equal(SeniorityLevel.Unspecified, loaded.Seniority);
    }

    [Fact]
    public async Task A_candidate_round_trips_including_its_rejection_state()
    {
        var id = Guid.Parse("33333333-3333-3333-3333-333333333333");
        var rejectedAt = DateTimeOffset.Parse("2026-08-01T09:30:00Z", null);

        var original = new Candidate(
            id, "Ana Herrera", ["dotnet", "postgresql"], 9,
            SeniorityLevel.Senior, new Location("Madrid", "ES"), willingToRelocate: true);

        original.Reject("Withdrew after the second interview.", rejectedAt);

        await using (var write = this.postgres.CreateContext())
        {
            write.Candidates.Add(original);
            await write.SaveChangesAsync();
        }

        await using var read = this.postgres.CreateContext();
        var loaded = await read.Candidates.FindAsync(id);

        Assert.NotNull(loaded);
        Assert.Equal(CandidateStatus.Rejected, loaded.Status);
        Assert.Equal("Withdrew after the second interview.", loaded.RejectionReason);
        Assert.Equal(rejectedAt, loaded.RejectedAt);
        Assert.True(loaded.IsValid());
    }

    [Fact]
    public async Task An_active_candidate_comes_back_with_no_rejection_fields_set()
    {
        var id = Guid.Parse("44444444-4444-4444-4444-444444444444");

        await using (var write = this.postgres.CreateContext())
        {
            write.Candidates.Add(new Candidate(
                id, "Bruno Silva", ["dotnet"], 7,
                SeniorityLevel.Senior, new Location("Madrid", "ES"), willingToRelocate: false));

            await write.SaveChangesAsync();
        }

        await using var read = this.postgres.CreateContext();
        var loaded = await read.Candidates.FindAsync(id);

        Assert.NotNull(loaded);
        Assert.Equal(CandidateStatus.Active, loaded.Status);
        Assert.Null(loaded.RejectionReason);
        Assert.Null(loaded.RejectedAt);

        // Half-set rejection state is what IsValid guards; assert it holds after materialization and
        // not only after construction.
        Assert.True(loaded.IsValid());
    }

    [Fact]
    public async Task Enums_are_stored_as_text_not_as_ordinals()
    {
        // Ordinals make the table unreadable and silently reinterpret every row if a member is ever
        // inserted mid-enum. A1 reads this schema, so a wrong value would surface in another repo.
        var id = Guid.Parse("55555555-5555-5555-5555-555555555555");

        await using (var write = this.postgres.CreateContext())
        {
            write.Jobs.Add(new Job(
                id, "Staff Engineer", "Lead.", ["go"], SeniorityLevel.Staff,
                new Location("Berlin", "DE"), WorkArrangement.OnSite, SalaryRange.NotDisclosed));

            await write.SaveChangesAsync();
        }

        await using var read = this.postgres.CreateContext();
        var connection = read.Database.GetDbConnection();
        await connection.OpenAsync();

        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT seniority, arrangement FROM jobs WHERE id = @id";

        var parameter = command.CreateParameter();
        parameter.ParameterName = "id";
        parameter.Value = id;
        command.Parameters.Add(parameter);

        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.Equal("Staff", reader.GetString(0));
        Assert.Equal("OnSite", reader.GetString(1));
    }

    [Fact]
    public async Task Skill_ids_are_stored_as_a_postgres_array()
    {
        var id = Guid.Parse("66666666-6666-6666-6666-666666666666");

        await using (var write = this.postgres.CreateContext())
        {
            write.Jobs.Add(new Job(
                id, "Data Engineer", "Model the funnel.", ["dbt", "postgresql", "spark"],
                SeniorityLevel.Senior, new Location("Lisbon", "PT"),
                WorkArrangement.Remote, SalaryRange.NotDisclosed));

            await write.SaveChangesAsync();
        }

        await using var read = this.postgres.CreateContext();
        var connection = read.Database.GetDbConnection();
        await connection.OpenAsync();

        await using var command = connection.CreateCommand();

        // Asserted through Postgres' own array operator: if the column were a delimited string this
        // query would fail rather than quietly matching a substring.
        command.CommandText =
            "SELECT array_length(required_skill_ids, 1) FROM jobs WHERE required_skill_ids @> ARRAY['dbt','spark']::text[]";

        var count = await command.ExecuteScalarAsync();

        Assert.Equal(3, Convert.ToInt32(count, System.Globalization.CultureInfo.InvariantCulture));
    }
}
