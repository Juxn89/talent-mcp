namespace Talent.Mcp.Tests.Tools;

using Talent.Mcp.Tools.Constants;
using Xunit;

/// <summary>
/// <c>get_job</c>: the cacheable read, and the one whose routing knob travels as a transport header
/// rather than as an argument.
/// </summary>
public sealed class GetJobToolTests
{
    private static FakeJobRepository Repository() =>
        new([ToolTestData.MadridBackend, ToolTestData.BerlinPlatform]);

    [Fact]
    public async Task A_posting_is_returned_in_full_including_its_description()
    {
        await using var harness = await ToolHarness.StartAsync(Repository());

        var result = await harness.CallAsync(
            Mcp.ToolNames.GetJob,
            new Dictionary<string, object?> { ["jobId"] = ToolTestData.MadridBackend.Id });

        var payload = ToolHarness.StructuredOf(result).GetProperty("job");

        Assert.Equal("Backend Engineer", payload.GetProperty("title").GetString());
        Assert.Equal("Build and operate the payments API.", payload.GetProperty("description").GetString());
        Assert.Equal("Senior", payload.GetProperty("seniority").GetString());
        Assert.Equal("Hybrid", payload.GetProperty("arrangement").GetString());
        Assert.Equal("Madrid", payload.GetProperty("city").GetString());
    }

    [Fact]
    public async Task Without_a_region_header_a_posting_from_any_region_is_served()
    {
        await using var harness = await ToolHarness.StartAsync(Repository());

        var result = await harness.CallAsync(
            Mcp.ToolNames.GetJob,
            new Dictionary<string, object?> { ["jobId"] = ToolTestData.BerlinPlatform.Id });

        var payload = ToolHarness.StructuredOf(result);

        Assert.Equal("Platform Engineer", payload.GetProperty("job").GetProperty("title").GetString());

        // Empty means "no routing applied", not "unknown region". The distinction matters: treating a
        // missing optional header as an unknown region would hide every posting.
        Assert.Equal(string.Empty, payload.GetProperty("servedRegion").GetString());
    }

    [Fact]
    public async Task A_matching_region_serves_the_posting_and_is_echoed_back()
    {
        await using var harness = await ToolHarness.StartAsync(Repository());

        var result = await harness.CallAsync(
            Mcp.ToolNames.GetJob,
            new Dictionary<string, object?>
            {
                ["jobId"] = ToolTestData.MadridBackend.Id,
                ["region"] = "ES",
            });

        var payload = ToolHarness.StructuredOf(result);

        Assert.Equal("Backend Engineer", payload.GetProperty("job").GetProperty("title").GetString());

        // Echoed because the result is cached with cacheScope private and the protocol's cache fields
        // have no Vary: a caller holding two responses must be able to tell which region each describes.
        Assert.Equal("ES", payload.GetProperty("servedRegion").GetString());
    }

    [Fact]
    public async Task A_region_is_matched_case_insensitively()
    {
        await using var harness = await ToolHarness.StartAsync(Repository());

        var result = await harness.CallAsync(
            Mcp.ToolNames.GetJob,
            new Dictionary<string, object?>
            {
                ["jobId"] = ToolTestData.MadridBackend.Id,
                ["region"] = "es",
            });

        Assert.NotEqual(true, result.IsError);
    }

    [Fact]
    public async Task A_posting_from_another_region_is_reported_as_not_served_here()
    {
        await using var harness = await ToolHarness.StartAsync(Repository());

        var result = await harness.CallAsync(
            Mcp.ToolNames.GetJob,
            new Dictionary<string, object?>
            {
                ["jobId"] = ToolTestData.BerlinPlatform.Id,
                ["region"] = "ES",
            });

        Assert.True(result.IsError);

        var message = harness.TextOf(result);

        // Deliberately NOT collapsed into "not found". A recruiter routed to the wrong regional
        // catalogue needs to know the posting is real and they are looking in the wrong place — unlike a
        // bad handle, this is not adversarial.
        Assert.Contains("not served in region", message, StringComparison.Ordinal);
        Assert.Contains("ES", message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_malformed_region_is_refused_before_the_posting_is_read()
    {
        var jobs = Repository();
        await using var harness = await ToolHarness.StartAsync(jobs);

        var result = await harness.CallAsync(
            Mcp.ToolNames.GetJob,
            new Dictionary<string, object?>
            {
                ["jobId"] = ToolTestData.MadridBackend.Id,
                ["region"] = "Spain",
            });

        Assert.True(result.IsError);
        Assert.Contains("two-letter", harness.TextOf(result), StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_unknown_id_says_so_plainly()
    {
        await using var harness = await ToolHarness.StartAsync(Repository());
        var missing = Guid.Parse("99999999-9999-9999-9999-999999999999");

        var result = await harness.CallAsync(
            Mcp.ToolNames.GetJob,
            new Dictionary<string, object?> { ["jobId"] = missing });

        Assert.True(result.IsError);
        Assert.Contains(missing.ToString(), harness.TextOf(result), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_posting_with_no_country_is_served_in_every_region()
    {
        var placeless = new Talent.Domain.Entities.Job(
            Guid.Parse("44444444-4444-4444-4444-444444444444"),
            "Remote Architect",
            "Location-independent role.",
            ["csharp"],
            Talent.Domain.Enums.SeniorityLevel.Principal,
            Talent.Domain.ValueObjects.Location.Unknown,
            Talent.Domain.Enums.WorkArrangement.Remote,
            Talent.Domain.ValueObjects.SalaryRange.NotDisclosed);

        await using var harness = await ToolHarness.StartAsync(new FakeJobRepository([placeless]));

        var result = await harness.CallAsync(
            Mcp.ToolNames.GetJob,
            new Dictionary<string, object?> { ["jobId"] = placeless.Id, ["region"] = "ES" });

        // A missing country is a gap in the data, not a routing rule. Hiding the posting would make a
        // seeding omission look like a deliberate regional restriction.
        Assert.NotEqual(true, result.IsError);
        Assert.Equal(
            "Remote Architect",
            ToolHarness.StructuredOf(result).GetProperty("job").GetProperty("title").GetString());
    }
}
