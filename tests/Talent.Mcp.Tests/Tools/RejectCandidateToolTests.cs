namespace Talent.Mcp.Tests.Tools;

using System.Text.Json;
using ModelContextProtocol.Protocol;
using Talent.Application.Configuration;
using Talent.Mcp.Tools.Constants;
using Talent.Mcp.Tools.Tools;
using Xunit;

/// <summary>
/// <c>reject_candidate</c>: the destructive tool, and the MRTR exchange.
/// <para>
/// The SDK client drives the round-trip itself — it intercepts <c>input_required</c>, calls the
/// elicitation handler and retries with <c>inputResponses</c> and the server's <c>requestState</c>. So
/// most of these tests supply a handler and assert the end state, and the ones that need to control the
/// second leg build it by hand.
/// </para>
/// </summary>
public sealed class RejectCandidateToolTests
{
    private const string GoodReason = "Failed the take-home exercise twice.";

    private static Dictionary<string, object?> Arguments(string? reason = GoodReason, bool? confirmed = null)
    {
        var arguments = new Dictionary<string, object?>
        {
            ["candidateId"] = ToolTestData.MadridSenior.Id,
        };

        if (reason is not null) { arguments["reason"] = reason; }
        if (confirmed is not null) { arguments["confirmed"] = confirmed.Value; }

        return arguments;
    }

    private static RecordingCandidateRepository Repository() =>
        new([ToolTestData.MadridSenior]);

    private static ValueTask<ElicitResult> Accept(params (string Field, object Value)[] content)
    {
        var payload = content.ToDictionary(
            static c => c.Field,
            static c => JsonSerializer.SerializeToElement(c.Value),
            StringComparer.Ordinal);

        return ValueTask.FromResult(new ElicitResult { Action = "accept", Content = payload });
    }

    [Fact]
    public async Task A_confirmed_rejection_is_recorded_with_its_reason()
    {
        var candidates = Repository();
        await using var harness = await ToolHarness.StartAsync(
            candidates: candidates,
            elicitationHandler: (_, _) => Accept(("confirm", true)));

        var payload = ToolHarness.StructuredOf(
            await harness.CallAsync(Mcp.ToolNames.RejectCandidate, Arguments()));

        Assert.Equal("Rejected", payload.GetProperty("outcome").GetString());
        Assert.Equal("UserConfirmed", payload.GetProperty("confirmation").GetString());
        Assert.Equal(GoodReason, payload.GetProperty("reason").GetString());

        var written = Assert.Single(candidates.Rejections);
        Assert.Equal(ToolTestData.MadridSenior.Id, written.CandidateId);
        Assert.Equal(GoodReason, written.Reason);
    }

    [Fact]
    public async Task The_prompt_states_the_candidate_and_the_reason_on_record()
    {
        ElicitRequestParams? prompt = null;

        await using var harness = await ToolHarness.StartAsync(
            candidates: Repository(),
            elicitationHandler: (p, _) =>
            {
                prompt = p;
                return Accept(("confirm", true));
            });

        await harness.CallAsync(Mcp.ToolNames.RejectCandidate, Arguments());

        Assert.NotNull(prompt);
        Assert.Contains(ToolTestData.MadridSenior.Id.ToString(), prompt!.Message, StringComparison.Ordinal);
        Assert.Contains(GoodReason, prompt.Message, StringComparison.Ordinal);

        // A destructive confirmation that does not say it is irreversible is not a confirmation.
        Assert.Contains("cannot be undone", prompt.Message, StringComparison.Ordinal);

        // Only the confirmation is asked for: a usable reason was already supplied, so asking again
        // would be a second question the caller has already answered.
        var properties = prompt.RequestedSchema!.Properties;
        Assert.Equal(["confirm"], properties.Keys.ToArray());
    }

