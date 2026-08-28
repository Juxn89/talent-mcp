namespace Talent.Infrastructure.Seeding;

using Talent.Domain.Entities;
using Talent.Domain.Enums;
using Talent.Domain.ValueObjects;

/// <summary>
/// Realistic seed data for the recruitment domain.
/// <para>
/// Deliberately not lorem ipsum. A1 reuses this dataset to evaluate its RAG matching against the
/// deterministic scorer here, so the data has to contain the cases that make matching interesting:
/// near-misses on one skill, seniority mismatches in both directions, remote roles that make location
/// irrelevant, and candidates who are excellent but in the wrong country. Random filler would let a
/// bad matcher look good.
/// </para>
/// <para>
/// Every id is a fixed GUID rather than generated. Seeds are re-run, referenced from tests, and read
/// by another repository — an id that changes per run makes all three impossible.
/// </para>
/// </summary>
public static class SeedData
{
    // Expressions, NOT static readonly fields. Each access constructs a new Location.
    //
    // This is the bug that cost the most time building the seeds. A `static readonly Location Madrid`
    // is shared by every entity that references it, and EF Core attaches change-tracking state to an
    // owned instance on behalf of ONE owner. Four candidates sharing one Madrid instance means the
    // tracker has one owned entry claimed by four owners, and on SaveChanges it writes NULL for that
    // entry's columns — surfacing as `23502: null value in column "city"`, which names neither owned
    // types nor sharing.
    //
    // It hides well: saving each seed individually succeeds, so it only appears in a batch. Records
    // make it worse, because value equality means two separately-constructed Madrids also compare
    // equal. Owned value objects must be per-owner instances.
    private static Location Madrid => new("Madrid", "ES");

    private static Location Barcelona => new("Barcelona", "ES");

    private static Location Berlin => new("Berlin", "DE");

    private static Location Lisbon => new("Lisbon", "PT");

    private static Location London => new("London", "GB");

    private static Location Amsterdam => new("Amsterdam", "NL");

