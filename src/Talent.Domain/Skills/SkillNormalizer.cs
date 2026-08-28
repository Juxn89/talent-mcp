namespace Talent.Domain.Skills;

using Talent.Domain.Entities;

/// <summary>
/// Normalizes free text — a CV, a job description — onto canonical taxonomy skills.
/// <para>
/// A pure function over <see cref="SkillTaxonomy"/>: no repository, no <c>DbContext</c>, no LLM. That
/// is what lets its tests run in milliseconds without Docker, and what lets A1 reuse it unchanged.
/// </para>
/// </summary>
public static class SkillNormalizer
{
    /// <summary>
    /// Characters that may sit directly against an alias without being part of it. An alias only
    /// matches when both of its edges land on one of these or on a string boundary, which is what
    /// stops "go" matching inside "mongodb" or "algorithm".
    /// </summary>
    private static readonly char[] BoundaryChars =
        [' ', '\t', '\n', '\r', ',', ';', ':', '/', '\\', '|', '(', ')', '[', ']', '{', '}',
         '"', '\'', '!', '?', '<', '>', '=', '*', '&', '%', '~', '`'];

    /// <summary>
    /// Extracts the canonical skills mentioned in <paramref name="text"/>.
    /// <para>
    /// Aliases are matched longest-first, so "ASP.NET Core" resolves to <c>aspnet-core</c> rather than
    /// being split by the shorter "asp.net". A matched span is consumed, so the same words cannot
    /// produce two skills.
    /// </para>
    /// </summary>
    /// <param name="text">Free text. <see langword="null"/> or whitespace yields an empty result.</param>
    /// <returns>
    /// The distinct skills found, ordered by canonical id. The ordering is deterministic on purpose:
    /// a tool result that reorders between identical calls would break the revision's prompt caching
    /// and make the conformance suite flaky.
    /// </returns>
    public static IReadOnlyList<Skill> Extract(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return [];
        }

        var haystack = text.ToLowerInvariant();

        // Tracks which character positions a previous, longer alias already claimed.
        var consumed = new bool[haystack.Length];
        var found = new HashSet<string>(StringComparer.Ordinal);

        foreach (var alias in SkillTaxonomy.AliasesLongestFirst)
        {
            var searchFrom = 0;

            while (searchFrom <= haystack.Length - alias.Length)
            {
                var at = haystack.IndexOf(alias, searchFrom, StringComparison.Ordinal);
                if (at < 0)
                {
                    break;
                }

                searchFrom = at + 1;

                if (!IsStandaloneMatch(haystack, at, alias.Length) || IsConsumed(consumed, at, alias.Length))
                {
                    continue;
                }

                var skillId = SkillTaxonomy.ResolveAlias(alias);
                if (skillId is null)
                {
                    continue;
                }

                found.Add(skillId);
                Consume(consumed, at, alias.Length);
            }
        }

        return [.. found
            .Select(SkillTaxonomy.FindById)
            .Where(static s => s is not null)
            .Select(static s => s!)
            .OrderBy(static s => s.Id, StringComparer.Ordinal)];
    }

    /// <summary>
    /// Normalizes a list of already-separated skill names — the shape a structured CV or an import
    /// file gives you — onto canonical ids, discarding anything the taxonomy does not recognise.
    /// </summary>
    /// <param name="names">Skill names or aliases.</param>
    /// <returns>The distinct canonical ids, ordered.</returns>
    public static IReadOnlyList<string> NormalizeAll(IEnumerable<string>? names)
    {
        if (names is null)
        {
            return [];
        }

        return [.. names
            .Select(SkillTaxonomy.ResolveAlias)
            .Where(static id => id is not null)
            .Select(static id => id!)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(static id => id, StringComparer.Ordinal)];
    }

    /// <summary>
    /// Names in <paramref name="names"/> that the taxonomy does not recognise. Returned so a tool can
    /// tell a caller what it ignored instead of silently dropping it — an actionable error, which the
    /// tool-level tests assert on.
    /// </summary>
    /// <param name="names">Skill names or aliases.</param>
    /// <returns>The unrecognised names, trimmed and ordered.</returns>
    public static IReadOnlyList<string> FindUnrecognised(IEnumerable<string>? names)
    {
        if (names is null)
        {
            return [];
        }

        return [.. names
            .Where(static n => !string.IsNullOrWhiteSpace(n))
            .Select(static n => n.Trim())
            .Where(static n => SkillTaxonomy.ResolveAlias(n) is null)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(static n => n, StringComparer.Ordinal)];
    }

    private static bool IsStandaloneMatch(string haystack, int at, int length) =>
        IsBoundary(haystack, at - 1, step: -1) && IsBoundary(haystack, at + length, step: +1);

    /// <summary>
    /// Characters that appear inside skill names (".net", "c#", "next.js", "react-native") and so
    /// cannot be unconditional boundaries — otherwise "net" would match inside ".net". They count as a
    /// boundary only when the character on the far side of them is not alphanumeric, which is how
    /// sentence punctuation is told apart from a compound token: in "Rust." the '.' ends a sentence, in
    /// ".net" and "next.js" it joins two parts of one name.
    /// </summary>
    private static readonly char[] ContextualChars = ['.', '-', '+', '#', '_'];

    /// <summary>
    /// Whether position <paramref name="index"/> is a boundary for a match, looking outward in the
    /// direction given by <paramref name="step"/> (-1 before the match, +1 after it).
    /// </summary>
    private static bool IsBoundary(string haystack, int index, int step)
    {
        if (index < 0 || index >= haystack.Length)
        {
            return true;
        }

        var c = haystack[index];

        if (Array.IndexOf(BoundaryChars, c) >= 0)
        {
            return true;
        }

        if (Array.IndexOf(ContextualChars, c) >= 0)
        {
            var outward = index + step;

            return outward < 0
                || outward >= haystack.Length
                || !char.IsLetterOrDigit(haystack[outward]);
        }

        return false;
    }

    private static bool IsConsumed(bool[] consumed, int at, int length)
    {
        for (var i = at; i < at + length; i++)
        {
            if (consumed[i])
            {
                return true;
            }
        }

        return false;
    }

    private static void Consume(bool[] consumed, int at, int length)
    {
        for (var i = at; i < at + length; i++)
        {
            consumed[i] = true;
        }
    }
}
