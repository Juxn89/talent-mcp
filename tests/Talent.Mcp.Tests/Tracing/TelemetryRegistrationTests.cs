namespace Talent.Mcp.Tests.Tracing;

using System.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using OpenTelemetry.Trace;
using Talent.Mcp.Toolkit.Tracing;
using Xunit;

/// <summary>
/// Which sources the hosts' tracer provider listens to. A test-side <see cref="ActivityListener"/> would
/// enable any source on its own, so these tests build the provider <c>AddTalentTelemetry</c> registers and
/// ask it, with no other listener involved.
/// <para>
/// Found 20 Oct 2026 (#13): the README promised a trace spanning client → server → Postgres, but only
/// the tool source was registered, so every trace in Jaeger stopped at the tool span.
/// </para>
/// </summary>
public sealed class TelemetryRegistrationTests
{
    [Theory]
    [InlineData(TalentActivitySource.Name)]
    [InlineData(ExternalActivitySources.Npgsql)]
    public void The_host_tracer_provider_records_spans_from(string sourceName)
    {
        using var services = new ServiceCollection()
            .AddTalentTelemetry(new ConfigurationBuilder().Build(), serviceName: "talent-mcp-test")
            .BuildServiceProvider();
        using var tracerProvider = services.GetRequiredService<TracerProvider>();

        // A source of the same name as the library's: the provider subscribes by name, so this stands in
        // for Npgsql's own without opening a connection.
        using var source = new ActivitySource(sourceName);
        using var activity = source.StartActivity("probe");

        Assert.NotNull(activity);
        Assert.True(activity.Recorded);
    }
}
