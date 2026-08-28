namespace Talent.Domain.Skills;

using System.Collections.Frozen;
using Talent.Domain.Entities;
using Talent.Domain.Enums;

/// <summary>
/// The canonical skill vocabulary and its aliases.
/// <para>
/// This is the reference data that makes <c>extract_skills</c> deterministic: free text is matched
/// against these aliases rather than sent to a model, so the tool needs no API key, costs nothing,
/// and returns the same answer every time. A1 reuses this table as-is for its eval harness, which is
/// why it lives in the domain and not in a database.
/// </para>
/// <para>
/// Aliases are matched case-insensitively and are stored lower-cased. Adding a skill means adding a
/// row here; there is deliberately no runtime extension point, because a taxonomy that can change
/// under a running scorer stops being reproducible.
/// </para>
/// </summary>
public static class SkillTaxonomy
{
    private static readonly Skill[] SkillList =
    [
        // Backend
        new("dotnet", ".NET", SkillCategory.Backend),
        new("csharp", "C#", SkillCategory.Backend),
        new("aspnet-core", "ASP.NET Core", SkillCategory.Backend),
        new("java", "Java", SkillCategory.Backend),
        new("spring-boot", "Spring Boot", SkillCategory.Backend),
        new("python", "Python", SkillCategory.Backend),
        new("nodejs", "Node.js", SkillCategory.Backend),
        new("go", "Go", SkillCategory.Backend),
        new("rust", "Rust", SkillCategory.Backend),
        new("grpc", "gRPC", SkillCategory.Backend),
        new("graphql", "GraphQL", SkillCategory.Backend),

        // Frontend
        new("typescript", "TypeScript", SkillCategory.Frontend),
        new("javascript", "JavaScript", SkillCategory.Frontend),
        new("react", "React", SkillCategory.Frontend),
        new("nextjs", "Next.js", SkillCategory.Frontend),
        new("angular", "Angular", SkillCategory.Frontend),
        new("vue", "Vue.js", SkillCategory.Frontend),
        new("tailwind", "Tailwind CSS", SkillCategory.Frontend),
        new("blazor", "Blazor", SkillCategory.Frontend),

        // Data
        new("sql-server", "SQL Server", SkillCategory.Data),
        new("postgresql", "PostgreSQL", SkillCategory.Data),
        new("mongodb", "MongoDB", SkillCategory.Data),
        new("redis", "Redis", SkillCategory.Data),
        new("elasticsearch", "Elasticsearch", SkillCategory.Data),
        new("kafka", "Apache Kafka", SkillCategory.Data),
        new("spark", "Apache Spark", SkillCategory.Data),
        new("dbt", "dbt", SkillCategory.Data),
        new("pgvector", "pgvector", SkillCategory.Data),

        // DevOps
        new("docker", "Docker", SkillCategory.DevOps),
        new("kubernetes", "Kubernetes", SkillCategory.DevOps),
        new("terraform", "Terraform", SkillCategory.DevOps),
        new("github-actions", "GitHub Actions", SkillCategory.DevOps),
        new("jenkins", "Jenkins", SkillCategory.DevOps),
        new("helm", "Helm", SkillCategory.DevOps),

        // Cloud
        new("azure", "Microsoft Azure", SkillCategory.Cloud),
        new("aws", "Amazon Web Services", SkillCategory.Cloud),
        new("gcp", "Google Cloud Platform", SkillCategory.Cloud),

        // Mobile
        new("android", "Android", SkillCategory.Mobile),
        new("ios", "iOS", SkillCategory.Mobile),
        new("flutter", "Flutter", SkillCategory.Mobile),
        new("react-native", "React Native", SkillCategory.Mobile),
        new("maui", ".NET MAUI", SkillCategory.Mobile),

        // Testing
        new("xunit", "xUnit", SkillCategory.Testing),
        new("playwright", "Playwright", SkillCategory.Testing),
        new("selenium", "Selenium", SkillCategory.Testing),
        new("k6", "k6", SkillCategory.Testing),

        // Security
        new("oauth2", "OAuth 2.0", SkillCategory.Security),
        new("keycloak", "Keycloak", SkillCategory.Security),
        new("owasp", "OWASP", SkillCategory.Security),
        new("threat-modeling", "Threat Modeling", SkillCategory.Security),

        // Soft
        new("mentoring", "Mentoring", SkillCategory.Soft),
        new("technical-writing", "Technical Writing", SkillCategory.Soft),
        new("stakeholder-management", "Stakeholder Management", SkillCategory.Soft),
    ];

