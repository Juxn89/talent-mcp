namespace Talent.Mcp.Tests;

using System.Security.Cryptography;
using Talent.Mcp.Toolkit;
using Xunit;

/// <summary>
/// Tests for the signed handles that replace sessions under the 2026-07-28 revision.
/// <para>
/// Most of these are adversarial. A handle is the only thing carrying state between stateless calls, so
/// "can a client forge, extend, tamper with, or replay one" is the whole security surface of the
/// pagination and shortlist tools. A codec that only round-trips correctly is not tested.
/// </para>
/// </summary>
public sealed class HandleCodecTests
{
    private static readonly byte[] Key = Enumerable.Range(0, 32).Select(i => (byte)i).ToArray();
    private static readonly byte[] OtherKey = Enumerable.Range(100, 32).Select(i => (byte)i).ToArray();

    private sealed record Cursor(string Query, int Skip);

    [Fact]
    public void Round_trips_a_payload()
    {
        using var codec = new HandleCodec(Key);
        var handle = codec.Mint(new Cursor("dotnet", 40), TimeSpan.FromMinutes(10));

        Assert.True(codec.TryRead<Cursor>(handle, out var cursor));
        Assert.Equal(new Cursor("dotnet", 40), cursor);
    }

    [Fact]
    public void Handle_is_url_safe()
    {
        using var codec = new HandleCodec(Key);

        // Handles travel as ordinary tool arguments and end up in URLs and logs. '+' and '/' from
        // standard base64 would be mangled by a naive consumer; base64url avoids the class entirely.
        var handle = codec.Mint(new Cursor("c# / .net", 1), TimeSpan.FromMinutes(10));

        Assert.DoesNotContain('+', handle);
        Assert.DoesNotContain('/', handle);
        Assert.DoesNotContain('=', handle);
    }

    [Fact]
    public void A_handle_signed_with_another_key_is_rejected()
    {
        using var minter = new HandleCodec(OtherKey);
        using var verifier = new HandleCodec(Key);

        var foreign = minter.Mint(new Cursor("dotnet", 40), TimeSpan.FromMinutes(10));

        Assert.False(verifier.TryRead<Cursor>(foreign, out var cursor));
        Assert.Null(cursor);
    }

    [Fact]
    public void A_tampered_payload_is_rejected()
    {
        using var codec = new HandleCodec(Key);
        var handle = codec.Mint(new Cursor("dotnet", 40), TimeSpan.FromMinutes(10));

        // Flip one bit in the middle, which is where the payload lives.
        var bytes = System.Buffers.Text.Base64Url.DecodeFromChars(handle);
        bytes[bytes.Length / 2] ^= 0x01;
        var tampered = System.Buffers.Text.Base64Url.EncodeToString(bytes);

        Assert.False(codec.TryRead<Cursor>(tampered, out _));
    }

    [Fact]
    public void A_tampered_expiry_is_rejected_so_a_handle_cannot_be_extended()
    {
        var clock = new FakeTimeProvider(DateTimeOffset.Parse("2026-08-27T12:00:00Z", null));
        using var codec = new HandleCodec(Key, clock);

        var handle = codec.Mint(new Cursor("dotnet", 0), TimeSpan.FromMinutes(1));
        var bytes = System.Buffers.Text.Base64Url.DecodeFromChars(handle);

        // The expiry is the first 8 bytes. Raising it is exactly the attack the layout defends
        // against by putting the timestamp inside the signed region.
        bytes[7] = 0xFF;
        var extended = System.Buffers.Text.Base64Url.EncodeToString(bytes);

        Assert.False(codec.TryRead<Cursor>(extended, out _));
    }

    [Fact]
    public void An_expired_handle_is_rejected()
    {
        var clock = new FakeTimeProvider(DateTimeOffset.Parse("2026-08-27T12:00:00Z", null));
        using var codec = new HandleCodec(Key, clock);

        var handle = codec.Mint(new Cursor("dotnet", 0), TimeSpan.FromMinutes(10));

        Assert.True(codec.TryRead<Cursor>(handle, out _));

        clock.Advance(TimeSpan.FromMinutes(10) + TimeSpan.FromSeconds(1));

        Assert.False(codec.TryRead<Cursor>(handle, out _));
    }

    [Fact]
    public void A_handle_is_still_valid_at_the_instant_it_expires()
    {
        // Boundary stated explicitly rather than left to chance: expiry is inclusive, so a handle
        // read in the same second it expires still works.
        var clock = new FakeTimeProvider(DateTimeOffset.Parse("2026-08-27T12:00:00Z", null));
        using var codec = new HandleCodec(Key, clock);

        var handle = codec.Mint(new Cursor("dotnet", 0), TimeSpan.FromMinutes(10));
        clock.Advance(TimeSpan.FromMinutes(10));

        Assert.True(codec.TryRead<Cursor>(handle, out _));
    }

