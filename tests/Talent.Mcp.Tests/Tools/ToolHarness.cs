namespace Talent.Mcp.Tests.Tools;

using System.IO.Pipelines;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Client;
using ModelContextProtocol.Extensions.Tasks;
using ModelContextProtocol.Protocol;
using Talent.Application.Configuration;
using Talent.Application.Ports;
using Talent.Domain.Entities;
using Talent.Domain.Enums;
using Talent.Domain.ValueObjects;
using Talent.Infrastructure.Handles;
using Talent.Mcp.Toolkit;
using Talent.Mcp.Tools;
using Talent.Mcp.Tools.Constants;

/// <summary>
/// A real MCP server and a real MCP client talking to each other over a pair of in-memory pipes.
/// <para>
/// The SDK ships no in-memory transport, so this crosses two <see cref="Pipe"/>s: the client writes
/// into the stream the server reads, and reads from the stream the server writes. Everything above the
/// wire is genuine — JSON-RPC framing, schema generation, argument binding, the DI scope per request —
/// which is the point. A test that called the tool method directly would prove nothing about
/// <c>inputSchema</c>, about header binding, or about whether a parameter got bound from arguments
/// instead of from services.
/// </para>
/// <para>
/// The repositories are fakes; the transport, the server and the client are not.
/// </para>
/// </summary>
internal sealed class ToolHarness : IAsyncDisposable
{
    /// <summary>Labelled test key. Long enough for the codec, and obviously not a secret.</summary>
    private static readonly byte[] SigningKey =
        System.Text.Encoding.UTF8.GetBytes("talent-mcp-unit-test-signing-key!");

    private readonly IHost host;
    private readonly SignedHandleCodec codec;

    private ToolHarness(IHost host, McpClient client, SignedHandleCodec codec)
    {
        this.host = host;
        this.Client = client;
        this.codec = codec;
    }

    /// <summary>The connected client.</summary>
    public McpClient Client { get; }

    /// <summary>Starts a server over in-memory pipes and connects a client to it.</summary>
    /// <param name="jobs">Job repository fake.</param>
    /// <param name="candidates">Candidate repository fake.</param>
    /// <param name="options">Tunables, defaulted when omitted.</param>
    /// <param name="timeProvider">Clock for handle expiry, defaulted when omitted.</param>
    /// <param name="elicitationHandler">
    /// How the client answers an MRTR elicitation. The SDK client drives the whole round-trip itself
    /// when this is set — it sees <c>input_required</c>, calls this, and retries with
    /// <c>inputResponses</c> and the server's <c>requestState</c>. Leaving it null makes the client
    /// throw on an elicitation, which is what an MRTR-capable client with nobody to ask does.
    /// </param>
    /// <param name="clientProtocolVersion">
    /// Protocol revision the client declares. Set it to an older revision to get a client whose
    /// <c>IsMrtrSupported</c> is false — the real degraded case, rather than a fabricated capability.
    /// </param>
    /// <returns>A started harness.</returns>
    public static async Task<ToolHarness> StartAsync(
        IJobRepository? jobs = null,
        ICandidateRepository? candidates = null,
        TalentOptions? options = null,
        TimeProvider? timeProvider = null,
        Func<ElicitRequestParams?, CancellationToken, ValueTask<ElicitResult>>? elicitationHandler = null,
        string? clientProtocolVersion = null)
    {
        var clientToServer = new Pipe();
        var serverToClient = new Pipe();

        var builder = Host.CreateApplicationBuilder();
        builder.Logging.ClearProviders();

        // Opt-in server-side logging. Needed because the SDK strips the message from any exception that
        // is not an McpException, so a tool failing for an unexpected reason arrives as
        // "An error occurred invoking '<tool>'." and nothing else. HARNESS_LOG=1 shows what it was.
        if (Environment.GetEnvironmentVariable("HARNESS_LOG") == "1")
        {
            builder.Logging.AddConsole();
        }

        var codec = new SignedHandleCodec(new HandleCodec(SigningKey, timeProvider), ownsCodec: true);

        builder.Services.AddSingleton(options ?? new TalentOptions());
        builder.Services.AddSingleton(jobs ?? new FakeJobRepository([]));
        builder.Services.AddSingleton(candidates ?? new FakeCandidateRepository([]));
        builder.Services.AddSingleton<IHandleCodec>(codec);

        builder.Services
            .AddMcpServer(serverOptions => serverOptions.ServerInfo = TalentServerInfo.Value)
            .WithStreamServerTransport(
                clientToServer.Reader.AsStream(),
                serverToClient.Writer.AsStream())
            .AddTalentTools(new InMemoryMcpTaskStore());

        var host = builder.Build();
        await host.StartAsync().ConfigureAwait(false);

        var transport = new StreamClientTransport(
            serverInput: clientToServer.Writer.AsStream(),
            serverOutput: serverToClient.Reader.AsStream());

        var clientOptions = new McpClientOptions();

        if (elicitationHandler is not null)
        {
            clientOptions.Handlers.ElicitationHandler = elicitationHandler;
        }

        if (clientProtocolVersion is not null)
        {
            clientOptions.ProtocolVersion = clientProtocolVersion;
        }

        var client = await McpClient.CreateAsync(transport, clientOptions).ConfigureAwait(false);

        return new ToolHarness(host, client, codec);
    }