    [Fact]
    public async Task A_declined_confirmation_writes_nothing_and_is_not_an_error()
    {
        var candidates = Repository();
        await using var harness = await ToolHarness.StartAsync(
            candidates: candidates,
            elicitationHandler: (_, _) => ValueTask.FromResult(new ElicitResult { Action = "decline" }));

        var result = await harness.CallAsync(Mcp.ToolNames.RejectCandidate, Arguments());

        // Not an error: the exchange worked exactly as designed and the answer was no. Reporting a
        // decline as a failure would push a client towards retrying it.
        Assert.NotEqual(true, result.IsError);
        Assert.Equal(
            "DeclinedAtConfirmation",
            ToolHarness.StructuredOf(result).GetProperty("outcome").GetString());
        Assert.Empty(candidates.Rejections);
    }

    [Fact]
    public async Task A_cancelled_confirmation_writes_nothing()
    {
        var candidates = Repository();
        await using var harness = await ToolHarness.StartAsync(
            candidates: candidates,
            elicitationHandler: (_, _) => ValueTask.FromResult(new ElicitResult { Action = "cancel" }));

        var payload = ToolHarness.StructuredOf(
            await harness.CallAsync(Mcp.ToolNames.RejectCandidate, Arguments()));

        Assert.Equal("DeclinedAtConfirmation", payload.GetProperty("outcome").GetString());
        Assert.Empty(candidates.Rejections);
    }

    [Fact]
    public async Task An_accepted_confirmation_that_says_no_writes_nothing()
    {
        var candidates = Repository();
        await using var harness = await ToolHarness.StartAsync(
            candidates: candidates,
            elicitationHandler: (_, _) => Accept(("confirm", false)));

        var payload = ToolHarness.StructuredOf(
            await harness.CallAsync(Mcp.ToolNames.RejectCandidate, Arguments()));

        // "accept" means the user answered the form, not that they said yes. Conflating the two would
        // turn every dismissed dialog into a rejection.
        Assert.Equal("DeclinedAtConfirmation", payload.GetProperty("outcome").GetString());
        Assert.Empty(candidates.Rejections);
    }

    [Fact]
    public async Task A_confirmation_missing_the_field_writes_nothing()
    {
        var candidates = Repository();
        await using var harness = await ToolHarness.StartAsync(
            candidates: candidates,
            elicitationHandler: (_, _) => Accept());

        var payload = ToolHarness.StructuredOf(
            await harness.CallAsync(Mcp.ToolNames.RejectCandidate, Arguments()));

        // An absent answer is not a yes. The safe reading of an ambiguous confirmation is "no", and
        // this is the case a malformed client would produce.
        Assert.Equal("DeclinedAtConfirmation", payload.GetProperty("outcome").GetString());
        Assert.Empty(candidates.Rejections);
    }

    [Fact]
    public async Task Without_a_reason_the_prompt_asks_for_one_and_records_what_the_user_typed()
    {
        const string TypedReason = "Withdrew after the second interview.";
        ElicitRequestParams? prompt = null;

        var candidates = Repository();
        await using var harness = await ToolHarness.StartAsync(
            candidates: candidates,
            elicitationHandler: (p, _) =>
            {
                prompt = p;
                return Accept(("confirm", true), ("reason", TypedReason));
            });

        var payload = ToolHarness.StructuredOf(
            await harness.CallAsync(Mcp.ToolNames.RejectCandidate, Arguments(reason: null)));

        // Both questions in one round-trip. The domain requires a reason, so asking "are you sure?"
        // and then "why?" separately would be two exchanges where one does.
        Assert.NotNull(prompt);
        Assert.Equal(
            ["confirm", "reason"],
            prompt!.RequestedSchema!.Properties.Keys.OrderBy(static k => k, StringComparer.Ordinal).ToArray());

        Assert.Equal("Rejected", payload.GetProperty("outcome").GetString());
        Assert.Equal(TypedReason, payload.GetProperty("reason").GetString());
        Assert.Equal(TypedReason, Assert.Single(candidates.Rejections).Reason);
    }

    [Fact]
    public async Task A_too_short_reason_is_treated_as_no_reason_at_all()
    {
        ElicitRequestParams? prompt = null;

        await using var harness = await ToolHarness.StartAsync(
            candidates: Repository(),
            elicitationHandler: (p, _) =>
            {
                prompt = p;
                return Accept(("confirm", true), ("reason", "Rejected because the role was withdrawn."));
            });

        await harness.CallAsync(Mcp.ToolNames.RejectCandidate, Arguments(reason: "no"));

        // "no" satisfies "a reason was supplied" while making the audit trail worthless. The prompt
        // asks for a real one rather than accepting it.
        Assert.NotNull(prompt);
        Assert.Contains("reason", prompt!.RequestedSchema!.Properties.Keys);
    }