    /// <summary>
    /// Job postings. A FACTORY, not a cached list.
    /// <para>
    /// Returning fresh instances per call is not a style choice. EF Core takes ownership of an entity
    /// instance when it is added to a context: it attaches change-tracking state to that object and to
    /// its owned <c>Location</c> and <c>Salary</c>. Handing the same instance to a second context — two
    /// tests, or a retry — corrupts that state, and the symptom is
    /// <c>23502 null value in column "city"</c> on save, which points nowhere near the cause.
    /// </para>
    /// <para>
    /// Ids stay fixed across calls, so anything keyed on them still works.
    /// </para>
    /// </summary>
    /// <returns>Freshly constructed job postings.</returns>
    public static IReadOnlyList<Job> CreateJobs() =>
    [
        Job("a0000001", "Senior .NET Engineer, Platform",
            "Own the services behind our job-matching platform. Heavy .NET and PostgreSQL, "
            + "containerised on Kubernetes. You will work with Kafka for event ingestion.",
            ["dotnet", "postgresql", "kubernetes", "kafka"],
            SeniorityLevel.Senior, Madrid, WorkArrangement.Hybrid, Salary(65_000, 85_000)),

        Job("a0000002", "Staff Backend Engineer, Search",
            "Lead the relevance and search stack. Elasticsearch at scale, Go services, and the "
            + "occasional deep dive into query performance.",
            ["go", "elasticsearch", "kubernetes"],
            SeniorityLevel.Staff, Berlin, WorkArrangement.OnSite, Salary(90_000, 115_000)),

        Job("a0000003", "Frontend Engineer, Candidate Experience",
            "Build the candidate-facing application in React and TypeScript. Next.js, Tailwind CSS, "
            + "and a strong bias for accessibility.",
            ["react", "typescript", "nextjs", "tailwind"],
            SeniorityLevel.Mid, Barcelona, WorkArrangement.Remote, Salary(45_000, 60_000)),

        Job("a0000004", "Data Engineer, Talent Analytics",
            "Model the recruitment funnel. dbt on top of PostgreSQL, Apache Spark for the heavy "
            + "aggregations, and Airflow-style orchestration.",
            ["postgresql", "dbt", "spark", "python"],
            SeniorityLevel.Senior, Lisbon, WorkArrangement.Remote, Salary(55_000, 72_000)),

        Job("a0000005", "Platform Engineer, Developer Experience",
            "Terraform, GitHub Actions and Helm. Make the deployment path boring so product teams "
            + "stop thinking about it.",
            ["terraform", "github-actions", "helm", "kubernetes", "aws"],
            SeniorityLevel.Senior, London, WorkArrangement.Hybrid, Salary(75_000, 95_000)),

        Job("a0000006", "Junior Backend Engineer",
            "First or second role. You will write C# against PostgreSQL with a senior engineer "
            + "reviewing everything, and grow into ownership.",
            ["csharp", "postgresql"],
            SeniorityLevel.Junior, Madrid, WorkArrangement.OnSite, Salary(30_000, 38_000)),

        Job("a0000007", "Principal Engineer, Identity",
            "Own authentication and authorization across the group. OAuth 2.1, Keycloak, and the "
            + "threat modelling that goes with holding candidate data.",
            ["oauth2", "keycloak", "threat-modeling", "dotnet"],
            SeniorityLevel.Principal, Amsterdam, WorkArrangement.Hybrid, Salary(110_000, 140_000)),

        Job("a0000008", "Mobile Engineer, Recruiter App",
            "Flutter across iOS and Android for the recruiter-facing app. Offline-first, because "
            + "recruiters work on trains.",
            ["flutter", "ios", "android"],
            SeniorityLevel.Mid, Berlin, WorkArrangement.Remote, Salary(55_000, 70_000)),

        Job("a0000009", "QA Automation Engineer",
            "Playwright end-to-end suites and k6 load profiles. You will be the person who finds "
            + "the race condition before a customer does.",
            ["playwright", "k6", "typescript"],
            SeniorityLevel.Mid, Barcelona, WorkArrangement.Hybrid, Salary(42_000, 55_000)),

        Job("a000000a", "Engineering Manager, Matching",
            "Lead the team that owns candidate-to-job matching. Still technical enough to review a "
            + "Python notebook, with mentoring and stakeholder work as the day job.",
            ["python", "mentoring", "stakeholder-management"],
            SeniorityLevel.Staff, Madrid, WorkArrangement.Hybrid, Salary(85_000, 105_000)),

        // No required skills, on purpose: exercises the "cannot discriminate" branch of the scorer,
        // which is a real posting shape and not a synthetic edge case.
        Job("a000000b", "Software Engineer, Generalist",
            "We care more about how you think than which framework you last used. Tell us what you "
            + "have built.",
            [],
            SeniorityLevel.Mid, London, WorkArrangement.Remote, SalaryRange.NotDisclosed),

        // Salary undisclosed and location unknown: the shape a scraped or partially-filled posting
        // has, which the tools must survive rather than assume away.
        Job("a000000c", "Security Engineer",
            "Application security across a .NET estate. OWASP top ten as a starting point, not a "
            + "checklist.",
            ["owasp", "dotnet", "threat-modeling"],
            SeniorityLevel.Senior, Location.Unknown, WorkArrangement.Unspecified, SalaryRange.NotDisclosed),
    ];

