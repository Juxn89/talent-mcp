namespace Talent.Mcp.Tests.Tools;

using Talent.Mcp.Tools.Constants;
using Xunit;

/// <summary>
/// The two tools that call no LLM and, between them, need no database to be interesting:
/// <c>extract_skills</c> and <c>score_candidate_fit</c>. Their determinism is what lets these
/// assertions be exact rather than approximate.
/// </summary>
public sealed class DeterministicToolTests
{
    [Fact]
    public async Task Skills_are_extracted_from_free_text()
    {
        await using var harness = await ToolHarness.StartAsync();

        var result = await harness.CallAsync(
            Mcp.ToolNames.ExtractSkills,
            new Dictionary<string, object?>
            {
                ["text"] = "Eight years building services in C# and PostgreSQL, plus some Rust.",
            });

        var payload = ToolHarness.StructuredOf(result);
        var ids = payload.GetProperty("skills").EnumerateArray()
            .Select(s => s.GetProperty("id").GetString())
            .ToArray();

        Assert.Contains("csharp", ids);
        Assert.Contains("postgresql", ids);

        // "some Rust." — a skill at the end of a sentence, followed by a full stop. It was missed before
        // the normalizer learned about contextual boundary characters, so it stays pinned here too.
        Assert.Contains("rust", ids);
    }

    [Fact]
    public async Task Free_text_reports_no_unrecognised_names()
    {
        await using var harness = await ToolHarness.StartAsync();

        var result = await harness.CallAsync(
            Mcp.ToolNames.ExtractSkills,
            new Dictionary<string, object?> { ["text"] = "Ten years of nothing in particular." });

        var payload = ToolHarness.StructuredOf(result);

        // Every word that is not a skill would qualify, so the list would be noise rather than signal.
        Assert.Empty(payload.GetProperty("unrecognisedNames").EnumerateArray());
    }

    [Fact]
    public async Task An_explicit_name_list_reports_what_the_taxonomy_rejected()
    {
        await using var harness = await ToolHarness.StartAsync();

        var result = await harness.CallAsync(
            Mcp.ToolNames.ExtractSkills,
            new Dictionary<string, object?>
            {
                ["names"] = new[] { "C#", "Postgres", "COBOL", "Fortran" },
            });

        var payload = ToolHarness.StructuredOf(result);

        Assert.Equal(
            ["csharp", "postgresql"],
            payload.GetProperty("skills").EnumerateArray().Select(s => s.GetProperty("id").GetString()!).ToArray());

        // Here the list is meaningful: the caller asserted each entry was a skill. "We ignored COBOL and
        // Fortran" is actionable where a short result list is not.
        Assert.Equal(
            ["COBOL", "Fortran"],
            payload.GetProperty("unrecognisedNames").EnumerateArray().Select(n => n.GetString()!).ToArray());
    }

    [Fact]
    public async Task Aliases_normalize_onto_canonical_ids()
    {
        await using var harness = await ToolHarness.StartAsync();

        var result = await harness.CallAsync(
            Mcp.ToolNames.ExtractSkills,
            new Dictionary<string, object?> { ["names"] = new[] { "Postgres", "postgresql", "POSTGRESQL" } });

        var payload = ToolHarness.StructuredOf(result);

        var only = Assert.Single(payload.GetProperty("skills").EnumerateArray().ToArray());
        Assert.Equal("postgresql", only.GetProperty("id").GetString());
        Assert.Equal("PostgreSQL", only.GetProperty("displayName").GetString());
    }

