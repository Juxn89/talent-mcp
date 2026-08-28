namespace Talent.Mcp.Tests;

using Talent.Application.Configuration;
using Talent.Application.Ports;
using Talent.Application.UseCases;
using Talent.Domain.Entities;
using Talent.Domain.Enums;
using Talent.Domain.Scoring;
using Talent.Domain.ValueObjects;
using Xunit;

/// <summary>
/// Behaviour of the remaining use cases against fake ports, including the degraded paths — a repository
/// returning nothing, and a caller omitting a required argument.
/// </summary>
public sealed class UseCaseBehaviourTests
{
    private static readonly Guid JobId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid CandidateId = Guid.Parse("22222222-2222-2222-2222-222222222222");

    [Fact]
    public async Task Scoring_returns_an_explainable_score()
    {
        var useCase = new ScoreCandidateFitUseCase(
            new FakeJobs(MakeJob()), new FakeCandidates(MakeCandidate()), new TalentOptions());

        var (score, failure) = await useCase.ExecuteAsync(CandidateId, JobId);

        Assert.Equal(ScoreCandidateFitFailure.None, failure);
        Assert.Equal(100, score!.Total);
        Assert.Equal(3, score.Components.Count);
        Assert.All(score.Components, c => Assert.NotEqual(ScoreReason.None, c.Reason));
    }

    [Fact]
    public async Task Scoring_reports_which_id_was_missing()
    {
        // Reported specifically, unlike a bad handle: a caller with a typo is not an attacker, and
        // "which of my two ids was wrong" is the only useful answer.
        var noJob = new ScoreCandidateFitUseCase(
            new FakeJobs(null), new FakeCandidates(MakeCandidate()), new TalentOptions());
        var noCandidate = new ScoreCandidateFitUseCase(
            new FakeJobs(MakeJob()), new FakeCandidates(null), new TalentOptions());

        Assert.Equal(ScoreCandidateFitFailure.JobNotFound, (await noJob.ExecuteAsync(CandidateId, JobId)).Failure);
        Assert.Equal(ScoreCandidateFitFailure.CandidateNotFound, (await noCandidate.ExecuteAsync(CandidateId, JobId)).Failure);
    }

    [Fact]
    public async Task Scoring_honours_configured_weights()
    {
        // Location-only weighting, and the candidate is in a different country with no relocation, so
        // the total must be 0 even though the skills match perfectly. Proves the configured weights
        // reach the domain rather than the default being used silently.
        var options = new TalentOptions { ScoringWeights = new ScoringWeights(0, 0, 1) };
        var useCase = new ScoreCandidateFitUseCase(
            new FakeJobs(MakeJob(arrangement: WorkArrangement.OnSite)),
            new FakeCandidates(MakeCandidate(new Location("Berlin", "DE"))),
            options);

        var (score, _) = await useCase.ExecuteAsync(CandidateId, JobId);

        Assert.Equal(0, score!.Total);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("no")]
    [InlineData("too short")]
    public async Task Rejecting_without_an_adequate_reason_is_refused(string? reason)
    {
        var candidates = new FakeCandidates(MakeCandidate());
        var useCase = new RejectCandidateUseCase(candidates);

        var failure = await useCase.ExecuteAsync(CandidateId, reason);

        Assert.Equal(RejectCandidateFailure.ReasonRequired, failure);

        // And crucially: nothing was destroyed on the way to finding that out.
        Assert.Empty(candidates.Rejections);
    }

    [Fact]
    public async Task Rejecting_with_a_reason_records_it_trimmed()
    {
        var candidates = new FakeCandidates(MakeCandidate());
        var useCase = new RejectCandidateUseCase(candidates);

        var failure = await useCase.ExecuteAsync(CandidateId, "   Insufficient Kubernetes experience   ");

        Assert.Equal(RejectCandidateFailure.None, failure);
        Assert.Equal(("Insufficient Kubernetes experience", CandidateId), candidates.Rejections[0]);
    }

