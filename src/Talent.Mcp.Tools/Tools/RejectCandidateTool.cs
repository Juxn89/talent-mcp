namespace Talent.Mcp.Tools.Tools;

using System.ComponentModel;
using System.Text.Json;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Talent.Application.Configuration;
using Talent.Application.Ports;
using Talent.Application.UseCases;
using Talent.Mcp.Toolkit;
using Talent.Mcp.Tools.Constants;
using Talent.Mcp.Tools.Contracts;

/// <summary>
/// State carried across an MRTR confirmation round-trip.
/// <para>
/// It travels as <c>requestState</c> inside a signed handle — not as a row in a pending-operations
/// table. Under stateless HTTP the retry can land on any node (ADR-0001), so a server-side table would
/// have to be shared between them; a signed handle needs nothing shared at all. Same mechanism as
/// pagination, which is the point of having it in the toolkit.
/// </para>
/// <para>
/// The reason is inside the signed region deliberately. If the client re-sent it on the retry, a
/// confirmation of "reject for failing the take-home" could execute as "reject for cultural fit" — a
/// confirmation that authorises something other than what it described.
/// </para>
/// </summary>
/// <param name="CandidateId">The candidate awaiting confirmation.</param>
/// <param name="Reason">The reason as it stood when confirmation was requested.</param>
public sealed record PendingRejection(Guid CandidateId, string Reason);

