namespace Talent.Mcp.Toolkit.Tracing;

using System.Text;

/// <summary>
/// A rough size proxy for a tool's serialized output, used for the <c>tool.output_tokens</c> span tag.
/// <para>
/// <b>Not a real token count.</b> No tool in this domain calls an LLM (see AGENTS.md) — there is no
/// tokenizer to run. This uses the common "~4 bytes per token" heuristic purely so the span schema has
/// a number to plot; it should never be read as an accurate count of anything.
/// </para>
/// </summary>
public static class TokenEstimate
{
    private const int BytesPerToken = 4;

    /// <summary>Estimates a token count from serialized JSON.</summary>
    /// <param name="json">The serialized output, or <see langword="null"/>/empty.</param>
    /// <returns>Zero for empty input, otherwise the byte count divided by <see cref="BytesPerToken"/>, rounded up.</returns>
    public static long Approximate(string? json) =>
        string.IsNullOrEmpty(json)
            ? 0
            : (long)Math.Ceiling(Encoding.UTF8.GetByteCount(json) / (double)BytesPerToken);
}
