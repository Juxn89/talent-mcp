namespace Talent.Mcp.Toolkit.Tracing;

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using OpenTelemetry.Logs;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

/// <summary>
/// Wires the OpenTelemetry SDK the same way for both hosts.
/// <para>
/// Deliberately in <c>Talent.Mcp.Toolkit</c>, not either host: <see cref="TalentActivitySource"/> and
/// <see cref="TalentMeter"/> are what a tool-execution span/metric actually comes from, so the code
/// that tells <c>TracerProviderBuilder</c>/<c>MeterProviderBuilder</c> to listen to them belongs next
/// to them — otherwise the two hosts would each carry their own copy of "add this exact source name",
/// with no compiler check that they stay identical.
/// </para>
/// <para>
/// Log export is a separate method (<see cref="AddTalentOtlpLogging"/>), called by the HTTP host only.
/// The stdio host keeps its stderr console logger (pitfall #11: <c>stdout</c> carries JSON-RPC) and
/// never calls it.
/// </para>
/// </summary>
public static class TalentTelemetryServiceCollectionExtensions
{
    /// <summary>Configuration key for the OTLP endpoint the Collector listens on.</summary>
    public const string OtelEndpointConfigKey = "Talent:Otel:Endpoint";

    /// <summary>Adds tracing (<see cref="TalentActivitySource"/>) and metrics (<see cref="TalentMeter"/>), OTLP-exported.</summary>
    /// <param name="services">The service collection.</param>
    /// <param name="configuration">Read for <see cref="OtelEndpointConfigKey"/>.</param>
    /// <param name="serviceName">
    /// The <c>service.name</c> resource attribute. Differs per host (<c>talent-mcp-http</c> vs
    /// <c>talent-mcp-stdio</c>) so Jaeger/Grafana can tell which process produced a span.
    /// </param>
    /// <param name="configureTracing">Extra tracing configuration — the HTTP host uses this to add ASP.NET Core instrumentation.</param>
    /// <returns>The same collection, for chaining.</returns>
    /// <exception cref="ArgumentNullException">A required argument was <see langword="null"/>.</exception>
    public static IServiceCollection AddTalentTelemetry(
        this IServiceCollection services,
        IConfiguration configuration,
        string serviceName,
        Action<TracerProviderBuilder>? configureTracing = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentException.ThrowIfNullOrWhiteSpace(serviceName);

        var endpoint = ReadEndpoint(configuration);

        services
            .AddOpenTelemetry()
            .ConfigureResource(resource => resource.AddService(serviceName))
            .WithTracing(tracing =>
            {
                tracing.AddSource(TalentActivitySource.Name);

                if (endpoint is not null)
                {
                    tracing.AddOtlpExporter(otlp => otlp.Endpoint = endpoint);
                }

                configureTracing?.Invoke(tracing);
            })
            .WithMetrics(metrics =>
            {
                metrics.AddMeter(TalentMeter.Name);

                if (endpoint is not null)
                {
                    metrics.AddOtlpExporter(otlp => otlp.Endpoint = endpoint);
                }
            });

        return services;
    }

    /// <summary>
    /// Adds an OTLP log exporter (→ Collector → Loki). HTTP-host-only — see the type doc for why.
    /// </summary>
    /// <param name="logging">The logging builder.</param>
    /// <param name="configuration">Read for <see cref="OtelEndpointConfigKey"/>.</param>
    /// <param name="serviceName">The <c>service.name</c> resource attribute.</param>
    /// <returns>The same builder, for chaining.</returns>
    public static ILoggingBuilder AddTalentOtlpLogging(
        this ILoggingBuilder logging,
        IConfiguration configuration,
        string serviceName)
    {
        ArgumentNullException.ThrowIfNull(logging);
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentException.ThrowIfNullOrWhiteSpace(serviceName);

        var endpoint = ReadEndpoint(configuration);
        if (endpoint is null)
        {
            return logging;
        }

        logging.AddOpenTelemetry(options =>
        {
            options.SetResourceBuilder(ResourceBuilder.CreateDefault().AddService(serviceName));
            options.AddOtlpExporter(otlp => otlp.Endpoint = endpoint);
        });

        return logging;
    }

    private static Uri? ReadEndpoint(IConfiguration configuration)
    {
        var raw = configuration[OtelEndpointConfigKey];
        return string.IsNullOrWhiteSpace(raw) ? null : new Uri(raw);
    }
}
