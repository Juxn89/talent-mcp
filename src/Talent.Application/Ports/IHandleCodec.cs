namespace Talent.Application.Ports;

using System.Diagnostics.CodeAnalysis;
using System.Text.Json.Serialization.Metadata;

/// <summary>
/// Mints and verifies the opaque, signed, TTL-bounded handles that replace sessions under the
/// 2026-07-28 revision.
/// <para>
/// Declared here as a port and implemented in <c>Talent.Mcp.Toolkit</c>: the use cases need handles,
/// but the Application layer must not depend on the toolkit, and the toolkit must stay
/// domain-agnostic. The interface is the seam that lets both hold.
/// </para>
/// </summary>
public interface IHandleCodec
{
    /// <summary>
    /// Mints a signed handle carrying <paramref name="payload"/>, serialized through a
    /// source-generated contract.
    /// </summary>
    /// <typeparam name="TPayload">Payload type.</typeparam>
    /// <param name="payload">State to carry across calls.</param>
    /// <param name="payloadTypeInfo">The payload's serialization contract.</param>
    /// <param name="timeToLive">How long the handle stays valid.</param>
    /// <returns>An opaque handle safe to hand to a client.</returns>
    string Mint<TPayload>(TPayload payload, JsonTypeInfo<TPayload> payloadTypeInfo, TimeSpan timeToLive)
        where TPayload : notnull;

    /// <summary>Mints a signed handle, serializing <paramref name="payload"/> reflectively.</summary>
    /// <typeparam name="TPayload">Payload type.</typeparam>
    /// <param name="payload">State to carry across calls.</param>
    /// <param name="timeToLive">How long the handle stays valid.</param>
    /// <returns>An opaque handle safe to hand to a client.</returns>
    [Obsolete("Use the overload taking a JsonTypeInfo<TPayload>. See ADR-0007.")]
    [RequiresUnreferencedCode("Serializes TPayload reflectively.")]
    [RequiresDynamicCode("Serializes TPayload reflectively.")]
    string Mint<TPayload>(TPayload payload, TimeSpan timeToLive)
        where TPayload : notnull;

    /// <summary>
    /// Verifies a handle's signature and expiry, then deserializes its payload through a
    /// source-generated contract.
    /// </summary>
    /// <typeparam name="TPayload">Expected payload type.</typeparam>
    /// <param name="handle">The handle a client sent back.</param>
    /// <param name="payloadTypeInfo">The payload's serialization contract.</param>
    /// <param name="payload">The payload when verification succeeded.</param>
    /// <returns>
    /// <see langword="true"/> when the handle is authentic and unexpired. A forged, tampered, foreign
    /// or expired handle returns <see langword="false"/> rather than throwing, so the tool layer can
    /// answer with an actionable protocol error.
    /// </returns>
    bool TryRead<TPayload>(string? handle, JsonTypeInfo<TPayload> payloadTypeInfo, out TPayload? payload)
        where TPayload : notnull;

    /// <summary>Verifies a handle, then deserializes its payload reflectively.</summary>
    /// <typeparam name="TPayload">Expected payload type.</typeparam>
    /// <param name="handle">The handle a client sent back.</param>
    /// <param name="payload">The payload when verification succeeded.</param>
    /// <returns><see langword="true"/> when the handle is authentic and unexpired.</returns>
    [Obsolete("Use the overload taking a JsonTypeInfo<TPayload>. See ADR-0007.")]
    [RequiresUnreferencedCode("Deserializes TPayload reflectively.")]
    [RequiresDynamicCode("Deserializes TPayload reflectively.")]
    bool TryRead<TPayload>(string? handle, out TPayload? payload)
        where TPayload : notnull;
}
