namespace Talent.Application.Serialization;

using System.Text.Json.Serialization;
using Talent.Application.UseCases;

/// <summary>
/// Source-generated serialization contracts for the handle payloads this layer mints.
/// <para>
/// Exists because <c>HandleCodec</c> ships to NuGet and cannot enumerate its consumers' payload
/// types — so each consumer supplies its own contract, and this is ours. It is what lets
/// <see cref="Talent.Application.Ports.IHandleCodec"/>'s trim-safe overloads be used instead of the
/// reflective ones. See <see href="../../../docs/adr/0007-trim-clean-over-native-aot.md">ADR-0007</see>.
/// </para>
/// <para>
/// <strong>The options below are load-bearing, not decoration.</strong> <c>HandleCodec</c>'s reflective
/// path used <c>new JsonSerializerOptions(JsonSerializerDefaults.Web)</c>, and a source-generated
/// context inherits none of those settings. Omit them and the bytes change: handles minted before a
/// deploy stop reading after it. Handle lifetimes are minutes, so the blast radius is bounded — but
/// bounded is not the same as acceptable, and a silent format change is exactly the kind of thing
/// that ships unnoticed. <c>HandleCodecTests.Handle_bytes_are_unchanged_for_a_known_payload</c> pins
/// the exact base64url string so getting this wrong is a failing test rather than a support ticket.
/// </para>
/// </summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    PropertyNameCaseInsensitive = true,
    NumberHandling = JsonNumberHandling.AllowReadingFromString)]
[JsonSerializable(typeof(JobSearchCursor))]
public sealed partial class TalentHandleJsonContext : JsonSerializerContext;
