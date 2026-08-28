namespace Talent.Infrastructure.Persistence;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

/// <summary>
/// Design-time factory so <c>dotnet ef</c> can build the model without starting the application.
/// <para>
/// Needed because <c>Talent.Infrastructure</c> is a library with no host: without this, EF tooling
/// looks for a startup project and fails. It also means migrations can be generated with no database
/// running and no host configuration — the connection string below is never used to connect during
/// <c>migrations add</c>, only to select the provider so the generated SQL is Postgres-shaped.
/// </para>
/// </summary>
public sealed class TalentDbContextFactory : IDesignTimeDbContextFactory<TalentDbContext>
{
    /// <summary>
    /// Environment variable a caller may set to point the tooling at a real database, for the commands
    /// that do connect (<c>database update</c>, <c>dbcontext scaffold</c>).
    /// </summary>
    public const string ConnectionStringVariable = "DATABASE_CONNECTION_STRING";

    private const string DesignTimeFallback =
        "Host=localhost;Port=5432;Database=talent;Username=talent;Password=talent";

    /// <inheritdoc />
    public TalentDbContext CreateDbContext(string[] args)
    {
        var connectionString =
            Environment.GetEnvironmentVariable(ConnectionStringVariable) ?? DesignTimeFallback;

        var options = new DbContextOptionsBuilder<TalentDbContext>()
            .UseNpgsql(connectionString)
            .Options;

        return new TalentDbContext(options);
    }
}
