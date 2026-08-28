namespace Talent.Infrastructure.Tests;

using Microsoft.EntityFrameworkCore;
using Talent.Infrastructure;
using Testcontainers.PostgreSql;
using Xunit;

/// <summary>
/// A real PostgreSQL instance for the duration of the test class, started by Testcontainers.
/// <para>
/// Real Postgres and not an in-memory provider, on purpose. Everything worth testing at this level
/// only exists in the real provider: <c>text[]</c> columns, <c>ILIKE</c>, array containment, owned-type
/// flattening, and whether the enum-to-text conversions survive a round trip. An in-memory provider
/// would pass all of these tests and prove nothing about the schema that actually ships.
/// </para>
/// <para>
/// Pinned to the same image tag as <c>deploy/compose.yaml</c>. A test suite that passes on a different
/// major version than production runs is a test suite that will surprise someone.
/// </para>
/// </summary>
public sealed class PostgresFixture : IAsyncLifetime
{
    private readonly PostgreSqlContainer container = new PostgreSqlBuilder("postgres:18.6-alpine")
        .WithDatabase("talent_tests")
        .WithUsername("talent")
        .WithPassword("talent")
        .Build();

    /// <summary>Connection string for the started container.</summary>
    public string ConnectionString => this.container.GetConnectionString();

    /// <summary>Starts the container and applies the migrations.</summary>
    public async Task InitializeAsync()
    {
        await this.container.StartAsync();

        // Migrated once here rather than per test. This also means the migrations themselves are under
        // test: a migration that does not apply cleanly fails every test in the class, loudly, instead
        // of being discovered at deploy time.
        await using var context = this.CreateContext();
        await context.Database.MigrateAsync();
    }

    /// <summary>Stops and removes the container.</summary>
    public async Task DisposeAsync() => await this.container.DisposeAsync();

    /// <summary>Creates a fresh context against the container.</summary>
    /// <returns>A new context; the caller disposes it.</returns>
    public TalentDbContext CreateContext() =>
        new(new DbContextOptionsBuilder<TalentDbContext>()
            .UseNpgsql(this.ConnectionString)
            .Options);

    /// <summary>
    /// Empties both tables so a test starts from a known state.
    /// <para>
    /// Truncate rather than recreating the database per test: the container start dominates the runtime,
    /// and a per-test database would multiply it by the test count for no isolation benefit these tests
    /// need.
    /// </para>
    /// </summary>
    /// <returns>A task that completes when the tables are empty.</returns>
    public async Task ResetAsync()
    {
        await using var context = this.CreateContext();

        await context.Database.ExecuteSqlRawAsync("TRUNCATE TABLE jobs, candidates");
    }
}

/// <summary>Marks a class as sharing one PostgreSQL container.</summary>
[CollectionDefinition(Name)]
public sealed class PostgresCollection : ICollectionFixture<PostgresFixture>
{
    /// <summary>The collection name.</summary>
    public const string Name = "postgres";
}