    [Fact]
    public async Task A_client_cannot_swap_the_reason_between_confirming_and_writing()
    {
        var candidates = Repository();
        await using var harness = await ToolHarness.StartAsync(candidates: candidates);

        // The retry is built by hand, because the SDK client would drive it and never let a test
        // disagree with the signed state. The state says one reason; the arguments say another.
        var state = harness.Mint(
            new PendingRejection(ToolTestData.MadridSenior.Id, GoodReason),
            TimeSpan.FromMinutes(5));

        var result = await harness.CallRawAsync(new CallToolRequestParams
        {
            Name = Mcp.ToolNames.RejectCandidate,
            Arguments = new Dictionary<string, JsonElement>(StringComparer.Ordinal)
            {
                ["candidateId"] = JsonSerializer.SerializeToElement(ToolTestData.MadridSenior.Id),
                ["reason"] = JsonSerializer.SerializeToElement("Not a culture fit, actually."),
            },
            RequestState = state,
            InputResponses = new Dictionary<string, InputResponse>(StringComparer.Ordinal)
            {
                [Mcp.InputRequestKeys.ConfirmRejection] = InputResponse.FromElicitResult(
                    new ElicitResult
                    {
                        Action = "accept",
                        Content = new Dictionary<string, JsonElement>(StringComparer.Ordinal)
                        {
                            ["confirm"] = JsonSerializer.SerializeToElement(true),
                        },
                    }),
            },
        });

        // The invariant that makes the confirmation worth anything: what was approved is what gets
        // written. Otherwise a user could authorise "failed the take-home" and the record would say
        // "not a culture fit".
        Assert.Equal(GoodReason, ToolHarness.StructuredOf(result).GetProperty("reason").GetString());
        Assert.Equal(GoodReason, Assert.Single(candidates.Rejections).Reason);
    }

    [Fact]
    public async Task A_confirmation_state_that_is_not_authentic_is_refused()
    {
        var candidates = Repository();
        await using var harness = await ToolHarness.StartAsync(candidates: candidates);

        var result = await harness.CallRawAsync(new CallToolRequestParams
        {
            Name = Mcp.ToolNames.RejectCandidate,
            Arguments = new Dictionary<string, JsonElement>(StringComparer.Ordinal)
            {
                ["candidateId"] = JsonSerializer.SerializeToElement(ToolTestData.MadridSenior.Id),
            },
            RequestState = "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA",
            InputResponses = new Dictionary<string, InputResponse>(StringComparer.Ordinal)
            {
                [Mcp.InputRequestKeys.ConfirmRejection] = InputResponse.FromElicitResult(
                    new ElicitResult { Action = "accept" }),
            },
        });

        Assert.True(result.IsError);
        Assert.Contains("expired or is not valid", harness.TextOf(result), StringComparison.Ordinal);
        Assert.Empty(candidates.Rejections);
    }

    [Fact]
    public async Task A_confirmation_state_minted_for_something_else_is_refused()
    {
        var candidates = Repository();
        await using var harness = await ToolHarness.StartAsync(candidates: candidates);

        // Signed by this server, unexpired, wrong payload type: a pagination cursor presented as a
        // rejection confirmation. The payload-type marker in the signed region is what stops it.
        var foreign = harness.Mint(
            new Talent.Application.UseCases.JobSearchCursor(
                "engineer", [], "ES", Talent.Domain.Enums.WorkArrangement.Remote, 0, 20),
            TimeSpan.FromMinutes(5));

        var result = await harness.CallRawAsync(new CallToolRequestParams
        {
            Name = Mcp.ToolNames.RejectCandidate,
            Arguments = new Dictionary<string, JsonElement>(StringComparer.Ordinal)
            {
                ["candidateId"] = JsonSerializer.SerializeToElement(ToolTestData.MadridSenior.Id),
            },
            RequestState = foreign,
            InputResponses = new Dictionary<string, InputResponse>(StringComparer.Ordinal)
            {
                [Mcp.InputRequestKeys.ConfirmRejection] = InputResponse.FromElicitResult(
                    new ElicitResult { Action = "accept" }),
            },
        });

        Assert.True(result.IsError);
        Assert.Empty(candidates.Rejections);
    }

