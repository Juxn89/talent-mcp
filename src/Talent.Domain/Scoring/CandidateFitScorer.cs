namespace Talent.Domain.Scoring;

using System.Globalization;
using Talent.Domain.Entities;
using Talent.Domain.Enums;
using Talent.Domain.Skills;

/// <summary>
/// Scores how well a candidate fits a job, deterministically and with a per-component explanation.
/// <para>
/// A pure function: same inputs, same output, no clock, no randomness, no I/O, no LLM. That is what
/// makes it free to run, testable in milliseconds without Docker, and usable as the baseline A1's
/// RAG matching has to beat.
/// </para>
/// </summary>
public static class CandidateFitScorer
{
    /// <summary>
    /// The seniority gap, in ladder steps, at which the seniority component reaches zero. Three steps
    /// is the span from <see cref="SeniorityLevel.Junior"/> to <see cref="SeniorityLevel.Staff"/> —
    /// far enough apart that the match is not worth arguing about.
    /// </summary>
    private const int MaxSeniorityDistance = 3;

    /// <summary>
    /// Penalty applied per step when the candidate is <em>more</em> senior than the role asks for.
    /// Lower than the under-qualified penalty on purpose: a senior person can do a mid-level job,
    /// while the reverse is the risk a recruiter is actually screening for.
    /// </summary>
    private const double OverqualifiedPenaltyPerStep = 0.15;

    /// <summary>Penalty applied per step when the candidate is less senior than the role asks for.</summary>
    private const double UnderqualifiedPenaltyPerStep = 1.0 / MaxSeniorityDistance;

    /// <summary>Scores a candidate against a job using <see cref="ScoringWeights.Default"/>.</summary>
    /// <param name="candidate">The candidate.</param>
    /// <param name="job">The job.</param>
    /// <returns>An explainable score.</returns>
    public static FitScore Score(Candidate candidate, Job job) =>
        Score(candidate, job, ScoringWeights.Default);

    /// <summary>Scores a candidate against a job.</summary>
    /// <param name="candidate">The candidate.</param>
    /// <param name="job">The job.</param>
    /// <param name="weights">Component weighting. Must satisfy <see cref="ScoringWeights.IsValid"/>.</param>
    /// <returns>An explainable score.</returns>
    /// <exception cref="ArgumentNullException">A required argument was <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">The weights were not valid.</exception>
    public static FitScore Score(Candidate candidate, Job job, ScoringWeights weights)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        ArgumentNullException.ThrowIfNull(job);
        ArgumentNullException.ThrowIfNull(weights);

        if (!weights.IsValid())
        {
            throw new ArgumentException(
                $"Scoring weights must be non-negative and sum to 1; got {weights.Total.ToString("R", CultureInfo.InvariantCulture)}.",
                nameof(weights));
        }

        // Ordering is fixed, not incidental: clients and tests read the breakdown positionally as
        // well as by name, and a reordered breakdown would be a silent contract change.
        ScoreComponent[] components =
        [
            ScoreSkillOverlap(candidate, job, weights.SkillOverlap),
            ScoreSeniorityDistance(candidate, job, weights.SeniorityDistance),
            ScoreLocationCompatibility(candidate, job, weights.LocationCompatibility),
        ];

        var total = components.Sum(static c => c.WeightedScore) * FitScore.MaxTotal;

