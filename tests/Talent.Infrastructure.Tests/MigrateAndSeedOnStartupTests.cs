namespace Talent.Infrastructure.Tests;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Talent.Infrastructure.DependencyInjection;
using Testcontainers.PostgreSql;
using Xunit;

/// <summary>
/// Tests the startup path that gives a freshly composed stack its schema and its data.
/// <para>
/// This class owns its own container rather than using <see cref="PostgresFixture"/>, because that
/// fixture migrates in <c>InitializeAsync</c> and the thing under test here is precisely what happens
/// against a database where nothing exists yet. Reusing an already-migrated one would test the
/// idempotency and skip the bug.
/// </para>
/// <para>
/// The bug: <c>TalentSeeder</c> shipped idempotent and documented for exactly this use — its own
/// summary says "so <c>docker compose up</c> on an existing volume does not fail or duplicate" — but
/// no production code ever called it. Only the test fixtures did, so a clone that followed the README
/// got a server with no domain tables and a <c>search_jobs</c> that returned nothing.
/// </para>
/// </summary>
public sealed class MigrateAndSeedOnStartupTests : IAsyncLifetime
{
    private readonly PostgreSqlContainer container = new PostgreSqlBuilder("postgres:18.6-alpine")
        .WithDatabase("talent_startup_tests")
        .WithUsername("talent")
        .WithPassword("talent")
        .Build();

    public async Task InitializeAsync() => await this.container.StartAsync();

    public async Task DisposeAsync() => await this.container.DisposeAsync();

    /// <summary>
    /// Creates an empty database on the running container and returns its connection string.
    /// <para>
    /// One per test, not one per class. These three tests assert mutually exclusive states of the
    /// same database — schema absent, freshly seeded, seeded twice — so sharing one would make them
    /// depend on an execution order xUnit does not guarantee. A database is cheap; a test that only
    /// passes in the order it was written is not.
    /// </para>
    /// </summary>
    private async Task<string> CreateEmptyDatabaseAsync(string name)
    {
        await using var maintenance = new NpgsqlConnection(this.container.GetConnectionString());
        await maintenance.OpenAsync();
        await using (var create = new NpgsqlCommand($"CREATE DATABASE \"{name}\";", maintenance))
        {
            await create.ExecuteNonQueryAsync();
        }

        return new NpgsqlConnectionStringBuilder(this.container.GetConnectionString())
        {
            Database = name,
        }.ConnectionString;
    }

    /// <summary>
    /// Composes the real registrations, so this exercises the same path a host does rather than a
    /// hand-built context.
    /// </summary>
    private static ServiceProvider BuildHost(string connectionString, bool migrateAndSeedOnStartup)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                [$"ConnectionStrings:{TalentInfrastructureServiceCollectionExtensions.ConnectionStringName}"] =
                    connectionString,
                // Any 32-byte key; this test never mints a handle.
                [TalentInfrastructureServiceCollectionExtensions.SigningKeyPath] =
                    Convert.ToBase64String(new byte[32]),
                [TalentInfrastructureServiceCollectionExtensions.MigrateAndSeedOnStartupPath] =
                    migrateAndSeedOnStartup ? "true" : "false",
            })
            .Build();

        var services = new ServiceCollection();
        services.AddTalentInfrastructure(configuration);
        services.AddSingleton<IConfiguration>(configuration);

        return services.BuildServiceProvider();
    }

    private static IConfiguration ConfigurationOf(ServiceProvider provider) =>
        provider.GetRequiredService<IConfiguration>();

    [Fact]
    public async Task Does_nothing_when_the_host_has_not_opted_in()
    {
        // The production default, and the one worth guarding hardest: a regression here would have
        // every replica of the HTTP host racing to migrate on boot, and would put a schema change on
        // the critical path of a process nobody asked to migrate anything.
        var connectionString = await this.CreateEmptyDatabaseAsync("opt_out");
        await using var provider = BuildHost(connectionString, migrateAndSeedOnStartup: false);

        var result = await provider.MigrateAndSeedAsync(ConfigurationOf(provider));

        Assert.False(result.Ran);
        Assert.Equal(0, result.Jobs);
        Assert.Equal(0, result.Candidates);

        // Not merely "it reported nothing": the schema must genuinely not exist. Querying a table
        // that was never created throws, which is the assertion.
        await using var scope = provider.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<TalentDbContext>();
        await Assert.ThrowsAnyAsync<Exception>(() => context.Jobs.CountAsync());
    }

    [Fact]
    public async Task Creates_the_schema_and_seeds_when_opted_in()
    {
        var connectionString = await this.CreateEmptyDatabaseAsync("opt_in");
        await using var provider = BuildHost(connectionString, migrateAndSeedOnStartup: true);

        var result = await provider.MigrateAndSeedAsync(ConfigurationOf(provider));

        Assert.True(result.Ran);
        Assert.True(result.Jobs > 0, "the seed data should contain jobs");
        Assert.True(result.Candidates > 0, "the seed data should contain candidates");

        // The point of the whole exercise: search_jobs has something to find.
        await using var scope = provider.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<TalentDbContext>();
        Assert.Equal(result.Jobs, await context.Jobs.CountAsync());
        Assert.Equal(result.Candidates, await context.Candidates.CountAsync());
    }

    [Fact]
    public async Task Is_idempotent_across_restarts()
    {
        // `docker compose up` on an existing volume, or any ordinary restart. TalentSeeder's summary
        // promises this; nothing had ever exercised the promise through the startup path.
        var connectionString = await this.CreateEmptyDatabaseAsync("restart");
        await using var first = BuildHost(connectionString, migrateAndSeedOnStartup: true);
        await first.MigrateAndSeedAsync(ConfigurationOf(first));

        await using var second = BuildHost(connectionString, migrateAndSeedOnStartup: true);
        var restart = await second.MigrateAndSeedAsync(ConfigurationOf(second));

        Assert.True(restart.Ran);
        Assert.Equal(0, restart.Jobs);
        Assert.Equal(0, restart.Candidates);
    }
}