    /// <summary>Calls a tool and returns its structured content as a <see cref="JsonElement"/>.</summary>
    /// <param name="toolName">Wire name of the tool.</param>
    /// <param name="arguments">Arguments to send.</param>
    /// <returns>The result, so a test can assert on either the payload or the error flag.</returns>
    public async Task<CallToolResult> CallAsync(
        string toolName,
        IReadOnlyDictionary<string, object?>? arguments = null)
    {
        return await this.Client
            .CallToolAsync(toolName, arguments)
            .ConfigureAwait(false);
    }

    /// <summary>Calls <c>search_jobs</c> and returns its structured content.</summary>
    /// <param name="query">Free-text query.</param>
    /// <param name="requiredSkillIds">Skill filter.</param>
    /// <param name="countryCode">Country filter.</param>
    /// <param name="arrangement">Arrangement filter.</param>
    /// <param name="pageSize">Page size.</param>
    /// <param name="pageHandle">Continuation handle.</param>
    /// <returns>The structured payload.</returns>
    public async Task<JsonElement> SearchAsync(
        string? query = null,
        string[]? requiredSkillIds = null,
        string? countryCode = null,
        WorkArrangement? arrangement = null,
        int? pageSize = null,
        string? pageHandle = null)
    {
        var arguments = new Dictionary<string, object?>();

        // Only non-null arguments are sent. Sending explicit nulls would test a shape no client
        // produces, and would hide whether the schema's defaults actually apply.
        if (query is not null) { arguments["query"] = query; }
        if (requiredSkillIds is not null) { arguments["requiredSkillIds"] = requiredSkillIds; }
        if (countryCode is not null) { arguments["countryCode"] = countryCode; }
        if (arrangement is not null) { arguments["arrangement"] = arrangement.Value.ToString(); }
        if (pageSize is not null) { arguments["pageSize"] = pageSize.Value; }
        if (pageHandle is not null) { arguments["pageHandle"] = pageHandle; }

        var result = await this.CallAsync(Mcp.ToolNames.SearchJobs, arguments).ConfigureAwait(false);

        return StructuredOf(result);
    }