/// <summary>
/// Rejects a candidate — the destructive operation in the surface, and the one that demonstrates MRTR.
/// <para>
/// Server-initiated requests are gone in 2026-07-28: under stateless HTTP there is no server→client
/// channel to ask a question on. MRTR is the replacement — the tool throws
/// <see cref="InputRequiredException"/>, the client asks its user, and it calls again with
/// <c>inputResponses</c> and the <c>requestState</c> the server minted.
/// </para>
/// <para>
/// Also demonstrates the degraded path, which is the half that is easy to skip: a client whose
/// <see cref="McpServer.IsMrtrSupported"/> is <see langword="false"/> cannot answer a question, so it
/// must either be refused or be allowed to assert confirmation itself. This tool does the latter and
/// labels it, rather than pretending the two are the same.
/// </para>
/// </summary>
[McpServerToolType]
public sealed class RejectCandidateTool
{
    /// <summary>Rejects a candidate after confirmation.</summary>
    /// <param name="context">Request context, for the MRTR responses and the client's capabilities.</param>
    /// <param name="reject">Injected use case.</param>
    /// <param name="handles">Injected handle codec, for the signed <c>requestState</c>.</param>
    /// <param name="options">Injected tunables.</param>
    /// <param name="candidateId">The candidate to reject.</param>
    /// <param name="reason">Why they are being rejected.</param>
    /// <param name="confirmed">Client-asserted confirmation, honoured only without MRTR.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>What happened, and how the confirmation was obtained.</returns>
    /// <exception cref="InputRequiredException">
    /// Confirmation is needed and the client supports MRTR. Not a failure — the first half of a
    /// two-step exchange.
    /// </exception>
    /// <exception cref="McpException">
    /// The candidate does not exist, the confirmation state was not authentic, or the client cannot
    /// confirm and did not assert confirmation itself.
    /// </exception>
    [McpServerTool(
        Name = Mcp.ToolNames.RejectCandidate,
        Title = "Reject a candidate",
        ReadOnly = false,
        Destructive = true,
        Idempotent = true,
        OpenWorld = false,
        UseStructuredContent = true),
     Description(
        "Rejects a candidate and records why. DESTRUCTIVE and confirmation-gated: the first call "
        + "answers input_required, asking the user to confirm and — if none was supplied — to state a "
        + "reason. Call again with the returned requestState and the user's answer in inputResponses. "
        + "The reason is carried inside requestState, so it cannot be changed between the confirmation "
        + "and the write. A reason of at least 10 characters is required either way.")]
    public static async Task<RejectCandidateResponse> ExecuteAsync(
        RequestContext<CallToolRequestParams> context,
        RejectCandidateUseCase reject,
        IHandleCodec handles,
        TalentOptions options,
        [Description("The candidate to reject.")] Guid candidateId,
        [Description("Why the candidate is being rejected. At least 10 characters; stored for audit.")]
        string? reason = null,
        [Description(
            "Client-asserted confirmation. Honoured ONLY when the client does not support MRTR; "
            + "otherwise the confirmation round-trip is authoritative and this is ignored.")]
        bool confirmed = false,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(reject);
        ArgumentNullException.ThrowIfNull(handles);
        ArgumentNullException.ThrowIfNull(options);

        if (TryReadConfirmation(context, handles, out var confirmation))
        {
            var pending = confirmation!.Pending;

            // The reason recorded is the one inside the signed state when there was one, and otherwise
            // the one the user typed at the confirmation prompt. The precedence matters: the signed
            // reason is protected against the CLIENT changing it, while an elicited reason came from
            // the user in this very exchange. Neither case lets the client substitute a reason a user
            // already approved.
            var effectiveReason = pending.Reason.Length > 0
                ? pending.Reason
                : confirmation.ElicitedReason;

            if (!confirmation.Confirmed)
            {
                // Declined is an outcome, not an error: the exchange worked and the answer was no.
                return new RejectCandidateResponse(
                    pending.CandidateId,
                    RejectionOutcome.DeclinedAtConfirmation,
                    effectiveReason,
                    ConfirmationChannel.UserConfirmed);
            }

            return await ApplyAsync(
                reject,
                pending.CandidateId,
                effectiveReason,
                ConfirmationChannel.UserConfirmed,
                cancellationToken).ConfigureAwait(false);
        }

        var trimmedReason = (reason ?? string.Empty).Trim();
        var reasonIsUsable = trimmedReason.Length >= RejectCandidateUseCase.MinReasonLength;

        if (CanAskTheUser(context))
        {
            // MRTR wins even when the caller already passed confirmed: true. A boolean in the argument
            // list is something a model can set by itself; an elicitation is a channel that reaches a
            // person. Letting the argument bypass the round-trip would make the gate decorative.
            throw ConfirmationRequired(handles, options, candidateId, trimmedReason, reasonIsUsable);
        }

        return await DegradedAsync(
            reject, candidateId, trimmedReason, reasonIsUsable, confirmed, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Whether this client can actually be asked a question.
    /// <para>
    /// <b>Two conditions, not one.</b> The plan and an earlier revision of <c>AGENTS.md</c> describe the
    /// degraded path as "when <c>IsMrtrSupported</c> is false", and that is not sufficient. Measured
    /// 2 Sep 2026: a client declaring protocol <c>2025-11-25</c> has <c>IsMrtrSupported</c> true, and
    /// raising an elicitation for it fails inside the SDK with
    /// <c>InvalidOperationException: Client does not support elicitation requests.</c> — which reaches
    /// the caller as a generic protocol error with the message stripped, not as an actionable tool
    /// error.
    /// </para>
    /// <para>
    /// So <c>IsMrtrSupported</c> answers "is the MRTR mechanism available", and the elicitation
    /// capability answers "is there anyone at the other end to ask". A confirmation needs both.
    /// </para>
    /// <para>
    /// The capability is read through <see cref="McpClientCapabilityReader"/> rather than straight off
    /// <c>Server.ClientCapabilities</c>, because that property is <see langword="null"/> for every
    /// request under stateless HTTP — there is no <c>initialize</c> to populate it, and the client
    /// declares itself in each request's <c>_meta</c> instead. Reading only the property would put the
    /// HTTP host permanently on the degraded path while the stdio host used MRTR, which is precisely
    /// the divergence ADR-0004 exists to prevent.
    /// </para>
    /// </summary>
    /// <param name="context">The request being handled.</param>
    /// <returns>Whether an elicitation can be raised.</returns>
    private static bool CanAskTheUser(RequestContext<CallToolRequestParams> context) =>
        context.Server.IsMrtrSupported
        && McpClientCapabilityReader.DeclaresElicitation(
            context.Server.ClientCapabilities,
            context.Params?.Meta);

    /// <summary>What a confirmation retry carried.</summary>
    /// <param name="Pending">The state the confirmation belongs to.</param>
    /// <param name="Confirmed">Whether the user said yes.</param>
    /// <param name="ElicitedReason">
    /// The reason the user typed, when the prompt asked for one. Empty otherwise.
    /// </param>
    private sealed record Confirmation(PendingRejection Pending, bool Confirmed, string ElicitedReason);

    /// <summary>
    /// Reads the confirmation from a retry, when this call is one.
    /// </summary>
    /// <param name="context">Request context.</param>
    /// <param name="handles">Handle codec.</param>
    /// <param name="confirmation">What the retry carried.</param>
    /// <returns><see langword="false"/> when this is a first call rather than a retry.</returns>
    private static bool TryReadConfirmation(
        RequestContext<CallToolRequestParams> context,
        IHandleCodec handles,
        out Confirmation? confirmation)
    {
        confirmation = null;

        var parameters = context.Params;
        var state = parameters?.RequestState;

        var response = parameters?.InputResponses is { } responses
            && responses.TryGetValue(Mcp.InputRequestKeys.ConfirmRejection, out var found)
                ? found
                : null;

        if (string.IsNullOrWhiteSpace(state) && response is null)
        {
            return false;
        }

        // One half without the other is a malformed retry, not a first call. Treating it as a first
        // call would silently restart the exchange and ask the user the same question again.
        if (string.IsNullOrWhiteSpace(state) || response is null)
        {
            throw new McpException(
                "A rejection confirmation must carry both requestState and an inputResponses entry "
                + $"keyed '{Mcp.InputRequestKeys.ConfirmRejection}'. Start again by calling "
                + "reject_candidate without either.");
        }

        if (!handles.TryRead<PendingRejection>(state, out var readPending) || readPending is null)
        {
            throw new McpException(
                "The rejection confirmation has expired or is not valid. Call reject_candidate again "
                + "to start a fresh confirmation.");
        }

        var (confirmed, elicitedReason) = ReadElicitedConfirmation(response);
        confirmation = new Confirmation(readPending, confirmed, elicitedReason);
        return true;
    }

    private static (bool Confirmed, string Reason) ReadElicitedConfirmation(InputResponse response)
    {
        // Nullable: Deserialize returns null for a JSON null payload. Grouped with the decline cases
        // below rather than dereferenced — a destructive write must not depend on a payload the
        // deserializer could not make sense of.
        ElicitResult? result;
        try
        {
            result = response.Deserialize(InputResponse.ElicitResultJsonTypeInfo);
        }
        catch (JsonException ex)
        {
            throw new McpException(
                "The rejection confirmation could not be read as an elicitation result.", ex);
        }

        // A cancelled or dismissed elicitation is a decline. Anything other than an explicit accept
        // must not authorise a destructive write — the safe reading of an ambiguous answer is "no".
        if (result is null || !result.IsAccepted || result.Content is not { } content)
        {
            return (false, string.Empty);
        }

        var confirmed = content.TryGetValue(ConfirmField, out var value)
            && value.ValueKind == JsonValueKind.True;

        var reason = content.TryGetValue(ReasonField, out var reasonValue)
            && reasonValue.ValueKind == JsonValueKind.String
                ? (reasonValue.GetString() ?? string.Empty).Trim()
                : string.Empty;

        return (confirmed, reason);
    }

    private static InputRequiredException ConfirmationRequired(
        IHandleCodec handles,
        TalentOptions options,
        Guid candidateId,
        string trimmedReason,
        bool reasonIsUsable)
    {
        var schema = new ElicitRequestParams.RequestSchema
        {
            Properties = new Dictionary<string, ElicitRequestParams.PrimitiveSchemaDefinition>(
                StringComparer.Ordinal)
            {
                [ConfirmField] = new ElicitRequestParams.BooleanSchema { Default = false },
            },
            Required = [ConfirmField],
        };

        if (!reasonIsUsable)
        {
            // The reason is asked for in the same round-trip rather than in a second one. The domain
            // requires it (RejectCandidateUseCase.MinReasonLength), and asking "are you sure?" and
            // "why?" separately would be two questions where one will do.
            schema.Properties[ReasonField] = new ElicitRequestParams.StringSchema
            {
                MinLength = RejectCandidateUseCase.MinReasonLength,
            };
            schema.Required = [ConfirmField, ReasonField];
        }

        var message = reasonIsUsable
            ? $"Reject candidate {candidateId}? Reason on record: \"{trimmedReason}\". "
              + "This cannot be undone from this tool."
            : $"Reject candidate {candidateId}? A reason of at least "
              + $"{RejectCandidateUseCase.MinReasonLength} characters is required, and this cannot be "
              + "undone from this tool.";

        var inputRequests = new Dictionary<string, InputRequest>(StringComparer.Ordinal)
        {
            [Mcp.InputRequestKeys.ConfirmRejection] = InputRequest.ForElicitation(
                new ElicitRequestParams { Message = message, RequestedSchema = schema }),
        };

        var state = handles.Mint(
            new PendingRejection(candidateId, trimmedReason),
            options.ConfirmationHandleTimeToLive);

        return new InputRequiredException(inputRequests, state);
    }

    private static async Task<RejectCandidateResponse> DegradedAsync(
        RejectCandidateUseCase reject,
        Guid candidateId,
        string trimmedReason,
        bool reasonIsUsable,
        bool confirmed,
        CancellationToken cancellationToken)
    {
        if (!confirmed || !reasonIsUsable)
        {
            // This client cannot be asked anything, so the requirements have to be stated instead of
            // elicited. Both are named in one message: telling a caller about one missing argument at a
            // time costs a round-trip per requirement, and this is the path that has no round-trips.
            throw new McpException(
                "This client does not support the MRTR confirmation round-trip, so reject_candidate "
                + "cannot ask for confirmation. Call it again with confirmed: true and a reason of at "
                + $"least {RejectCandidateUseCase.MinReasonLength} characters. The result will record "
                + "that the confirmation was asserted by the client rather than given by a user.");
        }

        return await ApplyAsync(
            reject, candidateId, trimmedReason, ConfirmationChannel.ClientAsserted, cancellationToken)
            .ConfigureAwait(false);
    }

    private static async Task<RejectCandidateResponse> ApplyAsync(
        RejectCandidateUseCase reject,
        Guid candidateId,
        string reason,
        ConfirmationChannel channel,
        CancellationToken cancellationToken)
    {
        var failure = await reject
            .ExecuteAsync(candidateId, reason, cancellationToken)
            .ConfigureAwait(false);

        return failure switch
        {
            RejectCandidateFailure.None =>
                new RejectCandidateResponse(candidateId, RejectionOutcome.Rejected, reason, channel),

            RejectCandidateFailure.CandidateNotFound =>
                throw new McpException($"No candidate with id {candidateId} exists."),

            // Reachable only if a confirmation was minted with a reason the domain refuses, which would
            // mean the two length checks had drifted apart. Surfaced rather than swallowed.
            _ => throw new McpException(
                $"A rejection reason of at least {RejectCandidateUseCase.MinReasonLength} characters "
                + "is required."),
        };
    }

    /// <summary>Elicitation field carrying the yes/no answer.</summary>
    private const string ConfirmField = "confirm";

    /// <summary>Elicitation field carrying the reason, asked for only when one was not supplied.</summary>
    private const string ReasonField = "reason";
}
