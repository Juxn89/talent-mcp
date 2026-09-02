namespace Talent.Mcp.Tools.Contracts;

/// <summary>How a rejection request ended.</summary>
public enum RejectionOutcome
{
    /// <summary>The candidate was rejected and the reason recorded.</summary>
    Rejected = 0,

    /// <summary>
    /// The confirmation came back declined or cancelled, and nothing was written. A first-class outcome
    /// rather than an error: the round-trip worked exactly as designed, and the answer was no.
    /// </summary>
    DeclinedAtConfirmation = 1,
}

/// <summary>How the confirmation behind a rejection was obtained.</summary>
public enum ConfirmationChannel
{
    /// <summary>
    /// Through an MRTR round-trip: the server raised <c>input_required</c> and the client came back with
    /// an elicitation answer, which is a channel that reaches a person.
    /// </summary>
    UserConfirmed = 0,

    /// <summary>
    /// The client asserted confirmation in the tool arguments because it does not support MRTR.
    /// <para>
    /// Reported explicitly, and worth reading as a warning rather than a detail: nothing here reached a
    /// human. A model can set a boolean on its own, so this channel is strictly weaker than
    /// <see cref="UserConfirmed"/>. It exists because a client without MRTR has no other way to perform
    /// a destructive operation at all — degraded, not equivalent.
    /// </para>
    /// </summary>
    ClientAsserted = 1,
}

/// <summary>The result of a rejection request.</summary>
/// <param name="CandidateId">The candidate the request was about.</param>
/// <param name="Outcome">Whether the rejection happened.</param>
/// <param name="Reason">
/// The reason that was recorded, or the one that was about to be. Echoed back so a caller can see
/// exactly what was written — the reason travels inside the signed confirmation state, so this is also
/// the proof that it was not altered between the confirmation and the write.
/// </param>
/// <param name="Confirmation">How the confirmation was obtained.</param>
public sealed record RejectCandidateResponse(
    Guid CandidateId,
    RejectionOutcome Outcome,
    string Reason,
    ConfirmationChannel Confirmation);