    /// <summary>
    /// Sends a hand-built <c>tools/call</c>, bypassing the client's own MRTR loop.
    /// <para>
    /// Needed because the SDK client drives the confirmation round-trip itself: it intercepts an
    /// <c>input_required</c> result, calls the elicitation handler and retries, so a test can never
    /// see the first leg or control the second. Building the retry by hand is the only way to assert
    /// what happens when a client sends a confirmation with arguments that disagree with the signed
    /// state.
    /// </para>
    /// </summary>
    /// <param name="parameters">The full call parameters, including inputResponses and requestState.</param>
    /// <returns>The result.</returns>
    public async Task<CallToolResult> CallRawAsync(CallToolRequestParams parameters)
    {
        return await this.Client
            .SendRequestAsync<CallToolRequestParams, CallToolResult>("tools/call", parameters)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Calls a tool as an MCP task and runs it to completion.
    /// <para>
    /// <c>CallToolAsTaskAsync</c> is what makes the request declare the Tasks extension — the SDK client
    /// sets <c>_meta/clientCapabilities/extensions/io.modelcontextprotocol/tasks</c> on the outgoing
    /// request itself, matching what a real task-capable client sends. Polling stops as soon as the task
    /// leaves <see cref="McpTaskStatus.Working"/>, so a test that expects a terminal state other than
    /// <see cref="McpTaskStatus.Completed"/> reads <see cref="GetTaskResult.Status"/> off the return
    /// value rather than assuming success.
    /// </para>
    /// </summary>
    /// <param name="toolName">Wire name of the tool.</param>
    /// <param name="arguments">Arguments to send.</param>
    /// <returns>The task's terminal state.</returns>
    /// <exception cref="InvalidOperationException">The call was not accepted as a task at all.</exception>
    public async Task<GetTaskResult> RunAsTaskToCompletionAsync(
        string toolName,
        IReadOnlyDictionary<string, object?>? arguments = null)
    {
        var jsonArguments = (arguments ?? new Dictionary<string, object?>())
            .ToDictionary(
                static kvp => kvp.Key,
                static kvp => JsonSerializer.SerializeToElement(kvp.Value),
                StringComparer.Ordinal);

        var created = await this.Client
            .CallToolAsTaskAsync(new CallToolRequestParams { Name = toolName, Arguments = jsonArguments })
            .ConfigureAwait(false);

        if (!created.IsTask)
        {
            throw new InvalidOperationException(
                $"{toolName} did not run as a task. This harness only calls task-required tools this way.");
        }

        var taskId = created.TaskCreated!.TaskId;

        while (true)
        {
            var status = await this.Client.GetTaskAsync(taskId).ConfigureAwait(false);

            if (status.Status is not McpTaskStatus.Working)
            {
                return status;
            }

            await Task.Delay(20).ConfigureAwait(false);
        }
    }

    /// <summary>Returns a successful call's structured content, failing the call if it errored.</summary>
    /// <param name="result">The call result.</param>
    /// <returns>The structured payload.</returns>
    /// <exception cref="InvalidOperationException">The call reported an error or carried no payload.</exception>
    public static JsonElement StructuredOf(CallToolResult result)
    {
        ArgumentNullException.ThrowIfNull(result);

        if (result.IsError is true)
        {
            throw new InvalidOperationException("The tool call failed: " + TextOfCore(result));
        }

        return result.StructuredContent
            ?? throw new InvalidOperationException(
                "The tool returned no structuredContent. UseStructuredContent defaults to false on "
                + "McpServerToolAttribute — measured 1 Sep 2026, SDK 2.2.0 — so a typed return lands in "
                + "text content only until it is set.");
    }

    /// <summary>Returns the text content of a result, for asserting on error messages.</summary>
    /// <param name="result">The call result.</param>
    /// <returns>The concatenated text blocks.</returns>
    public string TextOf(CallToolResult result) => TextOfCore(result);

    /// <summary>Verifies and reads a handle, as the server would.</summary>
    /// <typeparam name="TPayload">Expected payload type.</typeparam>
    /// <param name="handle">The handle.</param>
    /// <param name="payload">The payload when authentic.</param>
    /// <returns>Whether the handle was authentic and unexpired.</returns>
    public bool TryRead<TPayload>(string? handle, out TPayload? payload)
        where TPayload : notnull
        => this.codec.TryRead(handle, out payload);

    /// <summary>Mints a handle exactly as the server would, for tests that need a valid one up front.</summary>
    /// <typeparam name="TPayload">Payload type.</typeparam>
    /// <param name="payload">Payload to carry.</param>
    /// <param name="timeToLive">Lifetime.</param>
    /// <returns>The handle.</returns>
    public string Mint<TPayload>(TPayload payload, TimeSpan timeToLive)
        where TPayload : notnull
        => this.codec.Mint(payload, timeToLive);

    private static string TextOfCore(CallToolResult result)
    {
        ArgumentNullException.ThrowIfNull(result);

        return string.Join(
            Environment.NewLine,
            result.Content.OfType<TextContentBlock>().Select(static block => block.Text));
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        await this.Client.DisposeAsync().ConfigureAwait(false);
        await this.host.StopAsync().ConfigureAwait(false);
        this.host.Dispose();
        this.codec.Dispose();
    }
}

/// <summary>An in-memory job repository with the same ordering guarantee the EF one provides.</summary>
internal sealed class FakeJobRepository(IReadOnlyList<Job> jobs) : IJobRepository
{
    /// <summary>How many times <see cref="SearchAsync"/> was called, for tests about handle reuse.</summary>
    public List<JobSearchCriteria> Searches { get; } = [];

    /// <inheritdoc />
    public Task<Job?> FindByIdAsync(Guid id, CancellationToken cancellationToken = default) =>
        Task.FromResult(jobs.FirstOrDefault(j => j.Id == id));

