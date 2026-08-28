namespace Talent.Application.UseCases;

using Talent.Application.Ports;

/// <summary>Why a rejection was refused.</summary>
public enum RejectCandidateFailure
{
    /// <summary>No failure; the candidate was rejected.</summary>
    None = 0,

    /// <summary>No candidate with that id exists.</summary>
    CandidateNotFound = 1,

    /// <summary>
    /// No reason was supplied. This is the condition the presentation layer turns into an MRTR
    /// <c>input_required</c> round-trip rather than an error.
    /// </summary>
    ReasonRequired = 2,
}

/// <summary>
/// Rejects a candidate. The destructive operation in the tool surface.
/// <para>
/// The confirmation round-trip itself is a presentation concern — the tool throws
/// <c>InputRequiredException</c> and the client retries with <c>inputResponses</c>. What lives here is
/// the rule that makes that round-trip necessary: <strong>a rejection without a stated reason is
/// refused</strong>. Keeping it in the use case means the requirement holds for every caller, including
/// a client whose MRTR support is absent and which the presentation layer has to degrade for.
/// </para>
/// </summary>
public sealed class RejectCandidateUseCase
{
    /// <summary>
    /// Shortest acceptable rejection reason. Guards against a client satisfying the requirement with
    /// "no" or a single space, which would make the audit trail worthless while looking compliant.
    /// </summary>
    public const int MinReasonLength = 10;

    private readonly ICandidateRepository candidates;

    /// <summary>Creates the use case.</summary>
    /// <param name="candidates">Candidate repository port.</param>
    /// <exception cref="ArgumentNullException">The repository was <see langword="null"/>.</exception>
    public RejectCandidateUseCase(ICandidateRepository candidates)
    {
        ArgumentNullException.ThrowIfNull(candidates);

        this.candidates = candidates;
    }

    /// <summary>Rejects a candidate, recording why.</summary>
    /// <param name="candidateId">The candidate to reject.</param>
    /// <param name="reason">
    /// Why they were rejected. Required and stored for audit; must be at least
    /// <see cref="MinReasonLength"/> characters after trimming.
    /// </param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The failure reason, or <see cref="RejectCandidateFailure.None"/> on success.</returns>
    public async Task<RejectCandidateFailure> ExecuteAsync(
        Guid candidateId,
        string? reason,
        CancellationToken cancellationToken = default)
    {
        var trimmed = reason?.Trim() ?? string.Empty;

        // Checked before the lookup: a caller who forgot the reason should be told that, not told
        // whether the id they were about to act on exists. The negative case leaks less this way.
        if (trimmed.Length < MinReasonLength)
        {
            return RejectCandidateFailure.ReasonRequired;
        }

        var rejected = await this.candidates
            .RejectAsync(candidateId, trimmed, cancellationToken)
            .ConfigureAwait(false);

        return rejected ? RejectCandidateFailure.None : RejectCandidateFailure.CandidateNotFound;
    }
}
