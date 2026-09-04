namespace Talent.Mcp.Tools;

using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Extensions.Tasks;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Talent.Application.Ports;
using Talent.Application.Services;
using Talent.Application.UseCases;
using Talent.Mcp.Toolkit.Caching;
using Talent.Mcp.Toolkit.Tracing;
using Talent.Mcp.Tools.Constants;
using Talent.Mcp.Tools.Tools;

/// <summary>
/// Registers the recruitment tool surface on an MCP server builder.
/// <para>
/// Both hosts call this and nothing else, which is what makes "one server, two transports" structural
/// rather than aspirational: the Streamable HTTP host and the stdio host cannot drift apart in which
/// tools they publish or in what order. See ADR-0004.
/// </para>
/// </summary>
public static class TalentTools
{
    /// <summary>Registers the tool types, the use cases behind them and the list-response cache policy.</summary>
    /// <param name="builder">The MCP server builder.</param>
    /// <param name="taskStore">
    /// The task store backing <c>bulk_score_shortlist</c>. Constructed and started by the host, not
    /// here: it needs a connection string from configuration, which only the host's composition root
    /// reads, and starting its cross-node listener (ADR-0003) is a lifecycle decision that belongs with
    /// whoever owns the process, not with tool registration.
    /// </param>
    /// <returns>The same builder, for chaining.</returns>
    /// <exception cref="ArgumentNullException">A required argument was <see langword="null"/>.</exception>
    public static IMcpServerBuilder AddTalentTools(this IMcpServerBuilder builder, IMcpTaskStore taskStore)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(taskStore);

        AddUseCases(builder.Services);

        // One WithTools<T>() call per type, never WithToolsFromAssembly(). ADR-0002 measured what the
        // scan does under trimming: the server starts, logs nothing wrong, and answers tools/list with
        // -32601.
        return builder
            .WithTools<SearchJobsTool>()
            .WithTools<GetJobTool>()
            .WithTools<ExtractSkillsTool>()
            .WithTools<ScoreCandidateFitTool>()
            .WithTools<RejectCandidateTool>()
            .WithTools<BulkScoreShortlistTool>()
            .WithTasks(taskStore, options => options.ExecutionModeSelector = SelectExecutionMode)
            .WithRequestFilters(static filters => filters.AddListToolsFilter(static next =>
                async (request, cancellationToken) =>
                {
                    // A filter rather than WithListToolsHandler: the handler would replace the SDK's
                    // own enumeration of the registered tools, which is the part that generates
                    // inputSchema. All this needs to do is post-process the result.
                    var result = await next(request, cancellationToken).ConfigureAwait(false);
                    result.Tools = OrderCanonically(result.Tools);
                    return CachePolicies.ToolsList.ApplyTo(result);
                }))
            // F4: one span per tool call, on both hosts. AddCallToolFilter cannot do this — it never
            // fires for a tool that IS registered — so this uses the lower-level message-filter
            // pipeline instead. See ToolExecutionTelemetry's own doc comment, and ADR-0006, for why it
            // takes both an incoming and an outgoing filter and how they correlate.
            .WithMessageFilters(static filters => filters
                .AddIncomingFilter(ToolExecutionTelemetry.Incoming)
                .AddOutgoingFilter(ToolExecutionTelemetry.Outgoing));
    }

    /// <summary>
    /// Chooses which tools run as MCP tasks.
    /// <para>
    /// Only <c>bulk_score_shortlist</c> requires <see cref="McpTaskExecutionMode.Required"/> — it is the
    /// one operation in the surface sized to actually take a while (up to
    /// <see cref="Talent.Application.Configuration.TalentOptions.MaxShortlistSize"/> candidates), and
    /// requiring the client to opt in is what makes the demonstration mean something. Every other tool
    /// is <see cref="McpTaskExecutionMode.Synchronous"/>: they are fast, and a client that asked one of
    /// them to run as a task and then had to poll for a result nobody was waiting on would be worse off,
    /// not better.
    /// </para>
    /// <para>
    /// The default the SDK would otherwise apply — <c>Optional</c> for every tool — is deliberately not
    /// used here: it would let a client run <c>extract_skills</c> as a task for no benefit, which is
    /// surface area this project does not need to support or test.
    /// </para>
    /// </summary>
    /// <param name="context">The call being classified.</param>
    /// <returns>The execution mode for this call.</returns>
    private static McpTaskExecutionMode SelectExecutionMode(RequestContext<CallToolRequestParams> context) =>
        context.Params?.Name == Mcp.ToolNames.BulkScoreShortlist
            ? McpTaskExecutionMode.Required
            : McpTaskExecutionMode.Synchronous;

    /// <summary>
    /// Puts <c>tools/list</c> into the canonical order of <see cref="Mcp.ToolNames.All"/>.
    /// <para>
    /// Measured on 1 Sep 2026, SDK 2.2.0: <b>registration order is not wire order.</b> Four tools
    /// registered as search / get / extract / score came back alphabetically — extract_skills, get_job,
    /// score_candidate_fit, search_jobs. ADR-0002 claimed the registration order produced the
    /// deterministic ordering the revision asks for; that was wrong, and it is corrected here rather
    /// than left as a comment nobody checks.
    /// </para>
    /// <para>
    /// It is not enough to observe that the SDK's own order happens to be stable, either. The tools live
    /// in a concurrent collection, and concurrent-collection enumeration order is not a documented
    /// guarantee — so "stable in this run" is not "stable across restarts", and prompt-cache hit rate is
    /// exactly the thing that suffers when it is not.
    /// </para>
    /// </summary>
    /// <param name="tools">The tools as the SDK enumerated them.</param>
    /// <returns>The same tools, canonically ordered.</returns>
    private static List<Tool> OrderCanonically(IList<Tool> tools)
    {
        return tools
            .OrderBy(static t =>
            {
                var index = Array.IndexOf(Mcp.ToolNames.All, t.Name);

                // A tool that is not in the canonical list sorts last rather than throwing: a
                // list response is the wrong place to fail. The conformance suite asserts the two
                // lists match exactly, which is where an unlisted tool should be caught.
                return index < 0 ? int.MaxValue : index;
            })
            .ThenBy(static t => t.Name, StringComparer.Ordinal)
            .ToList();
    }

    private static void AddUseCases(IServiceCollection services)
    {
        // Scoped, matching the DbContext the repositories behind them resolve. ExtractSkillsUseCase is
        // the exception and is a singleton: it has no ports at all, because normalization is a pure
        // function over the taxonomy.
        services.AddScoped<SearchJobsUseCase>();
        services.AddScoped<GetJobUseCase>();
        services.AddScoped<ScoreCandidateFitUseCase>();
        services.AddScoped<RejectCandidateUseCase>();
        services.AddSingleton<ExtractSkillsUseCase>();

        // DomainShortlistScorer needs no EF Core — it composes IJobRepository, ICandidateRepository and
        // the pure domain scorer, all of which Application already has — so its binding lives here
        // rather than in Talent.Infrastructure's composition root.
        services.AddScoped<IShortlistScorer, DomainShortlistScorer>();
        services.AddScoped<BulkScoreShortlistUseCase>();
    }
}
