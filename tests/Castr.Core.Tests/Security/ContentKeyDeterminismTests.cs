using System.Security.Cryptography;
using Castr.Core.Security;

namespace Castr.Core.Tests.Security;

/// <summary>
/// Proves the property the receiver's bounded chunk cache is built on: <see cref="ContentKey.EncryptChunk"/> is
/// a <b>pure deterministic function</b> of (key material, sessionId, fileIndex, chunkIndex, plaintext).
///
/// <para>This is load-bearing rather than incidental. <c>ReceiverSession</c> evicts verified chunk ciphertext
/// under a byte budget and, when a peer asks for an evicted chunk, reads the plaintext back off disk and
/// re-encrypts it. That is only sound if re-encryption reproduces the evicted bytes exactly — the Merkle proof
/// retained alongside commits to a specific ciphertext hash, so a single differing byte would make the relayed
/// chunk fail verification at every peer. ChaCha20-Poly1305 has no implicit randomness, and both of the inputs
/// that could have carried some are derived: the nonce is <c>fileIndex|chunkIndex|0000</c> and the AAD is
/// <c>sessionId|fileIndex|chunkIndex</c>. These tests pin that so a future change (a random nonce, a timestamp
/// in the AAD, a switch to a randomized AEAD) fails here rather than in a rare cross-peer repair.</para>
/// </summary>
public class ContentKeyDeterminismTests
{
    private static readonly byte[] SessionId = Enumerable.Range(0, 16).Select(i => (byte)i).ToArray();

    [Fact]
    public void EncryptChunk_IsByteIdentical_AcrossRepeatedCalls()
    {
        var raw = RandomNumberGenerator.GetBytes(ContentKey.SizeBytes);
        using var key = ContentKey.Import(raw);
        var plaintext = RandomNumberGenerator.GetBytes(262_144); // the shipped default chunk size

        var first = key.EncryptChunk(SessionId, fileIndex: 3, chunkIndex: 4_211, plaintext);
        var second = key.EncryptChunk(SessionId, fileIndex: 3, chunkIndex: 4_211, plaintext);

        Assert.Equal(first, second);
    }

    [Fact]
    public void EncryptChunk_IsByteIdentical_AcrossTwoImportsOfTheSameKeyMaterial()
    {
        // The receiver's cold path re-encrypts using the ContentKey it unwrapped from its KEY_GRANT — a
        // different ContentKey *instance* from the sender's, imported from the same 32 bytes. So instance
        // identity must not matter, only the material.
        var raw = RandomNumberGenerator.GetBytes(ContentKey.SizeBytes);
        var plaintext = RandomNumberGenerator.GetBytes(9_973); // deliberately not a round number / final short chunk

        using var senderSide = ContentKey.Import(raw);
        using var receiverSide = ContentKey.Import(raw);

        Assert.Equal(
            senderSide.EncryptChunk(SessionId, fileIndex: 0, chunkIndex: 17, plaintext),
            receiverSide.EncryptChunk(SessionId, fileIndex: 0, chunkIndex: 17, plaintext));
    }

    [Fact]
    public void EncryptChunk_RoundTripsThroughDecrypt_SoTheColdPathReproducesUsablePlaintextToo()
    {
        var raw = RandomNumberGenerator.GetBytes(ContentKey.SizeBytes);
        using var key = ContentKey.Import(raw);
        var plaintext = RandomNumberGenerator.GetBytes(4_096);

        var ciphertext = key.EncryptChunk(SessionId, fileIndex: 1, chunkIndex: 2, plaintext);
        var recovered = key.TryDecryptChunk(SessionId, fileIndex: 1, chunkIndex: 2, ciphertext);

        Assert.Equal(plaintext, recovered);
    }

