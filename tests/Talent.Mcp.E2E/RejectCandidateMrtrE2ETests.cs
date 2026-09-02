namespace Talent.Mcp.E2E;

using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using ModelContextProtocol.Protocol;
using Talent.Domain.Enums;
using Talent.Mcp.Tools.Constants;
using Xunit;

/// <summary>
/// <c>reject_candidate</c>'s full MRTR cycle — <c>input_required</c>, then a retry carrying
/// <c>inputResponses</c> and the server-minted <c>requestState</c> — driven by the real MCP client's own
/// elicitation loop over a real HTTP connection, and verified against what actually landed in Postgres.
/// <para>
/// The signed <c>requestState</c> is the part worth distrusting until proven: it has to survive being
/// serialized into a real JSON-RPC response, held by the client across a real network round-trip, and
/// deserialized back out of the retry — none of which the in-memory pipe transport in
/// <c>Talent.Mcp.Tests</c> can fail to do the way a real socket could.
/// </para>
/// </summary>
[Collection(RealServerCollection.Name)]
public sealed class RejectCandidateMrtrE2ETests
{
    /// <summary>Bruno Silva — seeded, active, not used by any other test in this collection.</summary>
    private static readonly Guid CandidateId = Guid.Parse("b0000002-0000-0000-0000-000000000000");

    private readonly RealServerFixture fixture;

    public RejectCandidateMrtrE2ETests(RealServerFixture fixture)
    {
        this.fixture = fixture;
    }

    [Fact]
    public async Task Confirming_the_elicitation_rejects_the_candidate_and_persists_the_reason()
    {
        const string Reason = "Failed the take-home assessment: no test coverage on the exercise.";

        await using var client = await this.fixture
            .CreateClientAsync((_, _) => ValueTask.FromResult(Accept(confirm: true)));

        var result = await client.CallToolAsync(
            "reject_candidate",
            new Dictionary<string, object?> { ["candidateId"] = CandidateId, ["reason"] = Reason });

        Assert.False(result.IsError is true, TextOf(result));

        var payload = result.StructuredContent!.Value;
        Assert.Equal("Rejected", payload.GetProperty("outcome").GetString());
        Assert.Equal("UserConfirmed", payload.GetProperty("confirmation").GetString());
        Assert.Equal(Reason, payload.GetProperty("reason").GetString());

        // The client's own elicitation loop drove the retry; this confirms the write it produced is the
        // one actually sitting in Postgres, not just the shape of the tool's response.
        await using var db = this.fixture.CreateDbContext();
        var candidate = await db.Candidates
            .AsNoTracking()
            .SingleAsync(c => c.Id == CandidateId);

        Assert.Equal(CandidateStatus.Rejected, candidate.Status);
        Assert.Equal(Reason, candidate.RejectionReason);
        Assert.NotNull(candidate.RejectedAt);
    }

    [Fact]
    public async Task Declining_the_elicitation_leaves_the_candidate_untouched()
    {
        var declinedId = Guid.Parse("b0000003-0000-0000-0000-000000000000"); // Clara Nowak

        await using var client = await this.fixture
            .CreateClientAsync((_, _) => ValueTask.FromResult(Accept(confirm: false)));

        var result = await client.CallToolAsync(
            "reject_candidate",
            new Dictionary<string, object?>
            {
                ["candidateId"] = declinedId,
                ["reason"] = "Considering for a different role instead.",
            });

        Assert.False(result.IsError is true, TextOf(result));
        Assert.Equal("DeclinedAtConfirmation", result.StructuredContent!.Value.GetProperty("outcome").GetString());

        await using var db = this.fixture.CreateDbContext();
        var candidate = await db.Candidates
            .AsNoTracking()
            .SingleAsync(c => c.Id == declinedId);

        Assert.Equal(CandidateStatus.Active, candidate.Status);
        Assert.Null(candidate.RejectionReason);
    }

    [Fact]
    public async Task A_client_that_cannot_be_asked_gets_told_how_to_proceed_instead()
    {
        // AGENTS.md pitfall #19, verified during F2: IsMrtrSupported alone is not the right guard — a
        // 2025-11-25 client still has it true. What actually puts this client on the degraded path is
        // that it declares no elicitation capability, which a client on the interop revision does not.
        // Same real-degraded-case construction RejectCandidateToolTests uses over the in-memory
        // transport; here it runs the tool's error-message path over a real HTTP call instead.
        await using var client = await this.fixture
            .CreateClientAsync(protocolVersion: Mcp.ProtocolVersions.Interop[0]);

        var candidateId = Guid.Parse("b0000004-0000-0000-0000-000000000000"); // Diego Marín

        var result = await client.CallToolAsync(
            "reject_candidate",
            new Dictionary<string, object?>
            {
                ["candidateId"] = candidateId,
                ["reason"] = "Location mismatch for an on-site-only role.",
            });

        Assert.True(result.IsError);

        var message = TextOf(result);
        Assert.Contains("does not support the MRTR", message, StringComparison.Ordinal);
        Assert.Contains("confirmed: true", message, StringComparison.Ordinal);

        await using var db = this.fixture.CreateDbContext();
        var candidate = await db.Candidates
            .AsNoTracking()
            .SingleAsync(c => c.Id == candidateId);
        Assert.Equal(CandidateStatus.Active, candidate.Status);
    }

    private static ElicitResult Accept(bool confirm) =>
        new()
        {
            Action = "accept",
            Content = new Dictionary<string, JsonElement>(StringComparer.Ordinal)
            {
                ["confirm"] = JsonSerializer.SerializeToElement(confirm),
            },
        };

    private static string TextOf(CallToolResult result) =>
        string.Join(
            Environment.NewLine,
            result.Content.OfType<TextContentBlock>().Select(static block => block.Text));
}
