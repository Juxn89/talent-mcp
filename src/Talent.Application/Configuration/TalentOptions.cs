namespace Talent.Application.Configuration;

using Talent.Domain.Scoring;

/// <summary>
/// Tunables for the use cases: page sizes, handle lifetimes, scoring weights.
/// <para>
/// A plain settings object, not an <c>IOptions&lt;T&gt;</c>. The architecture test forbids
/// <c>Microsoft.Extensions.Options</c> in this layer, so the presentation host binds configuration to
/// this type and injects the value. The use cases therefore stay constructible in a unit test with one
/// <c>new</c> and no service provider — which is the whole point of the restriction.
/// </para>
/// </summary>
public sealed class TalentOptions
{
    /// <summary>Configuration section name a host should bind from.</summary>
    public const string SectionName = "Talent";

    /// <summary>Page size used when a caller does not ask for one.</summary>
    public int DefaultPageSize { get; init; } = 20;

    /// <summary>
    /// Largest page a caller may request. A cap rather than a suggestion: without it, a client can ask
    /// for the entire table in one response and turn a paginated tool into an unbounded one.
    /// </summary>
    public int MaxPageSize { get; init; } = 100;

    /// <summary>
    /// How long a pagination handle stays valid.
    /// <para>
    /// Short on purpose. A handle carries an offset into a result set that keeps changing as postings
    /// open and close, so a long-lived one silently paginates through a snapshot that no longer exists.
    /// Ten minutes is longer than any interactive session needs and short enough that staleness stays
    /// bounded.
    /// </para>
    /// </summary>
    public TimeSpan PaginationHandleTimeToLive { get; init; } = TimeSpan.FromMinutes(10);

    /// <summary>
    /// How long an MRTR confirmation stays valid — the window between the server asking "really reject
    /// this candidate?" and the client coming back with the answer.
    /// <para>
    /// The shorter of the two handle lifetimes, and deliberately so. It brackets a human decision, and
    /// a confirmation that outlives the conversation it belonged to can be replayed against a
    /// destructive operation. Five minutes is longer than anyone needs to answer one question.
    /// </para>
    /// </summary>
    public TimeSpan ConfirmationHandleTimeToLive { get; init; } = TimeSpan.FromMinutes(5);

    /// <summary>Largest shortlist a single bulk-scoring call may accept.</summary>
    public int MaxShortlistSize { get; init; } = 500;

    /// <summary>
    /// Component weighting for fit scoring. Defaults to <see cref="ScoringWeights.Default"/>; a host may
    /// override it, which is how the domain stays free of configuration while remaining tunable.
    /// </summary>
    public ScoringWeights ScoringWeights { get; init; } = ScoringWeights.Default;

    /// <summary>
    /// Whether the options are coherent. Called at startup so a bad configuration fails fast, rather
    /// than producing quietly wrong pages or an out-of-range score at the first request.
    /// </summary>
    /// <param name="error">A description of the first problem found.</param>
    /// <returns><see langword="true"/> when the options can be used.</returns>
    public bool TryValidate(out string? error)
    {
        if (this.DefaultPageSize <= 0)
        {
            error = $"{nameof(this.DefaultPageSize)} must be positive; got {this.DefaultPageSize}.";
            return false;
        }

        if (this.MaxPageSize < this.DefaultPageSize)
        {
            error =
                $"{nameof(this.MaxPageSize)} ({this.MaxPageSize}) must be at least "
                + $"{nameof(this.DefaultPageSize)} ({this.DefaultPageSize}).";
            return false;
        }

        if (this.PaginationHandleTimeToLive <= TimeSpan.Zero)
        {
            error = $"{nameof(this.PaginationHandleTimeToLive)} must be positive.";
            return false;
        }

        if (this.ConfirmationHandleTimeToLive <= TimeSpan.Zero)
        {
            error = $"{nameof(this.ConfirmationHandleTimeToLive)} must be positive.";
            return false;
        }

        if (this.MaxShortlistSize <= 0)
        {
            error = $"{nameof(this.MaxShortlistSize)} must be positive; got {this.MaxShortlistSize}.";
            return false;
        }

        if (!this.ScoringWeights.IsValid())
        {
            error =
                $"{nameof(this.ScoringWeights)} must be non-negative and sum to 1; got "
                + $"{this.ScoringWeights.Total}.";
            return false;
        }

        error = null;
        return true;
    }
}
