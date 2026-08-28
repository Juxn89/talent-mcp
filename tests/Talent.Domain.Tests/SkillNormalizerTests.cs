namespace Talent.Domain.Tests;

using Talent.Domain.Enums;
using Talent.Domain.Skills;
using Xunit;

/// <summary>
/// Table-driven tests for deterministic skill normalization. No Docker, no fixtures, milliseconds —
/// which is the payoff for keeping the domain free of framework dependencies.
/// </summary>
public sealed class SkillNormalizerTests
{
    [Theory]
    [InlineData(".NET", "dotnet")]
    [InlineData("dotnet", "dotnet")]
    [InlineData(".net core", "dotnet")]
    [InlineData("C#", "csharp")]
    [InlineData("c sharp", "csharp")]
    [InlineData("PostgreSQL", "postgresql")]
    [InlineData("postgres", "postgresql")]
    [InlineData("k8s", "kubernetes")]
    [InlineData("KUBERNETES", "kubernetes")]
    [InlineData("golang", "go")]
    [InlineData("Next.js", "nextjs")]
    [InlineData("react native", "react-native")]
    [InlineData("OIDC", "oauth2")]
    [InlineData("openid connect", "oauth2")]
    public void Resolves_alias_to_canonical_id(string alias, string expectedId) =>
        Assert.Equal(expectedId, SkillTaxonomy.ResolveAlias(alias));

    [Theory]
    [InlineData("cobol")]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("a skill nobody has")]
    public void Unrecognised_alias_resolves_to_null(string alias) =>
        Assert.Null(SkillTaxonomy.ResolveAlias(alias));

    [Fact]
    public void Null_alias_resolves_to_null() => Assert.Null(SkillTaxonomy.ResolveAlias(null));

    [Fact]
    public void Longest_alias_wins_so_a_shorter_one_inside_it_does_not_steal_the_match()
    {
        // "asp.net core" contains "asp.net", and both are aliases. Both happen to resolve to
        // aspnet-core, so the assertion that matters is that the text yields ONE skill, not that a
        // leftover fragment produced a second.
        var skills = SkillNormalizer.Extract("Strong ASP.NET Core experience");

        Assert.Single(skills);
        Assert.Equal("aspnet-core", skills[0].Id);
    }

    [Theory]
    [InlineData("We use MongoDB heavily", "mongodb")]
    [InlineData("Algorithms and data structures", "")]
    [InlineData("Good at Go and Python", "go,python")]
    public void Alias_only_matches_on_word_boundaries(string text, string expectedCsv)
    {
        // "go" sits inside "mongodb" and "algorithms"; matching it there would be the classic
        // substring bug that makes an extractor look broken to a recruiter.
        var actual = string.Join(",", SkillNormalizer.Extract(text).Select(s => s.Id));

        Assert.Equal(expectedCsv, actual);
    }

    [Theory]
    // A skill at the end of a sentence. '.' cannot be an unconditional boundary — that would make
    // "net" match inside ".net" — so it counts as one only when the character beyond it is not
    // alphanumeric. Found by a use-case test: "some Rust." silently matched nothing.
    [InlineData("We need some Rust.", "rust")]
    [InlineData("Rust, Go and Docker.", "docker,go,rust")]
    [InlineData("Experience with .NET.", "dotnet")]
    [InlineData("Deep .NET knowledge", "dotnet")]
    [InlineData("Uses Next.js in production", "nextjs")]
    [InlineData("Uses Next.js.", "nextjs")]
    [InlineData("Node.js on the backend", "nodejs")]
    [InlineData("React-Native apps", "react-native")]
    [InlineData("Strong C#.", "csharp")]
    public void Punctuation_around_a_skill_does_not_hide_it(string text, string expectedCsv)
    {
        var actual = string.Join(",", SkillNormalizer.Extract(text).Select(s => s.Id));

        Assert.Equal(expectedCsv, actual);
    }

    [Fact]
    public void Extract_returns_distinct_skills_ordered_by_id()
    {
        var skills = SkillNormalizer.Extract("TypeScript, React, typescript again, and Docker");

        Assert.Equal(["docker", "react", "typescript"], skills.Select(s => s.Id));
    }

    [Fact]
    public void Extract_is_deterministic_across_calls()
    {
        const string Text = "We need .NET, Kubernetes, PostgreSQL, React and OAuth 2.0 experience.";

        var first = SkillNormalizer.Extract(Text).Select(s => s.Id).ToArray();
        var second = SkillNormalizer.Extract(Text).Select(s => s.Id).ToArray();

        // Determinism is not a nicety here: the 2026-07-28 revision asks for stable tool output to
        // help LLM prompt caching, and a conformance test asserts it end to end.
        Assert.Equal(first, second);
        Assert.Equal(["dotnet", "kubernetes", "oauth2", "postgresql", "react"], first);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("    ")]
    public void Extract_on_empty_text_returns_empty(string? text) =>
        Assert.Empty(SkillNormalizer.Extract(text));

    [Fact]
    public void NormalizeAll_drops_unrecognised_names_and_orders_the_rest()
    {
        var ids = SkillNormalizer.NormalizeAll(["k8s", "cobol", "Docker", "postgres", "docker"]);

        Assert.Equal(["docker", "kubernetes", "postgresql"], ids);
    }

    [Fact]
    public void FindUnrecognised_reports_what_was_dropped()
    {
        // Reported rather than silently discarded: the tool tests assert on actionable errors, and
        // "we ignored COBOL and Fortran" is actionable where an empty result is not.
        var unknown = SkillNormalizer.FindUnrecognised(["k8s", "COBOL", "Fortran", "docker"]);

        Assert.Equal(["COBOL", "Fortran"], unknown);
    }

    [Fact]
    public void Taxonomy_ids_are_unique_and_self_resolving()
    {
        var ids = SkillTaxonomy.All.Select(s => s.Id).ToArray();

        Assert.Equal(ids.Length, ids.Distinct(StringComparer.Ordinal).Count());
        Assert.All(ids, id => Assert.Equal(id, SkillTaxonomy.ResolveAlias(id)));
    }

    [Fact]
    public void Taxonomy_has_no_skill_left_in_the_unknown_category()
    {
        // SkillCategory.Unknown exists so a default-constructed value is never a real category. A
        // taxonomy entry sitting in it means someone added a skill and forgot to classify it.
        Assert.DoesNotContain(SkillTaxonomy.All, s => s.Category == SkillCategory.Unknown);
    }

    [Fact]
    public void Taxonomy_all_is_ordered_by_id()
    {
        var ids = SkillTaxonomy.All.Select(s => s.Id).ToArray();

        Assert.Equal(ids.OrderBy(id => id, StringComparer.Ordinal), ids);
    }
}