    [Fact]
    public async Task Rejecting_an_absent_candidate_reports_not_found()
    {
        var useCase = new RejectCandidateUseCase(new FakeCandidates(null));

        var failure = await useCase.ExecuteAsync(CandidateId, "Withdrew from the process");

        Assert.Equal(RejectCandidateFailure.CandidateNotFound, failure);
    }

    [Fact]
    public async Task The_reason_check_runs_before_the_lookup()
    {
        // Ordering is deliberate: a caller who forgot the reason is told that, not told whether the
        // id exists. The negative case leaks less.
        var candidates = new FakeCandidates(null);
        var useCase = new RejectCandidateUseCase(candidates);

        var failure = await useCase.ExecuteAsync(CandidateId, "");

        Assert.Equal(RejectCandidateFailure.ReasonRequired, failure);
        Assert.Equal(0, candidates.RejectCallCount);
    }

    [Fact]
    public void Extracting_from_text_finds_skills_and_reports_nothing_unrecognised()
    {
        var result = new ExtractSkillsUseCase().FromText("We need .NET, Kubernetes and some Rust.");

        Assert.Equal(["dotnet", "kubernetes", "rust"], result.Skills.Select(s => s.Id));
        Assert.Empty(result.UnrecognisedNames);
    }

    [Fact]
    public void Extracting_from_names_reports_what_it_dropped()
    {
        var result = new ExtractSkillsUseCase().FromNames(["k8s", "COBOL", "postgres"]);

        Assert.Equal(["kubernetes", "postgresql"], result.Skills.Select(s => s.Id));
        Assert.Equal(["COBOL"], result.UnrecognisedNames);
    }

    [Fact]
    public void Options_validation_catches_incoherent_configuration()
    {
        Assert.False(new TalentOptions { DefaultPageSize = 0 }.TryValidate(out _));
        Assert.False(new TalentOptions { DefaultPageSize = 50, MaxPageSize = 10 }.TryValidate(out _));
        Assert.False(new TalentOptions { PaginationHandleTimeToLive = TimeSpan.Zero }.TryValidate(out _));
        Assert.False(new TalentOptions { MaxShortlistSize = 0 }.TryValidate(out _));
        Assert.False(new TalentOptions { ScoringWeights = new ScoringWeights(0.5, 0.25, 0.15) }.TryValidate(out _));

        Assert.True(new TalentOptions().TryValidate(out var error));
        Assert.Null(error);
    }

    private static Job MakeJob(WorkArrangement arrangement = WorkArrangement.Remote) =>
        new(JobId, "Senior Backend Engineer", "Build things.", ["dotnet"], SeniorityLevel.Senior,
            new Location("Madrid", "ES"), arrangement, SalaryRange.NotDisclosed);

    private static Candidate MakeCandidate(Location? location = null) =>
        new(CandidateId, "Ada Recruiter", ["dotnet"], 8, SeniorityLevel.Senior,
            location ?? new Location("Madrid", "ES"), willingToRelocate: false);

    private sealed class FakeJobs(Job? job) : IJobRepository
    {
        public Task<Job?> FindByIdAsync(Guid id, CancellationToken cancellationToken = default) =>
            Task.FromResult(job);

        public Task<JobPage> SearchAsync(JobSearchCriteria criteria, CancellationToken cancellationToken = default) =>
            Task.FromResult(new JobPage([], 0, null));
    }

    private sealed class FakeCandidates(Candidate? candidate) : ICandidateRepository
    {
        public List<(string Reason, Guid Id)> Rejections { get; } = [];

        public int RejectCallCount { get; private set; }

        public Task<Candidate?> FindByIdAsync(Guid id, CancellationToken cancellationToken = default) =>
            Task.FromResult(candidate);

        public Task<IReadOnlyList<Candidate>> FindByIdsAsync(
            IReadOnlyList<Guid> ids, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<Candidate>>(candidate is null ? [] : [candidate]);

        public Task<bool> RejectAsync(Guid id, string reason, CancellationToken cancellationToken = default)
        {
            this.RejectCallCount++;

            if (candidate is null)
            {
                return Task.FromResult(false);
            }

            this.Rejections.Add((reason, id));
            return Task.FromResult(true);
        }
    }
}
