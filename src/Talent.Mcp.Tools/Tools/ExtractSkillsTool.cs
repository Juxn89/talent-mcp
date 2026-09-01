namespace Talent.Mcp.Tools.Tools;

using System.ComponentModel;
using ModelContextProtocol;
using ModelContextProtocol.Server;
using Talent.Application.UseCases;
using Talent.Mcp.Tools.Constants;
using Talent.Mcp.Tools.Contracts;

/// <summary>
/// Normalizes free text or an explicit name list onto canonical taxonomy skills.
/// <para>
/// The cheapest tool in the surface and the one the rest depend on: it is a pure function over the
/// taxonomy, so it needs no database, no API key and no LLM. Its determinism is what lets the
/// conformance suite assert exact outputs, and it is how a caller turns "5 years of Postgres and
/// dotnet" into the canonical ids that <c>search_jobs</c> filters by.
/// </para>
/// </summary>
[McpServerToolType]
public sealed class ExtractSkillsTool
{
    /// <summary>Normalizes skills from text or from a name list.</summary>
    /// <param name="extract">Injected use case.</param>
    /// <param name="text">Free text to scan.</param>
    /// <param name="names">Already-separated skill names to normalize.</param>
    /// <returns>The canonical skills, plus names the taxonomy did not recognise.</returns>
    /// <exception cref="McpException">Neither argument was supplied, or both were.</exception>
    [McpServerTool(
        Name = Mcp.ToolNames.ExtractSkills,
        Title = "Extract canonical skills",
        ReadOnly = true,
        Idempotent = true,
        OpenWorld = false,
        UseStructuredContent = true),
     Description(
        "Normalizes skills against the taxonomy and returns their canonical ids. Deterministic: the "
        + "same input always yields the same output, and no language model is involved. Supply exactly "
        + "one of text (a CV or job description to scan) or names (an already-separated list). With "
        + "names, entries the taxonomy does not know are reported in unrecognisedNames rather than "
        + "silently dropped.")]
    public static ExtractSkillsResponse Execute(
        ExtractSkillsUseCase extract,
        [Description("Free text to scan for skill mentions, e.g. a CV or a job description.")]
        string? text = null,
        [Description("Skill names or aliases to normalize, e.g. [\"C#\", \".NET\", \"Postgres\"].")]
        string[]? names = null)
    {
        ArgumentNullException.ThrowIfNull(extract);

        var hasText = !string.IsNullOrWhiteSpace(text);
        var hasNames = names is { Length: > 0 };

        // Rejected rather than resolved by precedence. Two inputs with a silent winner is the kind of
        // ambiguity a model cannot see it lost: it would send both, get results for one, and have no
        // way to tell which.
        if (hasText == hasNames)
        {
            throw new McpException(
                hasText
                    ? "Supply either text or names, not both."
                    : "Supply one of text (free text to scan) or names (a list of skill names).");
        }

        var result = hasText ? extract.FromText(text) : extract.FromNames(names);

        return new ExtractSkillsResponse(
            result.Skills.Select(SkillContract.From).ToArray(),
            result.UnrecognisedNames);
    }
}
