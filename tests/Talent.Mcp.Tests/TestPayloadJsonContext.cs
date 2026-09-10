namespace Talent.Mcp.Tests;

using System.Text.Json.Serialization;

/// <summary>
/// Serialization contracts for the payload types <see cref="HandleCodecTests"/> mints.
/// <para>
/// Declared at namespace level rather than nested inside the test class, because the source generator
/// requires every containing type to be <c>partial</c> and making a test class partial to satisfy a
/// generator is noise. The payload records themselves stay <em>nested</em> and merely become
/// <c>internal</c>: <c>HandleCodec</c> derives its payload-type marker from
/// <c>typeof(T).FullName</c>, so promoting <c>Cursor</c> to a top-level type would change the marker,
/// change every handle's bytes, and break the golden vector — which is precisely the class of silent
/// change that vector exists to catch.
/// </para>
/// <para>
/// The options mirror <c>JsonSerializerDefaults.Web</c>, matching what <c>HandleCodec</c>'s reflective
/// path used. That is what makes the equivalence test meaningful rather than tautological.
/// </para>
/// </summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    PropertyNameCaseInsensitive = true,
    NumberHandling = JsonNumberHandling.AllowReadingFromString)]
[JsonSerializable(typeof(HandleCodecTests.Cursor))]
[JsonSerializable(typeof(HandleCodecTests.OtherPayload))]
[JsonSerializable(typeof(ForeignPayload))]
internal sealed partial class TestPayloadJsonContext : JsonSerializerContext;

/// <summary>
/// A payload type no tool owns, for asserting that a handle minted for one type is rejected when read
/// as another.
/// <para>
/// A named record rather than the anonymous type this used to be: source generation cannot emit a
/// contract for an anonymous type. The test only ever needed <em>a different type marker</em>, so
/// naming it costs nothing and preserves the assertion exactly.
/// </para>
/// </summary>
/// <param name="CandidateIds">Arbitrary content; only the type identity matters.</param>
internal sealed record ForeignPayload(Guid[] CandidateIds);