    // ---- Negative controls ----
    //
    // These assert on the ciphertext BODY, with the 16-byte Poly1305 tag stripped, and that detail is the whole
    // point. An earlier version of this file compared whole ciphertexts and was shown not to bite: mutating
    // BuildNonce to ignore fileIndex/chunkIndex entirely — a position-INDEPENDENT nonce, still perfectly
    // deterministic, and a catastrophic keystream reuse across every chunk under one key — left all six tests
    // green. The AAD still varies by position, so the tag still differs, so Assert.NotEqual on the whole blob was
    // satisfied while the bodies were byte-identical. The tag is authentication; the body is the keystream. Only
    // the body can tell you the nonce did its job.

    private static byte[] Body(byte[] ciphertext) => ciphertext[..^16]; // strip the Poly1305 tag

    [Theory]
    [InlineData(0, 1)] // same file, adjacent chunk
    [InlineData(1, 0)] // same chunk index, different file
    public void EncryptChunk_UsesADifferentKeystreamAtADifferentPosition(int fileIndex, int chunkIndex)
    {
        // Determinism would be trivially satisfied by an encryptor that ignored position — and that encryptor
        // would reuse one ChaCha20 keystream for the entire transfer. Encrypting the SAME plaintext at two
        // positions makes that visible: identical bodies would mean identical keystreams.
        var raw = RandomNumberGenerator.GetBytes(ContentKey.SizeBytes);
        using var key = ContentKey.Import(raw);
        var plaintext = RandomNumberGenerator.GetBytes(1_024);

        var baseline = key.EncryptChunk(SessionId, fileIndex: 0, chunkIndex: 0, plaintext);
        var moved = key.EncryptChunk(SessionId, fileIndex, chunkIndex, plaintext);

        Assert.NotEqual(Body(baseline), Body(moved));
    }

    [Fact]
    public void EncryptChunk_IsNotATwoTimePad_AcrossChunksOfOneTransfer()
    {
        // The attack the test above prevents, stated directly. Under a position-independent nonce the keystream
        // repeats, so for any two chunks c0 = p0 ^ ks and c1 = p1 ^ ks, giving c0 ^ c1 == p0 ^ p1 — the XOR of
        // two plaintexts recovered from ciphertext alone, with no key. This asserts that equality does NOT hold.
        var raw = RandomNumberGenerator.GetBytes(ContentKey.SizeBytes);
        using var key = ContentKey.Import(raw);
        var p0 = RandomNumberGenerator.GetBytes(1_024);
        var p1 = RandomNumberGenerator.GetBytes(1_024);

        var c0 = Body(key.EncryptChunk(SessionId, fileIndex: 0, chunkIndex: 0, p0));
        var c1 = Body(key.EncryptChunk(SessionId, fileIndex: 0, chunkIndex: 1, p1));

        Assert.NotEqual(Xor(c0, c1), Xor(p0, p1));
    }

    [Fact]
    public void EncryptChunk_BindsToTheSessionId_ViaTheAad()
    {
        // Deliberately NOT a keystream test, and deliberately compared whole rather than body-only: the sessionId
        // is an AAD input and does not enter the nonce, so the bodies here are identical *by design* and only the
        // tag distinguishes them. That is exactly the position-confusion binding ADR-0003 specifies, and it is
        // what makes a ciphertext replayed into another session fail to authenticate.
        var raw = RandomNumberGenerator.GetBytes(ContentKey.SizeBytes);
        using var key = ContentKey.Import(raw);
        var plaintext = RandomNumberGenerator.GetBytes(1_024);
        var otherSession = SessionId.Select(b => (byte)(b ^ 0xFF)).ToArray();

        var mine = key.EncryptChunk(SessionId, fileIndex: 0, chunkIndex: 0, plaintext);
        var other = key.EncryptChunk(otherSession, fileIndex: 0, chunkIndex: 0, plaintext);

        Assert.NotEqual(mine, other);
        Assert.Null(key.TryDecryptChunk(otherSession, fileIndex: 0, chunkIndex: 0, mine)); // and it fails closed
    }

    private static byte[] Xor(byte[] a, byte[] b)
    {
        var result = new byte[Math.Min(a.Length, b.Length)];
        for (int i = 0; i < result.Length; i++)
            result[i] = (byte)(a[i] ^ b[i]);
        return result;
    }
}