        return new FitScore(Math.Round(total, 2, MidpointRounding.AwayFromZero), components);
    }

    private static ScoreComponent ScoreSkillOverlap(Candidate candidate, Job job, double weight)
    {
        if (job.RequiredSkillIds.Count == 0)
        {
            // Not a perfect match and not a failure: the job simply gave the component nothing to
            // measure. Scoring it 1 would flatter every candidate; scoring it 0 would punish them
            // for the posting's omission. Neutral is the honest answer.
            return new ScoreComponent(
                FitScore.SkillOverlapComponent,
                RawScore: 0.5,
                weight,
                ScoreReason.NoSkillsRequired,
                "The posting lists no required skills, so this component cannot discriminate.");
        }

        var candidateSkills = new HashSet<string>(candidate.SkillIds, StringComparer.Ordinal);
        var matched = job.RequiredSkillIds.Where(candidateSkills.Contains).ToArray();
        var missing = job.RequiredSkillIds.Where(id => !candidateSkills.Contains(id)).ToArray();

        var raw = (double)matched.Length / job.RequiredSkillIds.Count;

        var reason = matched.Length switch
        {
            0 => ScoreReason.NoRequiredSkillsCovered,
            var m when m == job.RequiredSkillIds.Count => ScoreReason.AllRequiredSkillsCovered,
            _ => ScoreReason.SomeRequiredSkillsMissing,
        };

        var detail = missing.Length == 0
            ? $"Covers all {matched.Length} required skills."
            : $"Covers {matched.Length} of {job.RequiredSkillIds.Count} required skills; missing: {DescribeSkills(missing)}.";

        return new ScoreComponent(FitScore.SkillOverlapComponent, raw, weight, reason, detail);
    }

    private static ScoreComponent ScoreSeniorityDistance(Candidate candidate, Job job, double weight)
    {
        if (candidate.Seniority == SeniorityLevel.Unspecified || job.Seniority == SeniorityLevel.Unspecified)
        {
            return new ScoreComponent(
                FitScore.SeniorityDistanceComponent,
                RawScore: 0.5,
                weight,
                ScoreReason.SeniorityUnknown,
                "Seniority was not stated on both sides, so this component cannot discriminate.");
        }

        var steps = (int)candidate.Seniority - (int)job.Seniority;

        if (steps == 0)
        {
            return new ScoreComponent(
                FitScore.SeniorityDistanceComponent,
                RawScore: 1.0,
                weight,
                ScoreReason.SeniorityExactMatch,
                $"Both are {job.Seniority}.");
        }

        var overqualified = steps > 0;
        var distance = Math.Min(Math.Abs(steps), MaxSeniorityDistance);
        var penaltyPerStep = overqualified ? OverqualifiedPenaltyPerStep : UnderqualifiedPenaltyPerStep;
        var raw = Math.Max(0, 1.0 - (distance * penaltyPerStep));

        var reason = overqualified ? ScoreReason.CandidateOverqualified : ScoreReason.CandidateUnderqualified;
        var direction = overqualified ? "above" : "below";
        var detail =
            $"Candidate is {candidate.Seniority}, the role targets {job.Seniority}: {distance} step(s) {direction}.";

        return new ScoreComponent(FitScore.SeniorityDistanceComponent, raw, weight, reason, detail);
    }

    private static ScoreComponent ScoreLocationCompatibility(Candidate candidate, Job job, double weight)
    {
        if (job.Arrangement == WorkArrangement.Remote)
        {
            return new ScoreComponent(
                FitScore.LocationCompatibilityComponent,
                RawScore: 1.0,
                weight,
                ScoreReason.RemoteRoleLocationIrrelevant,
                "The role is remote, so location does not constrain the match.");
        }

        var jobLocation = job.Location;
        var candidateLocation = candidate.Location;

        if (candidateLocation.IsSameCityAs(jobLocation))
        {
            return new ScoreComponent(
                FitScore.LocationCompatibilityComponent,
                RawScore: 1.0,
                weight,
                ScoreReason.SameCity,
                $"Both are in {jobLocation}.");
        }

        if (candidateLocation.IsSameCountryAs(jobLocation))
        {
            // Same country, different city. Hybrid roles need regular presence, so a domestic move or
            // commute is a real cost; on-site is the same cost. Scored equally rather than inventing
            // a distance model this domain has no data for.
            return new ScoreComponent(
                FitScore.LocationCompatibilityComponent,
                RawScore: 0.6,
                weight,
                ScoreReason.SameCountry,
                $"Same country, different city: candidate in {candidateLocation}, role in {jobLocation}.");
        }

        return candidate.WillingToRelocate
            ? new ScoreComponent(
                FitScore.LocationCompatibilityComponent,
                RawScore: 0.4,
                weight,
                ScoreReason.DifferentCountryWillRelocate,
                $"Different country, but the candidate will relocate: {candidateLocation} to {jobLocation}.")
            : new ScoreComponent(
                FitScore.LocationCompatibilityComponent,
                RawScore: 0.0,
                weight,
                ScoreReason.DifferentCountryNoRelocation,
                $"Different country and the candidate will not relocate: {candidateLocation} to {jobLocation}.");
    }

    /// <summary>
    /// Renders skill ids using their taxonomy display names, so an explanation reads ".NET, Docker"
    /// rather than "dotnet, docker".
    /// </summary>
    private static string DescribeSkills(IEnumerable<string> skillIds) =>
        string.Join(", ", skillIds.Select(static id => SkillTaxonomy.FindById(id)?.DisplayName ?? id));
}