    /// <summary>
    /// Alias to canonical id. Every canonical id is also its own alias, added below rather than
    /// repeated here.
    /// </summary>
    private static readonly (string Alias, string SkillId)[] AliasList =
    [
        (".net", "dotnet"),
        ("dot net", "dotnet"),
        ("netcore", "dotnet"),
        (".net core", "dotnet"),
        ("c#", "csharp"),
        ("c sharp", "csharp"),
        ("asp.net", "aspnet-core"),
        ("asp.net core", "aspnet-core"),
        ("aspnet", "aspnet-core"),
        ("minimal api", "aspnet-core"),
        ("minimal apis", "aspnet-core"),
        ("spring", "spring-boot"),
        ("springboot", "spring-boot"),
        ("py", "python"),
        ("node", "nodejs"),
        ("node js", "nodejs"),
        ("golang", "go"),
        ("grpc", "grpc"),
        ("ts", "typescript"),
        ("js", "javascript"),
        ("reactjs", "react"),
        ("react.js", "react"),
        ("next", "nextjs"),
        ("next.js", "nextjs"),
        ("angularjs", "angular"),
        ("vuejs", "vue"),
        ("vue.js", "vue"),
        ("tailwindcss", "tailwind"),
        ("tailwind css", "tailwind"),
        ("mssql", "sql-server"),
        ("ms sql", "sql-server"),
        ("t-sql", "sql-server"),
        ("tsql", "sql-server"),
        ("postgres", "postgresql"),
        ("psql", "postgresql"),
        ("mongo", "mongodb"),
        ("elastic", "elasticsearch"),
        ("elk", "elasticsearch"),
        ("apache kafka", "kafka"),
        ("apache spark", "spark"),
        ("pyspark", "spark"),
        ("k8s", "kubernetes"),
        ("kube", "kubernetes"),
        ("tf", "terraform"),
        ("gh actions", "github-actions"),
        ("github ci", "github-actions"),
        ("azure devops", "azure"),
        ("microsoft azure", "azure"),
        ("amazon web services", "aws"),
        ("google cloud", "gcp"),
        ("google cloud platform", "gcp"),
        ("react native", "react-native"),
        (".net maui", "maui"),
        ("xamarin", "maui"),
        ("swift", "ios"),
        ("swiftui", "ios"),
        ("kotlin", "android"),
        ("x-unit", "xunit"),
        ("oauth", "oauth2"),
        ("oauth 2", "oauth2"),
        ("oauth 2.0", "oauth2"),
        ("oidc", "oauth2"),
        ("openid connect", "oauth2"),
        ("threat modelling", "threat-modeling"),
        ("coaching", "mentoring"),
        ("docs", "technical-writing"),
        ("documentation", "technical-writing"),
    ];

    private static readonly FrozenDictionary<string, Skill> SkillsById =
        SkillList.ToFrozenDictionary(static s => s.Id, StringComparer.Ordinal);

    private static readonly FrozenDictionary<string, string> SkillIdByAlias = BuildAliasIndex();

    /// <summary>Every canonical skill, ordered by id so the ordering is deterministic.</summary>
    public static IReadOnlyList<Skill> All { get; } =
        [.. SkillList.OrderBy(static s => s.Id, StringComparer.Ordinal)];

    /// <summary>
    /// Every recognised alias, ordered longest-first. Longest-first matters: matching "asp.net core"
    /// before "asp.net" is what stops a longer phrase being consumed by a shorter alias inside it.
    /// </summary>
    public static IReadOnlyList<string> AliasesLongestFirst { get; } =
        [.. SkillIdByAlias.Keys
            .OrderByDescending(static a => a.Length)
            .ThenBy(static a => a, StringComparer.Ordinal)];

    /// <summary>Looks up a canonical skill by its id.</summary>
    /// <param name="skillId">Canonical id, case-insensitive.</param>
    /// <returns>The skill, or <see langword="null"/> when the id is not in the taxonomy.</returns>
    public static Skill? FindById(string? skillId) =>
        string.IsNullOrWhiteSpace(skillId)
            ? null
            : SkillsById.GetValueOrDefault(skillId.Trim().ToLowerInvariant());

    /// <summary>Resolves an alias to its canonical skill id.</summary>
    /// <param name="alias">Alias or canonical id, case-insensitive.</param>
    /// <returns>The canonical id, or <see langword="null"/> when unrecognised.</returns>
    public static string? ResolveAlias(string? alias) =>
        string.IsNullOrWhiteSpace(alias)
            ? null
            : SkillIdByAlias.GetValueOrDefault(alias.Trim().ToLowerInvariant());

    /// <summary>Whether the id names a skill in the taxonomy.</summary>
    /// <param name="skillId">Canonical id, case-insensitive.</param>
    /// <returns><see langword="true"/> when the skill exists.</returns>
    public static bool Contains(string? skillId) => FindById(skillId) is not null;

    /// <summary>All canonical skills in one category, ordered by id.</summary>
    /// <param name="category">The category to filter by.</param>
    /// <returns>The matching skills.</returns>
    public static IReadOnlyList<Skill> InCategory(SkillCategory category) =>
        [.. SkillList
            .Where(s => s.Category == category)
            .OrderBy(static s => s.Id, StringComparer.Ordinal)];

    private static FrozenDictionary<string, string> BuildAliasIndex()
    {
        var index = new Dictionary<string, string>(StringComparer.Ordinal);

        // A canonical id resolves to itself, so callers never need to special-case it.
        foreach (var skill in SkillList)
        {
            index[skill.Id] = skill.Id;

            // The display name is an alias too: "PostgreSQL" in a CV should match `postgresql`.
            index[skill.DisplayName.ToLowerInvariant()] = skill.Id;
        }

        foreach (var (alias, skillId) in AliasList)
        {
            index[alias] = skillId;
        }

        return index.ToFrozenDictionary(StringComparer.Ordinal);
    }
}
