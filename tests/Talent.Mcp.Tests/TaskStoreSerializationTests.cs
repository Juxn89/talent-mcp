namespace Talent.Mcp.Tests;

using System.Text.Json;
using ModelContextProtocol.Extensions.Tasks;
using ModelContextProtocol.Protocol;
using Xunit;

/// <summary>
/// Pins the wire format of the two types <c>PostgresMcpTaskStore</c> writes into its <c>jsonb</c>
/// columns, across F6's move from reflection-based serialization to the SDK's source-generated
/// <see cref="McpTasksJsonContext"/>.
/// <para>
/// The move was made to clear <c>IL2026</c>/<c>IL3050</c> so the assembly can be trimmed. That is
/// only safe if the bytes are unchanged: rows written by a previous deployment are still in the
/// table, and a task store that cannot read its own history loses in-flight work on upgrade — which
/// is the exact failure the Postgres store exists to prevent.
/// </para>
/// <para>
/// The bytes are expected to be identical because both types declare their own
/// <c>[JsonConverter]</c>, so the shape is decided by the SDK's converter rather than by any naming
/// policy on the options. That is a claim about someone else's library, so it is measured here
/// rather than argued in a comment.
/// </para>
/// </summary>
public sealed class TaskStoreSerializationTests
{
    private static InputRequest SampleRequest() => InputRequest.ForElicitation(
        new ElicitRequestParams { Message = "Reject candidate? This cannot be undone." });

    private static InputResponse SampleResponse() => InputResponse.FromElicitResult(
        new ElicitResult { Action = "accept" });

    [Fact]
    public void InputRequest_bytes_are_identical_under_the_source_generated_context()
    {
        var request = SampleRequest();

        // The pre-F6 call: JsonSerializer.Serialize(request) with the ambient default options.
#pragma warning disable IL2026, IL3050
        var reflectionBased = JsonSerializer.Serialize(request);
#pragma warning restore IL2026, IL3050
        var sourceGenerated = JsonSerializer.Serialize(request, McpTasksJsonContext.Default.InputRequest);

        Assert.Equal(reflectionBased, sourceGenerated);
    }

    [Fact]
    public void InputResponse_bytes_are_identical_under_the_source_generated_context()
    {
        var response = SampleResponse();

#pragma warning disable IL2026, IL3050
        var reflectionBased = JsonSerializer.Serialize(response);
#pragma warning restore IL2026, IL3050
        var sourceGenerated = JsonSerializer.Serialize(response, McpTasksJsonContext.Default.InputResponse);

        Assert.Equal(reflectionBased, sourceGenerated);
    }

    [Fact]
    public void A_row_written_the_old_way_still_reads_the_new_way()
    {
        // The upgrade path that matters: a row already sitting in input_requests when the trimmed
        // build rolls out.
#pragma warning disable IL2026, IL3050
        var storedByPreviousDeployment = JsonSerializer.Serialize(SampleRequest());
#pragma warning restore IL2026, IL3050

        var read = JsonSerializer.Deserialize(
            storedByPreviousDeployment, McpTasksJsonContext.Default.InputRequest);

        Assert.NotNull(read);
        Assert.Equal(SampleRequest().Method, read!.Method);
    }

    [Fact]
    public void An_input_response_row_written_the_old_way_still_reads_the_new_way()
    {
#pragma warning disable IL2026, IL3050
        var storedByPreviousDeployment = JsonSerializer.Serialize(SampleResponse());
#pragma warning restore IL2026, IL3050

        var read = JsonSerializer.Deserialize(
            storedByPreviousDeployment, McpTasksJsonContext.Default.InputResponse);

        Assert.NotNull(read);
        Assert.Equal(
            JsonSerializer.Serialize(SampleResponse(), McpTasksJsonContext.Default.InputResponse),
            JsonSerializer.Serialize(read!, McpTasksJsonContext.Default.InputResponse));
    }
}
