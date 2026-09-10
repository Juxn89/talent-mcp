namespace Talent.Mcp.Bench;

using BenchmarkDotNet.Attributes;
using Talent.Domain.Entities;
using Talent.Domain.Skills;

/// <summary>
/// Measures <see cref="SkillNormalizer.Extract(string?)"/>, the deterministic taxonomy match that
/// backs the <c>extract_skills</c> tool.
/// <para>
/// This is the one domain function whose cost is not obviously flat. Per call it lowercases the
/// whole input and allocates a <c>bool[]</c> the same length as it, then walks every alias in
/// <see cref="SkillTaxonomy.AliasesLongestFirst"/> running <c>IndexOf</c> repeatedly across the
/// entire haystack — so the work is proportional to alias count times text length, and a long CV
/// pays for every alias in the taxonomy whether or not it appears.
/// </para>
/// <para>
/// The parameter is text length precisely because that is the axis the shape predicts. Measuring
/// only one CV size would report a number without reporting whether it scales.
/// </para>
/// </summary>
[MemoryDiagnoser]
public class SkillExtractionBenchmarks
{
    private const string Paragraph =
        "Senior backend engineer with deep experience in C# and ASP.NET Core, building distributed "
        + "services on PostgreSQL and Redis. Comfortable with Docker, Kubernetes and Terraform, and "
        + "has shipped gRPC and GraphQL APIs in production. Also works with TypeScript and React on "
        + "the front end, and has used Python for data tooling. Familiar with Kafka, RabbitMQ and "
        + "event-driven design, plus observability with OpenTelemetry and Prometheus. ";

    /// <summary>
    /// Roughly: a two-line summary, a realistic one-page CV, and a ten-page CV. Named rather than
    /// numeric so the report reads as the scenario, not as a multiplier.
    /// </summary>
    [Params("short", "one-page", "ten-page")]
    public string Size { get; set; } = "one-page";

    private string text = string.Empty;

    [GlobalSetup]
    public void Setup() => this.text = this.Size switch
    {
        "short" => "C# and PostgreSQL developer, some React.",
        "one-page" => string.Concat(Enumerable.Repeat(Paragraph, 2)),
        "ten-page" => string.Concat(Enumerable.Repeat(Paragraph, 20)),
        _ => throw new ArgumentOutOfRangeException(nameof(this.Size)),
    };

    [Benchmark]
    public IReadOnlyList<Skill> Extract() => SkillNormalizer.Extract(this.text);
}