    /// <inheritdoc />
    public Task<JobPage> SearchAsync(JobSearchCriteria criteria, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(criteria);
        this.Searches.Add(criteria);

        // Total order by title then id, matching EfJobRepository. Without it a page boundary means
        // something different on each call and handle pagination skips or repeats rows.
        var matched = jobs
            .Where(j => criteria.Query.Length == 0
                || j.Title.Contains(criteria.Query, StringComparison.OrdinalIgnoreCase))
            .Where(j => criteria.CountryCode.Length == 0
                || string.Equals(j.Location.CountryCode, criteria.CountryCode, StringComparison.OrdinalIgnoreCase))
            .Where(j => criteria.Arrangement == WorkArrangement.Unspecified
                || j.Arrangement == criteria.Arrangement)
            .Where(j => criteria.RequiredSkillIds.All(s => j.RequiredSkillIds.Contains(s, StringComparer.Ordinal)))
            .OrderBy(j => j.Title, StringComparer.Ordinal)
            .ThenBy(j => j.Id)
            .ToArray();

        var page = matched.Skip(criteria.Skip).Take(criteria.Take).ToArray();
        var consumed = criteria.Skip + page.Length;
        int? nextSkip = consumed < matched.Length ? consumed : null;

        return Task.FromResult(new JobPage(page, matched.Length, nextSkip));
    }
}

/// <summary>An in-memory candidate repository.</summary>
internal sealed class FakeCandidateRepository(IReadOnlyList<Candidate> candidates) : ICandidateRepository
{
    /// <inheritdoc />
    public Task<Candidate?> FindByIdAsync(Guid id, CancellationToken cancellationToken = default) =>
        Task.FromResult(candidates.FirstOrDefault(c => c.Id == id));

    /// <inheritdoc />
    public Task<IReadOnlyList<Candidate>> FindByIdsAsync(
        IReadOnlyList<Guid> ids,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(ids);

        IReadOnlyList<Candidate> found = candidates
            .Where(c => ids.Contains(c.Id))
            .OrderBy(c => c.Id)
            .ToArray();

        return Task.FromResult(found);
    }

    /// <inheritdoc />
    public Task<bool> RejectAsync(Guid id, string reason, CancellationToken cancellationToken = default) =>
        Task.FromResult(candidates.Any(c => c.Id == id));
}

/// <summary>Deterministic entities the tool tests assert against.</summary>
internal static class ToolTestData
{
    /// <summary>A backend posting in Madrid, hybrid.</summary>
    public static Job MadridBackend { get; } = new(
        Guid.Parse("11111111-1111-1111-1111-111111111111"),
        "Backend Engineer",
        "Build and operate the payments API.",
        ["csharp", "postgresql"],
        SeniorityLevel.Senior,
        new Location("Madrid", "ES"),
        WorkArrangement.Hybrid,
        new SalaryRange(60000, 80000, "EUR"));

    /// <summary>A remote posting in Berlin, so region filtering has something to exclude.</summary>
    public static Job BerlinPlatform { get; } = new(
        Guid.Parse("22222222-2222-2222-2222-222222222222"),
        "Platform Engineer",
        "Own the delivery platform.",
        ["kubernetes", "terraform"],
        SeniorityLevel.Staff,
        new Location("Berlin", "DE"),
        WorkArrangement.Remote,
        SalaryRange.NotDisclosed);

    /// <summary>A candidate who overlaps the Madrid posting exactly.</summary>
    public static Candidate MadridSenior { get; } = new(
        Guid.Parse("33333333-3333-3333-3333-333333333333"),
        "Ada Lovelace",
        ["csharp", "postgresql"],
        8,
        SeniorityLevel.Senior,
        new Location("Madrid", "ES"),
        willingToRelocate: false);

    /// <summary>
    /// <paramref name="count"/> candidates, half overlapping the Madrid posting's skills and half not —
    /// so a shortlist scoring run has both matches and near-misses to sort between.
    /// </summary>
    public static IReadOnlyList<Candidate> ManyCandidates(int count) =>
        Enumerable.Range(1, count)
            .Select(i => new Candidate(
                Guid.Parse($"55555555-5555-5555-5555-{i:D12}"),
                $"Candidate {i:D2}",
                i % 2 == 0 ? ["csharp", "postgresql"] : ["java"],
                i,
                SeniorityLevel.Mid,
                new Location("Madrid", "ES"),
                willingToRelocate: false))
            .ToArray();

    /// <summary>Twelve postings with distinct sortable titles, for pagination tests.</summary>
    public static IReadOnlyList<Job> Many(int count) =>
        Enumerable.Range(1, count)
            .Select(i => new Job(
                Guid.Parse($"00000000-0000-0000-0000-{i:D12}"),
                $"Engineer {i:D2}",
                $"Posting number {i}.",
                ["csharp"],
                SeniorityLevel.Mid,
                new Location("Madrid", "ES"),
                WorkArrangement.Remote,
                new SalaryRange(50000, 60000, "EUR")))
            .ToArray();
}
