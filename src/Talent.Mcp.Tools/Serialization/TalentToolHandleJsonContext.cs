namespace Talent.Mcp.Tools.Serialization;

using System.Text.Json.Serialization;
using Talent.Mcp.Tools.Tools;

/// <summary>
/// Source-generated serialization contracts for the handle payloads the tool layer mints.
/// <para>
/// Separate from <c>Talent.Application</c>'s context because the payload lives here:
/// <see cref="PendingRejection"/> is the MRTR confirmation state for <c>reject_candidate</c>, which is
/// a presentation concern and not a use-case one. Each assembly declares contracts for the types it
/// owns.
/// </para>
/// <para>
/// The options mirror <c>JsonSerializerDefaults.Web</c> exactly, for the reason spelled out on
/// <c>TalentHandleJsonContext</c>: a source-generated context inherits none of them, and getting it
/// wrong silently changes the bytes of every handle this tool mints.
/// </para>
/// </summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    PropertyNameCaseInsensitive = true,
    NumberHandling = JsonNumberHandling.AllowReadingFromString)]
[JsonSerializable(typeof(PendingRejection))]
public sealed partial class TalentToolHandleJsonContext : JsonSerializerContext;
