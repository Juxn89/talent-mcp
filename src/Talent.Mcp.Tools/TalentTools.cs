namespace Talent.Mcp.Tools;

using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Protocol;
using Talent.Application.UseCases;
using Talent.Mcp.Toolkit.Caching;
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
    /// <returns>The same builder, for chaining.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="builder"/> was <see langword="null"/>.</exception>
    public static IMcpServerBuilder AddTalentTools(this IMcpServerBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

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
            .WithRequestFilters(static filters => filters.AddListToolsFilter(static next =>
                async (request, cancellationToken) =>
                {
                    // A filter rather than WithListToolsHandler: the handler would replace the SDK's
                    // own enumeration of the registered tools, which is the part that generates
                    // inputSchema. All this needs to do is post-process the result.
                    var result = await next(request, cancellationToken).ConfigureAwait(false);
                    result.Tools = OrderCanonically(result.Tools);
                    return CachePolicies.ToolsList.ApplyTo(result);
                }));
    }

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
    }
}