    [Fact]
    public async Task Neither_input_is_refused_with_instructions()
    {
        await using var harness = await ToolHarness.StartAsync();

        var result = await harness.CallAsync(Mcp.ToolNames.ExtractSkills, new Dictionary<string, object?>());

        Assert.True(result.IsError);
        Assert.Contains("Supply one of text", harness.TextOf(result), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Both_inputs_are_refused_rather_than_resolved_by_precedence()
    {
        await using var harness = await ToolHarness.StartAsync();

        var result = await harness.CallAsync(
            Mcp.ToolNames.ExtractSkills,
            new Dictionary<string, object?>
            {
                ["text"] = "C# developer",
                ["names"] = new[] { "Rust" },
            });

        // A silent winner is ambiguity a model cannot see it lost: it would send both, get results for
        // one, and have no way to tell which.
        Assert.True(result.IsError);
        Assert.Contains("not both", harness.TextOf(result), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_perfect_match_scores_one_hundred_with_three_components()
    {
        await using var harness = await ToolHarness.StartAsync(
            new FakeJobRepository([ToolTestData.MadridBackend]),
            new FakeCandidateRepository([ToolTestData.MadridSenior]));

        var result = await harness.CallAsync(
            Mcp.ToolNames.ScoreCandidateFit,
            new Dictionary<string, object?>
            {
                ["candidateId"] = ToolTestData.MadridSenior.Id,
                ["jobId"] = ToolTestData.MadridBackend.Id,
            });

        var payload = ToolHarness.StructuredOf(result);

        Assert.Equal(100, payload.GetProperty("total").GetDouble());

        var components = payload.GetProperty("components").EnumerateArray().ToArray();
        Assert.Equal(
            ["skill_overlap", "seniority_distance", "location_compatibility"],
            components.Select(c => c.GetProperty("name").GetString()!).ToArray());

        // The weighted score is sent so a caller can explain the total without multiplying, and the
        // reason code is a name rather than an ordinal.
        Assert.All(components, c =>
        {
            Assert.Equal(
                c.GetProperty("rawScore").GetDouble() * c.GetProperty("weight").GetDouble(),
                c.GetProperty("weightedScore").GetDouble(),
                precision: 10);
            Assert.False(string.IsNullOrWhiteSpace(c.GetProperty("reason").GetString()));
            Assert.False(string.IsNullOrWhiteSpace(c.GetProperty("detail").GetString()));
        });
    }

    [Fact]
    public async Task Scoring_the_same_pair_twice_gives_the_same_answer()
    {
        await using var harness = await ToolHarness.StartAsync(
            new FakeJobRepository([ToolTestData.MadridBackend]),
            new FakeCandidateRepository([ToolTestData.MadridSenior]));

        var arguments = new Dictionary<string, object?>
        {
            ["candidateId"] = ToolTestData.MadridSenior.Id,
            ["jobId"] = ToolTestData.MadridBackend.Id,
        };

        var first = ToolHarness.StructuredOf(await harness.CallAsync(Mcp.ToolNames.ScoreCandidateFit, arguments));
        var second = ToolHarness.StructuredOf(await harness.CallAsync(Mcp.ToolNames.ScoreCandidateFit, arguments));

        // The property A1's eval harness depends on: this is ground truth, not another model's opinion.
        Assert.Equal(first.GetRawText(), second.GetRawText());
    }

    [Fact]
    public async Task A_missing_job_is_named_as_the_missing_one()
    {
        await using var harness = await ToolHarness.StartAsync(
            new FakeJobRepository([]),
            new FakeCandidateRepository([ToolTestData.MadridSenior]));

        var missingJob = Guid.Parse("88888888-8888-8888-8888-888888888888");

        var result = await harness.CallAsync(
            Mcp.ToolNames.ScoreCandidateFit,
            new Dictionary<string, object?>
            {
                ["candidateId"] = ToolTestData.MadridSenior.Id,
                ["jobId"] = missingJob,
            });

        Assert.True(result.IsError);

        // Which id was wrong is named: a caller told only "not found" has to guess which of the two to
        // fix. Contrast search_jobs, where four handle failures are collapsed on purpose.
        Assert.Contains("job posting", harness.TextOf(result), StringComparison.OrdinalIgnoreCase);
        Assert.Contains(missingJob.ToString(), harness.TextOf(result), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_missing_candidate_is_named_as_the_missing_one()
    {
        await using var harness = await ToolHarness.StartAsync(
            new FakeJobRepository([ToolTestData.MadridBackend]),
            new FakeCandidateRepository([]));

        var missingCandidate = Guid.Parse("77777777-7777-7777-7777-777777777777");

        var result = await harness.CallAsync(
            Mcp.ToolNames.ScoreCandidateFit,
            new Dictionary<string, object?>
            {
                ["candidateId"] = missingCandidate,
                ["jobId"] = ToolTestData.MadridBackend.Id,
            });

        Assert.True(result.IsError);
        Assert.Contains("candidate", harness.TextOf(result), StringComparison.OrdinalIgnoreCase);
        Assert.Contains(missingCandidate.ToString(), harness.TextOf(result), StringComparison.Ordinal);
    }
}
