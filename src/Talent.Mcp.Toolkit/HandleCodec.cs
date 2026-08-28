namespace Talent.Mcp.Toolkit;

using System.Buffers.Binary;
using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text.Json;

/// <summary>
/// Mints and verifies opaque, signed, TTL-bounded handles.
/// <para>
/// The 2026-07-28 revision removed sessions: SEP-2567 removed <c>Mcp-Session-Id</c> and SEP-2575
/// removed the <c>initialize</c> handshake, so state between calls travels as ordinary tool
/// arguments. A raw offset would be client-modifiable — a pagination cursor a caller can edit is an
/// access-control hole, not a convenience — so handles are signed and expire.
/// </para>
/// <para>
/// Domain-agnostic by construction: it serializes whatever payload it is given and knows nothing
/// about jobs, candidates or skills. It also implements no application interface, because the
/// <c>IHandleCodec</c> port lives in <c>Talent.Application</c> and this library must not reference it
/// — Infrastructure supplies the adapter. The architecture test enforces both halves.
/// </para>
/// <para>
/// This is not encryption. A payload is signed, not hidden: a client can read it but cannot alter it.
/// Never put a secret in a payload.
/// </para>
/// </summary>
public sealed class HandleCodec : IDisposable
{
    /// <summary>Minimum signing-key length. Shorter keys weaken HMAC-SHA256 below its design margin.</summary>
    public const int MinimumKeyLengthBytes = 32;

    private const int TimestampLengthBytes = sizeof(long);
    private const int SignatureLengthBytes = 32;

    private static readonly JsonSerializerOptions PayloadJsonOptions = new(JsonSerializerDefaults.Web);

    private readonly HMACSHA256 hmac;
    private readonly TimeProvider timeProvider;
    private bool disposed;

    /// <summary>Creates a codec.</summary>
    /// <param name="signingKey">
    /// Signing key, at least <see cref="MinimumKeyLengthBytes"/> bytes. Supplied from configuration —
    /// never a literal in code.
    /// </param>
    /// <param name="timeProvider">
    /// Clock. Injected rather than reading <see cref="DateTimeOffset.UtcNow"/> directly so expiry is
    /// testable without sleeping, which is the difference between a fast suite and a flaky one.
    /// </param>
    /// <exception cref="ArgumentNullException">The signing key was <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">The signing key was too short.</exception>
    public HandleCodec(byte[] signingKey, TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(signingKey);

        if (signingKey.Length < MinimumKeyLengthBytes)
        {
            throw new ArgumentException(
                $"The signing key must be at least {MinimumKeyLengthBytes} bytes; got {signingKey.Length}.",
                nameof(signingKey));
        }

        this.hmac = new HMACSHA256(signingKey);
        this.timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <summary>Mints a signed handle carrying <paramref name="payload"/>.</summary>
    /// <typeparam name="TPayload">Payload type.</typeparam>
    /// <param name="payload">State to carry across calls.</param>
    /// <param name="timeToLive">How long the handle stays valid. Must be positive.</param>
    /// <returns>An opaque handle safe to hand to a client.</returns>
    /// <exception cref="ArgumentNullException">The payload was <see langword="null"/>.</exception>
    /// <exception cref="ArgumentOutOfRangeException">The time-to-live was not positive.</exception>
    /// <exception cref="ObjectDisposedException">The codec was disposed.</exception>
    public string Mint<TPayload>(TPayload payload, TimeSpan timeToLive)
        where TPayload : notnull
    {
        ArgumentNullException.ThrowIfNull(payload);
        ObjectDisposedException.ThrowIf(this.disposed, this);

        if (timeToLive <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(timeToLive), timeToLive, "A handle's time-to-live must be positive.");
        }

        var expiresAt = this.timeProvider.GetUtcNow().Add(timeToLive).ToUnixTimeSeconds();
        var payloadBytes = JsonSerializer.SerializeToUtf8Bytes(payload, PayloadJsonOptions);

        // Layout: [8-byte big-endian expiry][payload][32-byte HMAC over the two preceding parts].
        // The expiry sits inside the signed region, so extending a handle's life requires the key.
        var signedLength = TimestampLengthBytes + payloadBytes.Length;
        var buffer = new byte[signedLength + SignatureLengthBytes];

        BinaryPrimitives.WriteInt64BigEndian(buffer.AsSpan(0, TimestampLengthBytes), expiresAt);
        payloadBytes.CopyTo(buffer.AsSpan(TimestampLengthBytes));

        var signature = this.ComputeSignature(buffer.AsSpan(0, signedLength));
        signature.CopyTo(buffer.AsSpan(signedLength));

        return Base64Url.EncodeToString(buffer);
    }

    /// <summary>Verifies a handle's signature and expiry, then returns its payload.</summary>
    /// <typeparam name="TPayload">Expected payload type.</typeparam>
    /// <param name="handle">The handle a client sent back.</param>
    /// <param name="payload">The payload when verification succeeded.</param>
    /// <returns>
    /// <see langword="true"/> when the handle is authentic and unexpired. A forged, tampered, foreign
    /// or expired handle returns <see langword="false"/> rather than throwing, so the tool layer can
    /// answer with an actionable protocol error instead of a stack trace.
    /// </returns>
    /// <exception cref="ObjectDisposedException">The codec was disposed.</exception>
    public bool TryRead<TPayload>(string? handle, out TPayload? payload)
        where TPayload : notnull
    {
        payload = default;
        ObjectDisposedException.ThrowIf(this.disposed, this);

        if (string.IsNullOrWhiteSpace(handle))
        {
            return false;
        }

        byte[] buffer;
        try
        {
            buffer = Base64Url.DecodeFromChars(handle);
        }
        catch (FormatException)
        {
            return false;
        }

        if (buffer.Length < TimestampLengthBytes + SignatureLengthBytes)
        {
            return false;
        }

        var signedLength = buffer.Length - SignatureLengthBytes;
        var expected = this.ComputeSignature(buffer.AsSpan(0, signedLength));

        // Fixed-time comparison: a content-dependent early exit leaks how much of a forged signature
        // was correct, which is enough to forge one byte at a time.
        if (!CryptographicOperations.FixedTimeEquals(expected, buffer.AsSpan(signedLength)))
        {
            return false;
        }

        var expiresAt = BinaryPrimitives.ReadInt64BigEndian(buffer.AsSpan(0, TimestampLengthBytes));
        if (this.timeProvider.GetUtcNow().ToUnixTimeSeconds() > expiresAt)
        {
            return false;
        }

        try
        {
            payload = JsonSerializer.Deserialize<TPayload>(
                buffer.AsSpan(TimestampLengthBytes, signedLength - TimestampLengthBytes),
                PayloadJsonOptions);
        }
        catch (JsonException)
        {
            // An authentic handle whose payload does not fit TPayload: a client replayed a handle
            // minted for a different tool. Not an authentication failure, but not usable either.
            return false;
        }

        return payload is not null;
    }

    /// <summary>Releases the signing primitive.</summary>
    public void Dispose()
    {
        if (this.disposed)
        {
            return;
        }

        this.hmac.Dispose();
        this.disposed = true;
    }

    private byte[] ComputeSignature(ReadOnlySpan<byte> data)
    {
        var signature = new byte[SignatureLengthBytes];

        // HMACSHA256 instances are not thread-safe, and a stateless HTTP server serves concurrent
        // requests from one registered codec.
        lock (this.hmac)
        {
            this.hmac.TryComputeHash(data, signature, out _);
        }

        return signature;
    }
}
