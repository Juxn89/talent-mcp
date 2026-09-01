namespace Talent.Infrastructure.DependencyInjection;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Talent.Application.Configuration;
using Talent.Application.Ports;
using Talent.Infrastructure.Handles;
using Talent.Infrastructure.Persistence;
using Talent.Mcp.Toolkit;

/// <summary>
/// Composition root for the adapters: EF Core, the repositories, the handle codec and the options the
/// use cases read.
/// <para>
/// Both hosts call this, and only the hosts do. Per ADR-0004 the tool library never references this
/// assembly, so this is the single place in the process that knows a database exists.
/// </para>
/// </summary>
public static class TalentInfrastructureServiceCollectionExtensions
{
    /// <summary>Configuration key holding the Postgres connection string.</summary>
    public const string ConnectionStringName = "Talent";

    /// <summary>
    /// Configuration key holding the handle signing key, base64-encoded.
    /// <para>
    /// There is deliberately no default. A signed handle is only as trustworthy as its key, and a
    /// fallback literal in source would make every deployment that forgot to configure one forgeable
    /// while looking perfectly healthy.
    /// </para>
    /// </summary>
    public const string SigningKeyPath = "Talent:HandleSigningKey";

    /// <summary>Registers the Application ports against their EF Core and toolkit implementations.</summary>
    /// <param name="services">The service collection.</param>
    /// <param name="configuration">Configuration to read the connection string, signing key and tunables from.</param>
    /// <returns>The same collection, for chaining.</returns>
    /// <exception cref="ArgumentNullException">An argument was <see langword="null"/>.</exception>
    /// <exception cref="InvalidOperationException">
    /// The connection string or signing key was missing, or the bound options were incoherent. Thrown at
    /// startup on purpose: every one of these produces a server that accepts requests and answers them
    /// wrongly, which is worse than one that refuses to boot.
    /// </exception>
    public static IServiceCollection AddTalentInfrastructure(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services.AddSingleton(BindOptions(configuration));

        var connectionString = ReadConnectionString(configuration);
        services.AddDbContext<TalentDbContext>(options => options.UseNpgsql(connectionString));

        services.AddScoped<IJobRepository, EfJobRepository>();
        services.AddScoped<ICandidateRepository, EfCandidateRepository>();

        // The codec is a singleton because it owns an HMAC instance; the adapter over it is a
        // singleton too and does NOT own it, so a scope ending cannot dispose the shared HMAC. That
        // ownership flag exists for exactly this registration.
        services.AddSingleton(_ => new HandleCodec(ReadSigningKey(configuration)));
        services.AddSingleton<IHandleCodec>(sp =>
            new SignedHandleCodec(sp.GetRequiredService<HandleCodec>(), ownsCodec: false));

        return services;
    }

    private static TalentOptions BindOptions(IConfiguration configuration)
    {
        // Bound to a plain object here rather than through IOptions<T>: the Application layer is not
        // allowed to reference Microsoft.Extensions.Options, so the composition root does the binding
        // and injects the value. Validated immediately, because a bad page size or weight set fails at
        // the first request otherwise, far from its cause.
        var options = configuration.GetSection(TalentOptions.SectionName).Get<TalentOptions>()
            ?? new TalentOptions();

        if (!options.TryValidate(out var error))
        {
            throw new InvalidOperationException(
                $"Configuration section '{TalentOptions.SectionName}' is invalid: {error}");
        }

        return options;
    }

    private static string ReadConnectionString(IConfiguration configuration)
    {
        var connectionString = configuration.GetConnectionString(ConnectionStringName);

        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw new InvalidOperationException(
                $"No connection string named '{ConnectionStringName}' was configured. Set "
                + $"ConnectionStrings__{ConnectionStringName} in the environment, or "
                + $"ConnectionStrings:{ConnectionStringName} in configuration. Both hosts read the "
                + "recruitment domain from Postgres — see ADR-0004.");
        }

        return connectionString;
    }

    private static byte[] ReadSigningKey(IConfiguration configuration)
    {
        var configured = configuration[SigningKeyPath];

        if (string.IsNullOrWhiteSpace(configured))
        {
            throw new InvalidOperationException(
                $"No handle signing key was configured at '{SigningKeyPath}'. Handles replace sessions "
                + "under the 2026-07-28 revision and are only trustworthy while the key is secret, so "
                + "there is no default. Generate one with: "
                + "openssl rand -base64 32");
        }

        byte[] key;
        try
        {
            key = Convert.FromBase64String(configured);
        }
        catch (FormatException ex)
        {
            throw new InvalidOperationException(
                $"The handle signing key at '{SigningKeyPath}' is not valid base64.", ex);
        }

        if (key.Length < HandleCodec.MinimumKeyLengthBytes)
        {
            throw new InvalidOperationException(
                $"The handle signing key at '{SigningKeyPath}' decodes to {key.Length} bytes; at least "
                + $"{HandleCodec.MinimumKeyLengthBytes} are required.");
        }

        return key;
    }
}
