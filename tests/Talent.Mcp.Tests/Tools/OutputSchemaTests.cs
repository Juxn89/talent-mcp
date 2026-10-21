namespace Talent.Mcp.Tests.Tools;

using System.Text.Json;
using Talent.Mcp.Tools.Constants;
using Xunit;

/// <summary>
/// What each tool's <c>outputSchema</c> promises, against what its payload actually carries.
/// <para>
/// Found 20 Oct 2026 with MCP Inspector 2.9.0 (#12): <c>search_jobs</c> listed <c>nextPageHandle</c> as
/// required, while the SDK's serializer omits null properties, so every last page arrived without it.
/// A client that validates structured output — Inspector does — rejected those pages with
/// "data must have required property 'nextPageHandle'". No test compared the two.
/// </para>
/// </summary>
public sealed class OutputSchemaTests
{
    [Fact]
    public async Task No_tool_requires_a_property_its_payload_may_leave_out()
    {
        await using var harness = await ToolHarness.StartAsync();

        var violations = new List<string>();
        foreach (var tool in await harness.Client.ListToolsAsync())
        {
            if (tool.ProtocolTool.OutputSchema is { } schema)
            {
                CollectNullableRequired(schema, tool.Name, violations);
            }
        }

        // A nullable property is omitted on the wire when it is null, so it cannot also be required.
        Assert.Empty(violations);
    }

    [Fact]
    public async Task The_last_page_of_a_search_matches_the_declared_schema()
    {
        await using var harness = await ToolHarness.StartAsync(new FakeJobRepository([ToolTestData.MadridBackend]));

        var page = await harness.SearchAsync();
        var schema = (await harness.Client.ListToolsAsync())
            .Single(t => t.Name == Mcp.ToolNames.SearchJobs)
            .ProtocolTool.OutputSchema!.Value;

        Assert.False(page.GetProperty("hasMore").GetBoolean());
        foreach (var required in RequiredOf(schema))
        {
            Assert.True(page.TryGetProperty(required, out _), $"The last page omits required property '{required}'.");
        }
    }

    private static void CollectNullableRequired(JsonElement schema, string path, List<string> violations)
    {
        if (schema.ValueKind != JsonValueKind.Object)
        {
            return;
        }

        if (schema.TryGetProperty("properties", out var properties))
        {
            var required = RequiredOf(schema).ToHashSet(StringComparer.Ordinal);
            foreach (var property in properties.EnumerateObject())
            {
                var propertyPath = $"{path}.{property.Name}";
                if (required.Contains(property.Name) && AllowsNull(property.Value))
                {
                    violations.Add(propertyPath);
                }

                CollectNullableRequired(property.Value, propertyPath, violations);
            }
        }

        if (schema.TryGetProperty("items", out var items))
        {
            CollectNullableRequired(items, $"{path}[]", violations);
        }
    }

    private static IEnumerable<string> RequiredOf(JsonElement schema) =>
        schema.TryGetProperty("required", out var required)
            ? required.EnumerateArray().Select(static name => name.GetString()!)
            : [];

    private static bool AllowsNull(JsonElement property) =>
        property.TryGetProperty("type", out var type) && type.ValueKind switch
        {
            JsonValueKind.String => type.GetString() == "null",
            JsonValueKind.Array => type.EnumerateArray().Any(static t => t.GetString() == "null"),
            _ => false,
        };
}