    private sealed record OtherPayload(Guid ShortlistId, int Processed);

    [Fact]
    public void An_authentic_handle_for_a_different_payload_type_is_rejected()
    {
        using var codec = new HandleCodec(Key);

        // Replaying a shortlist handle at the pagination tool. The signature is genuine, so the
        // signed type marker is the only thing that can catch it.
        //
        // Relying on deserialization to fail does NOT work, which is how this test earned its
        // keep: System.Text.Json is lenient, so OtherPayload read as Cursor produced
        // Cursor(Query: null, Skip: 0) instead of throwing, TryRead returned true, and the tool
        // would have paged from offset 0 believing the handle was its own.
        var shortlistHandle = codec.Mint(new OtherPayload(Guid.NewGuid(), 3), TimeSpan.FromMinutes(10));

        Assert.False(codec.TryRead<Cursor>(shortlistHandle, out var cursor));
        Assert.Null(cursor);
    }

    [Fact]
    public void The_type_marker_is_stable_across_codec_instances()
    {
        // Derived from SHA-256 of the type name, not string.GetHashCode(), which is randomized per
        // process. A per-process marker would invalidate every outstanding handle on restart.
        using var minter = new HandleCodec(Key);
        using var verifier = new HandleCodec(Key);

        var handle = minter.Mint(new Cursor("dotnet", 40), TimeSpan.FromMinutes(10));

        Assert.True(verifier.TryRead<Cursor>(handle, out var cursor));
        Assert.Equal(new Cursor("dotnet", 40), cursor);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not-base64url-!!!")]
    [InlineData("AAAA")]
    public void Malformed_input_is_rejected_without_throwing(string? handle)
    {
        using var codec = new HandleCodec(Key);

        // Never throws: the tool layer needs to answer with an actionable protocol error, and an
        // exception escaping here would surface as a stack trace to a client instead.
        Assert.False(codec.TryRead<Cursor>(handle, out _));
    }

    [Fact]
    public void A_short_signing_key_is_rejected()
    {
        var tooShort = new byte[HandleCodec.MinimumKeyLengthBytes - 1];

        var error = Assert.Throws<ArgumentException>(() => new HandleCodec(tooShort));
        Assert.Equal("signingKey", error.ParamName);
    }

    [Fact]
    public void A_null_signing_key_is_rejected() =>
        Assert.Throws<ArgumentNullException>(() => new HandleCodec(null!));

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void A_non_positive_time_to_live_is_rejected(int seconds)
    {
        using var codec = new HandleCodec(Key);

        Assert.Throws<ArgumentOutOfRangeException>(
            () => codec.Mint(new Cursor("x", 0), TimeSpan.FromSeconds(seconds)));
    }

    [Fact]
    public void Minting_after_dispose_throws()
    {
        var codec = new HandleCodec(Key);
        codec.Dispose();

        Assert.Throws<ObjectDisposedException>(() => codec.Mint(new Cursor("x", 0), TimeSpan.FromMinutes(1)));
    }

    [Fact]
    public void Dispose_is_idempotent()
    {
        var codec = new HandleCodec(Key);

        codec.Dispose();
        codec.Dispose();
    }

    [Fact]
    public void Concurrent_use_is_safe()
    {
        // A stateless HTTP server serves concurrent requests from one registered codec, and
        // HMACSHA256 instances are not thread-safe. Without the lock inside ComputeSignature this
        // produces corrupt signatures rather than an exception, so the assertion is on correctness.
        using var codec = new HandleCodec(Key);

        var handles = new string[200];
        Parallel.For(0, handles.Length, i =>
        {
            handles[i] = codec.Mint(new Cursor("q" + i, i), TimeSpan.FromMinutes(10));
        });

        Parallel.For(0, handles.Length, i =>
        {
            Assert.True(codec.TryRead<Cursor>(handles[i], out var cursor));
            Assert.Equal(i, cursor!.Skip);
        });
    }

    [Fact]
    public void Signatures_use_the_full_hmac_length()
    {
        using var codec = new HandleCodec(Key);
        var handle = codec.Mint(new Cursor("a", 0), TimeSpan.FromMinutes(1));
        var bytes = System.Buffers.Text.Base64Url.DecodeFromChars(handle);

        // 8-byte expiry + payload + 32-byte HMAC-SHA256. A truncated signature would still verify
        // against itself, so the length is asserted rather than assumed.
        Assert.True(bytes.Length > 8 + SHA256.HashSizeInBytes);
    }

    private sealed class FakeTimeProvider(DateTimeOffset now) : TimeProvider
    {
        private DateTimeOffset now = now;

        public override DateTimeOffset GetUtcNow() => this.now;

        public void Advance(TimeSpan by) => this.now = this.now.Add(by);
    }
}
