namespace Talent.Mcp.Tools.Constants;

/// <summary>
/// Protocol identity and the wire names of the tool surface.
/// <para>
/// Every one of these appears in at least three places — the server, the conformance suite and the
/// demo client — which is exactly the shape that breeds magic strings. The wire name in particular is
/// a published contract: the SDK derives one from the C# method name when the attribute does not pin
/// it, so without these constants a rename would silently change what clients call.
/// </para>
/// </summary>
public static class Mcp
{
    /// <summary>Server name reported in <c>server/discover</c> and in the client handshake.</summary>
    /// <remarks>
    /// The <em>version</em> is deliberately not a constant here: it is read from the assembly's
    /// informational version so it cannot drift from the <c>.csproj</c>.
    /// </remarks>
    public const string ServerName = "talent-mcp";

    /// <summary>Header carrying the region a read should be served from. See <c>get_job</c>.</summary>
    public const string RegionHeader = "Region";

    /// <summary>
    /// Keys identifying the MRTR input requests this server can raise.
    /// <para>
    /// The key is the correlation id between the <c>input_required</c> result and the
    /// <c>inputResponses</c> entry the client sends back, so it is part of the wire contract and belongs
    /// here rather than inlined at the throw site.
    /// </para>
    /// </summary>
    public static class InputRequestKeys
    {
        /// <summary>Confirmation of a candidate rejection. See <c>reject_candidate</c>.</summary>
        public const string ConfirmRejection = "confirm_rejection";
    }

    /// <summary>Protocol revisions this server speaks.</summary>
    public static class ProtocolVersions
    {
        /// <summary>The revision this server implements.</summary>
        public const string Supported = "2026-07-28";

        /// <summary>
        /// Revisions accepted through negotiation. A client on one of these is served statelessly like
        /// any other — per ADR-0001 there is no session era to fall back to.
        /// </summary>
        public static readonly string[] Interop = ["2025-11-25"];
    }

    /// <summary>The wire names of the six tools, in the order they are registered.</summary>
    public static class ToolNames
    {
        /// <summary>Paginated job search; continuation travels in a signed handle.</summary>
        public const string SearchJobs = "search_jobs";

        /// <summary>Single job read, region-routed through a header.</summary>
        public const string GetJob = "get_job";

        /// <summary>Deterministic skill normalization against the taxonomy.</summary>
        public const string ExtractSkills = "extract_skills";

        /// <summary>Explainable candidate-fit score with a per-component breakdown.</summary>
        public const string ScoreCandidateFit = "score_candidate_fit";

        /// <summary>Destructive rejection, gated behind an MRTR confirmation.</summary>
        public const string RejectCandidate = "reject_candidate";

        /// <summary>Long-running bulk scoring, driven by the Tasks extension.</summary>
        public const string BulkScoreShortlist = "bulk_score_shortlist";

        /// <summary>
        /// Every tool name, in registration order.
        /// <para>
        /// The order is the contract the conformance suite asserts: the revision asks for a
        /// deterministic <c>tools/list</c> because a stable order improves an LLM's prompt cache hit
        /// rate. Registration order is what produces it, so this array and
        /// <c>TalentTools.AddTalentTools</c> must stay in step.
        /// </para>
        /// </summary>
        public static readonly string[] All =
        [
            SearchJobs,
            GetJob,
            ExtractSkills,
            ScoreCandidateFit,
            RejectCandidate,
            BulkScoreShortlist,
        ];
    }
}