    [Fact]
    public async Task An_expired_confirmation_is_refused()
    {
        var clock = new MutableClock(new DateTimeOffset(2026, 9, 2, 9, 0, 0, TimeSpan.Zero));
        var candidates = Repository();

        await using var harness = await ToolHarness.StartAsync(
            candidates: candidates,
            options: new TalentOptions { ConfirmationHandleTimeToLive = TimeSpan.FromMinutes(5) },
            timeProvider: clock);

        var state = harness.Mint(
            new PendingRejection(ToolTestData.MadridSenior.Id, GoodReason),
            TimeSpan.FromMinutes(5));

        clock.Advance(TimeSpan.FromMinutes(6));

        var result = await harness.CallRawAsync(new CallToolRequestParams
        {
            Name = Mcp.ToolNames.RejectCandidate,

            // candidateId is sent even though the signed state carries it. It is a required parameter,
            // so omitting it makes the SDK's argument binder reject the call before the tool body runs —
            // and this test would then pass without ever reaching the expiry check.
            Arguments = new Dictionary<string, JsonElement>(StringComparer.Ordinal)
            {
                ["candidateId"] = JsonSerializer.SerializeToElement(ToolTestData.MadridSenior.Id),
            },
            RequestState = state,
            InputResponses = new Dictionary<string, InputResponse>(StringComparer.Ordinal)
            {
                [Mcp.InputRequestKeys.ConfirmRejection] = InputResponse.FromElicitResult(
                    new ElicitResult
                    {
                        Action = "accept",
                        Content = new Dictionary<string, JsonElement>(StringComparer.Ordinal)
                        {
                            ["confirm"] = JsonSerializer.SerializeToElement(true),
                        },
                    }),
            },
        });

        // A stale confirmation must not authorise a destructive write. The TTL is the shortest of the
        // three handle lifetimes for exactly this reason. Asserted on the message, not just on IsError,
        // so a different failure cannot make this pass.
        Assert.True(result.IsError);
        Assert.Contains("expired or is not valid", harness.TextOf(result), StringComparison.Ordinal);
        Assert.Empty(candidates.Rejections);
    }

