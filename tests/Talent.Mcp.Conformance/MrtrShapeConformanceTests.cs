namespace Talent.Mcp.Conformance;

using System.Text.Json;
using ModelContextProtocol.Protocol;
using Xunit;

/// <summary>
/// The MRTR wire shape itself — the elicitation schema a client needs to render a confirmation prompt —
/// rather than the business outcome of confirming or declining, which
/// <c>Talent.Mcp.E2E.RejectCandidateMrtrE2ETests</c> already covers end to end.
/// <para>
/// <c>reject_candidate</c> raises its <see cref="ModelContextProtocol.InputRequiredException"/> before
/// looking the candidate up (see <c>RejectCandidateTool.ExecuteAsync</c>), so these tests need no seeded
/// candidate to exist — a random id is enough to reach the confirmation prompt. The handler always
/// declines, so no write is ever attempted and no candidate needs to exist for that either.
/// </para>
/// </summary>
[Collection(ConformanceServerCollection.Name)]
public sealed class MrtrShapeConformanceTests
{
    private readonly ConformanceServerFixture fixture;

    public MrtrShapeConformanceTests(ConformanceServerFixture fixture)
    {
        this.fixture = fixture;
    }

    [Fact]
    public async Task A_confirmation_request_asks_for_a_boolean_confirm_and_names_the_candidate()
    {
        ElicitRequestParams? captured = null;

        await using var client = await this.fixture.CreateClientAsync((request, _) =>
        {
            captured = request;
            return ValueTask.FromResult(Decline());
        });

        var candidateId = Guid.NewGuid();
        await client.CallToolAsync(
            "reject_candidate",
            new Dictionary<string, object?>
            {
                ["candidateId"] = candidateId,
                ["reason"] = "Failed the technical screen: could not explain their own project.",
            });

        Assert.NotNull(captured);
        Assert.Contains(candidateId.ToString(), captured!.Message, StringComparison.Ordinal);

        var schema = captured!.RequestedSchema!;
        Assert.NotNull(schema.Required);
        Assert.Contains("confirm", schema.Required!);
        Assert.IsType<ElicitRequestParams.BooleanSchema>(
            schema.Properties["confirm"]);
    }

    [Fact]
    public async Task A_confirmation_request_without_a_usable_reason_also_asks_for_one()
    {
        ElicitRequestParams? captured = null;

        await using var client = await this.fixture.CreateClientAsync((request, _) =>
        {
            captured = request;
            return ValueTask.FromResult(Decline());
        });

        // No reason argument at all: the tool's own precondition (RejectCandidateUseCase.MinReasonLength)
        // is what decides whether the schema grows a second required field, so this is the input that
        // exercises the branch the previous test does not.
        await client.CallToolAsync(
            "reject_candidate",
            new Dictionary<string, object?> { ["candidateId"] = Guid.NewGuid() });

        Assert.NotNull(captured);

        var schema = captured!.RequestedSchema!;
        Assert.NotNull(schema.Required);
        Assert.Contains("confirm", schema.Required!);
        Assert.Contains("reason", schema.Required!);

        var reasonSchema = Assert.IsType<ElicitRequestParams.StringSchema>(schema.Properties["reason"]);
        Assert.Equal(10, reasonSchema.MinLength);
    }

    [Fact]
    public async Task A_client_with_no_elicitation_capability_is_told_which_capability_is_missing()
    {
        // No elicitation handler at all: the client declares no elicitation capability, which is what
        // CanAskTheUser in RejectCandidateTool actually gates on (AGENTS.md pitfall #19) — a lower
        // protocol version is one way to get there, but withholding the handler is the direct one.
        await using var client = await this.fixture.CreateClientAsync();

        var result = await client.CallToolAsync(
            "reject_candidate",
            new Dictionary<string, object?>
            {
                ["candidateId"] = Guid.NewGuid(),
                ["reason"] = "Duplicate application for the same requisition.",
            });

        Assert.True(result.IsError);

        var message = string.Join(
            Environment.NewLine,
            result.Content.OfType<TextContentBlock>().Select(static block => block.Text));
        Assert.Contains("does not support the MRTR", message, StringComparison.Ordinal);
    }

    private static ElicitResult Decline() => new()
    {
        Action = "accept",
        Content = new Dictionary<string, JsonElement>(StringComparer.Ordinal)
        {
            ["confirm"] = JsonSerializer.SerializeToElement(false),
        },
    };
}
