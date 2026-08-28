namespace Talent.Infrastructure.Handles;

using Talent.Application.Ports;
using Talent.Mcp.Toolkit;

/// <summary>
/// Adapts the toolkit's <see cref="HandleCodec"/> to the Application layer's
/// <see cref="IHandleCodec"/> port.
/// <para>
/// This class exists purely to keep two rules true at once: the Application layer must not reference
/// the toolkit, and the toolkit — which ships to NuGet — must not reference the Application layer. So
/// the port is declared inward, the implementation is domain-agnostic and outward, and the adapter
/// sits in Infrastructure where depending on both is legal. It is the dependency rule doing visible
/// work rather than being asserted in a document.
/// </para>
/// </summary>
public sealed class SignedHandleCodec : IHandleCodec, IDisposable
{
    private readonly HandleCodec codec;
    private readonly bool ownsCodec;

    /// <summary>Creates an adapter that owns its codec, building one from a signing key.</summary>
    /// <param name="signingKey">
    /// Signing key, at least <see cref="HandleCodec.MinimumKeyLengthBytes"/> bytes, supplied from
    /// configuration.
    /// </param>
    /// <param name="timeProvider">Clock, injected so expiry is testable without sleeping.</param>
    public SignedHandleCodec(byte[] signingKey, TimeProvider? timeProvider = null)
        : this(new HandleCodec(signingKey, timeProvider), ownsCodec: true)
    {
    }

    /// <summary>Creates an adapter over an existing codec.</summary>
    /// <param name="codec">The codec to delegate to.</param>
    /// <param name="ownsCodec">
    /// Whether disposing this adapter should dispose <paramref name="codec"/>. Registering the codec
    /// as a singleton in DI and the adapter as scoped would otherwise dispose a shared instance.
    /// </param>
    /// <exception cref="ArgumentNullException">The codec was <see langword="null"/>.</exception>
    public SignedHandleCodec(HandleCodec codec, bool ownsCodec = false)
    {
        ArgumentNullException.ThrowIfNull(codec);

        this.codec = codec;
        this.ownsCodec = ownsCodec;
    }

    /// <inheritdoc />
    public string Mint<TPayload>(TPayload payload, TimeSpan timeToLive)
        where TPayload : notnull
        => this.codec.Mint(payload, timeToLive);

    /// <inheritdoc />
    public bool TryRead<TPayload>(string? handle, out TPayload? payload)
        where TPayload : notnull
        => this.codec.TryRead(handle, out payload);

    /// <summary>Disposes the underlying codec when this adapter owns it.</summary>
    public void Dispose()
    {
        if (this.ownsCodec)
        {
            this.codec.Dispose();
        }
    }
}