    /// <summary>
    /// Candidate profiles. A factory, for the same reason as <see cref="CreateJobs"/>.
    /// </summary>
    /// <returns>Freshly constructed candidate profiles.</returns>
    public static IReadOnlyList<Candidate> CreateCandidates() =>
    [
        // Perfect match for a0000001: every skill, same seniority, same city.
        Candidate("b0000001", "Ana Herrera", ["dotnet", "postgresql", "kubernetes", "kafka", "docker"],
            9, SeniorityLevel.Senior, Madrid, willingToRelocate: false),

        // Near-miss for a0000001: missing Kafka only. The interesting case for A1 to beat.
        Candidate("b0000002", "Bruno Silva", ["dotnet", "postgresql", "kubernetes"],
            7, SeniorityLevel.Senior, Madrid, willingToRelocate: false),

        // Right skills, wrong country, will not relocate: location component floors at zero.
        Candidate("b0000003", "Clara Nowak", ["go", "elasticsearch", "kubernetes"],
            8, SeniorityLevel.Staff, Lisbon, willingToRelocate: false),

        // Same as above but willing to move — the pair exists so the relocation branch is visible.
        Candidate("b0000004", "Diego Marín", ["go", "elasticsearch", "kubernetes"],
            8, SeniorityLevel.Staff, Lisbon, willingToRelocate: true),

        // Over-qualified: Principal applying to a Mid role.
        Candidate("b0000005", "Elena Rossi", ["react", "typescript", "nextjs", "tailwind"],
            14, SeniorityLevel.Principal, Barcelona, willingToRelocate: false),

        // Under-qualified: Junior against a Senior posting, but skills line up.
        Candidate("b0000006", "Farid Haddad", ["postgresql", "dbt", "python"],
            2, SeniorityLevel.Junior, Lisbon, willingToRelocate: true),

        Candidate("b0000007", "Grace Okonkwo", ["terraform", "github-actions", "helm", "kubernetes", "aws"],
            10, SeniorityLevel.Senior, London, willingToRelocate: false),

        Candidate("b0000008", "Hugo Lindqvist", ["csharp", "postgresql", "xunit"],
            1, SeniorityLevel.Junior, Madrid, willingToRelocate: false),

        Candidate("b0000009", "Ines Duarte", ["oauth2", "keycloak", "threat-modeling", "dotnet", "owasp"],
            15, SeniorityLevel.Principal, Amsterdam, willingToRelocate: false),

        Candidate("b000000a", "Jonas Weber", ["flutter", "ios", "android", "react-native"],
            6, SeniorityLevel.Mid, Berlin, willingToRelocate: false),

        Candidate("b000000b", "Katarzyna Lis", ["playwright", "k6", "typescript", "selenium"],
            5, SeniorityLevel.Mid, Barcelona, willingToRelocate: false),

        Candidate("b000000c", "Liam Byrne", ["python", "mentoring", "stakeholder-management", "spark"],
            12, SeniorityLevel.Staff, Madrid, willingToRelocate: false),

        // Seniority unstated: exercises the neutral-rather-than-matching branch.
        Candidate("b000000d", "Mei Tanaka", ["dotnet", "azure", "docker"],
            4, SeniorityLevel.Unspecified, London, willingToRelocate: true),

        // Location unknown: the shape an incomplete profile has.
        Candidate("b000000e", "Noah Fischer", ["java", "spring-boot", "kafka"],
            6, SeniorityLevel.Mid, Location.Unknown, willingToRelocate: false),

        // No overlap with anything: the honest zero, so a matcher that always finds something is
        // visibly wrong.
        Candidate("b000000f", "Olivia Grant", ["blazor", "maui"],
            3, SeniorityLevel.Junior, Berlin, willingToRelocate: true),

        // Already rejected. Seeded in that state so the destructive tool's idempotency and any
        // "exclude rejected" filtering have something real to run against.
        RejectedCandidate("b0000010", "Pedro Sousa", ["dotnet", "postgresql"],
            5, SeniorityLevel.Mid, Lisbon,
            "Withdrew from the process after the second interview.",
            DateTimeOffset.Parse("2026-08-01T09:30:00Z", System.Globalization.CultureInfo.InvariantCulture)),
    ];

    private static Job Job(
        string idSuffix,
        string title,
        string description,
        string[] requiredSkillIds,
        SeniorityLevel seniority,
        Location location,
        WorkArrangement arrangement,
        SalaryRange salary) =>
        new(Id(idSuffix), title, description, requiredSkillIds, seniority, location, arrangement, salary);

    private static Candidate Candidate(
        string idSuffix,
        string fullName,
        string[] skillIds,
        int yearsOfExperience,
        SeniorityLevel seniority,
        Location location,
        bool willingToRelocate) =>
        new(Id(idSuffix), fullName, skillIds, yearsOfExperience, seniority, location, willingToRelocate);

    private static Candidate RejectedCandidate(
        string idSuffix,
        string fullName,
        string[] skillIds,
        int yearsOfExperience,
        SeniorityLevel seniority,
        Location location,
        string reason,
        DateTimeOffset at)
    {
        var candidate = Candidate(idSuffix, fullName, skillIds, yearsOfExperience, seniority, location, false);

        // Through the domain method, not by setting fields: the seed is subject to the same invariant
        // as every other caller, so a seed cannot create a state the domain forbids.
        candidate.Reject(reason, at);

        return candidate;
    }

    private static Guid Id(string suffix) => Guid.Parse($"{suffix}-0000-0000-0000-000000000000");

    private static SalaryRange Salary(int minimum, int maximum) => new(minimum, maximum, "EUR");
}