    [Fact]
    public async Task A_state_without_a_response_is_a_malformed_retry_not_a_fresh_call()
    {
        await using var harness = await ToolHarness.StartAsync(candidates: Repository());

        var state = harness.Mint(
            new PendingRejection(ToolTestData.MadridSenior.Id, GoodReason),
            TimeSpan.FromMinutes(5));

        var result = await harness.CallRawAsync(new CallToolRequestParams
        {
            Name = Mcp.ToolNames.RejectCandidate,
            Arguments = new Dictionary<string, JsonElement>(StringComparer.Ordinal)
            {
                ["candidateId"] = JsonSerializer.SerializeToElement(ToolTestData.MadridSenior.Id),
            },
            RequestState = state,
        });

        // Treating half a retry as a first call would silently restart the exchange and ask the user
        // the same question again, which looks like the server ignoring their answer.
        Assert.True(result.IsError);
        Assert.Contains("both requestState and", harness.TextOf(result), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_client_without_elicitation_is_told_how_to_proceed()
    {
        var candidates = Repository();
        await using var harness = await ToolHarness.StartAsync(
            candidates: candidates,
            clientProtocolVersion: "2025-11-25");

        var result = await harness.CallAsync(Mcp.ToolNames.RejectCandidate, Arguments());

        Assert.True(result.IsError);

        var message = harness.TextOf(result);
        Assert.Contains("does not support the MRTR", message, StringComparison.Ordinal);
        Assert.Contains("confirmed: true", message, StringComparison.Ordinal);
        Assert.Empty(candidates.Rejections);
    }

    [Fact]
    public async Task A_client_without_elicitation_may_assert_its_own_confirmation()
    {
        var candidates = Repository();
        await using var harness = await ToolHarness.StartAsync(
            candidates: candidates,
            clientProtocolVersion: "2025-11-25");

        var payload = ToolHarness.StructuredOf(await harness.CallAsync(
            Mcp.ToolNames.RejectCandidate,
            Arguments(confirmed: true)));

        Assert.Equal("Rejected", payload.GetProperty("outcome").GetString());

        // Labelled, not equated. Nothing here reached a human — a model can set a boolean on its own —
        // so the result says which channel was used and a reader can weigh it accordingly.
        Assert.Equal("ClientAsserted", payload.GetProperty("confirmation").GetString());
        Assert.Equal(GoodReason, Assert.Single(candidates.Rejections).Reason);
    }

    [Fact]
    public async Task A_client_without_elicitation_still_needs_a_real_reason()
    {
        var candidates = Repository();
        await using var harness = await ToolHarness.StartAsync(
            candidates: candidates,
            clientProtocolVersion: "2025-11-25");

        var result = await harness.CallAsync(
            Mcp.ToolNames.RejectCandidate,
            Arguments(reason: "no", confirmed: true));

        // The degraded path degrades the confirmation channel, not the domain rule. A rejection with
        // no stated reason is refused for every caller.
        Assert.True(result.IsError);
        Assert.Empty(candidates.Rejections);
    }

    [Fact]
    public async Task Confirming_a_candidate_that_does_not_exist_says_so()
    {
        await using var harness = await ToolHarness.StartAsync(
            candidates: new RecordingCandidateRepository([]),
            elicitationHandler: (_, _) => Accept(("confirm", true)));

        var missing = Guid.Parse("66666666-6666-6666-6666-666666666666");

        var result = await harness.CallAsync(
            Mcp.ToolNames.RejectCandidate,
            new Dictionary<string, object?> { ["candidateId"] = missing, ["reason"] = GoodReason });

        Assert.True(result.IsError);
        Assert.Contains(missing.ToString(), harness.TextOf(result), StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_tool_is_advertised_as_destructive_and_not_read_only()
    {
        await using var harness = await ToolHarness.StartAsync();

        var tool = (await harness.Client.ListToolsAsync())
            .Single(t => t.Name == Mcp.ToolNames.RejectCandidate)
            .ProtocolTool;

        // The annotations are how a client decides whether to gate an operation in its own UI. Getting
        // them wrong on the one destructive tool in the surface would defeat the point of having them.
        Assert.Equal(true, tool.Annotations?.DestructiveHint);
        Assert.Equal(false, tool.Annotations?.ReadOnlyHint);
    }
}

/// <summary>A candidate repository that records what it was asked to reject.</summary>
internal sealed class RecordingCandidateRepository(IReadOnlyList<Talent.Domain.Entities.Candidate> candidates)
    : Talent.Application.Ports.ICandidateRepository
{
    /// <summary>Every rejection that reached the repository.</summary>
    public List<(Guid CandidateId, string Reason)> Rejections { get; } = [];

    /// <inheritdoc />
    public Task<Talent.Domain.Entities.Candidate?> FindByIdAsync(
        Guid id,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(candidates.FirstOrDefault(c => c.Id == id));

    /// <inheritdoc />
    public Task<IReadOnlyList<Talent.Domain.Entities.Candidate>> FindByIdsAsync(
        IReadOnlyList<Guid> ids,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(ids);

        IReadOnlyList<Talent.Domain.Entities.Candidate> found = candidates
            .Where(c => ids.Contains(c.Id))
            .OrderBy(c => c.Id)
            .ToArray();

        return Task.FromResult(found);
    }

    /// <inheritdoc />
    public Task<bool> RejectAsync(Guid id, string reason, CancellationToken cancellationToken = default)
    {
        if (candidates.All(c => c.Id != id))
        {
            return Task.FromResult(false);
        }

        this.Rejections.Add((id, reason));
        return Task.FromResult(true);
    }
}
