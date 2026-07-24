using System.Security.Cryptography;
using Castr.Core.Chunking;
using Castr.Core.Security;
using NSec.Cryptography;

namespace Castr.Core.Tests.Security;

/// <summary>
/// Unit coverage for the ADR-0003 payload-encryption primitives: X25519 key agreement, the HKDF-SHA256 +
/// ChaCha20-Poly1305 content-key wrap/unwrap, and per-chunk AEAD encrypt/decrypt with nonce/AAD position
/// binding. Verifies both the happy path and that tampering / position-confusion is rejected.
/// </summary>
public class PayloadEncryptionTests
{
    private static readonly byte[] SessionId = Enumerable.Range(0, 16).Select(i => (byte)i).ToArray();
    private static readonly byte[] ReceiverId = Enumerable.Range(0, 16).Select(i => (byte)(100 + i)).ToArray();

    [Fact]
    public void X25519_Agreement_IsSymmetric()
    {
        using var alice = EncryptionKeys.Create();
        using var bob = EncryptionKeys.Create();

        using var fromAlice = KeyAgreementAlgorithm.X25519.Agree(alice, bob.PublicKey)!;
        using var fromBob = KeyAgreementAlgorithm.X25519.Agree(bob, alice.PublicKey)!;

        Assert.NotNull(fromAlice);
        Assert.NotNull(fromBob);
        // Both sides derive the same wrapping-key material from their (identical) shared secret.
        var keyA = KeyDerivationAlgorithm.HkdfSha256.DeriveBytes(fromAlice, SessionId, "castr/test"u8, 32);
        var keyB = KeyDerivationAlgorithm.HkdfSha256.DeriveBytes(fromBob, SessionId, "castr/test"u8, 32);
        Assert.Equal(keyA, keyB);
    }

    [Fact]
    public void Hkdf_IsDeterministic_ForSameInputs_DiffersForDifferentSalt()
    {
        using var alice = EncryptionKeys.Create();
        using var bob = EncryptionKeys.Create();
        using var shared = KeyAgreementAlgorithm.X25519.Agree(alice, bob.PublicKey)!;

        var a1 = KeyDerivationAlgorithm.HkdfSha256.DeriveBytes(shared, SessionId, "info"u8, 44);
        var a2 = KeyDerivationAlgorithm.HkdfSha256.DeriveBytes(shared, SessionId, "info"u8, 44);
        var differentSalt = KeyDerivationAlgorithm.HkdfSha256.DeriveBytes(shared, new byte[16], "info"u8, 44);

        Assert.Equal(a1, a2);
        Assert.NotEqual(a1, differentSalt);
    }

    [Fact]
    public void ContentKeyWrap_RoundTrip_RecoversExactKey()
    {
        using var senderKey = EncryptionKeys.Create();
        using var receiverKey = EncryptionKeys.Create();
        using var contentKey = ContentKey.Generate();
        var raw = contentKey.Export();

        var wrapped = ContentKeyWrap.Wrap(
            senderKey, EncryptionKeys.ExportPublicKey(receiverKey), SessionId, ReceiverId, raw);
        var unwrapped = ContentKeyWrap.TryUnwrap(
            receiverKey, EncryptionKeys.ExportPublicKey(senderKey), SessionId, ReceiverId, wrapped);

        Assert.Equal(ContentKeyWrap.WrappedSize, wrapped.Length); // 32 + 16-byte tag
        Assert.Equal(48, wrapped.Length);
        Assert.NotNull(unwrapped);
        Assert.Equal(raw, unwrapped);
    }

    [Fact]
    public void ContentKeyWrap_WrongReceiverKey_FailsToUnwrap()
    {
        using var senderKey = EncryptionKeys.Create();
        using var receiverKey = EncryptionKeys.Create();
        using var attackerKey = EncryptionKeys.Create();
        using var contentKey = ContentKey.Generate();

        var wrapped = ContentKeyWrap.Wrap(
            senderKey, EncryptionKeys.ExportPublicKey(receiverKey), SessionId, ReceiverId, contentKey.Export());

        // An eavesdropper with a different X25519 key derives a different shared secret and cannot unwrap.
        var unwrapped = ContentKeyWrap.TryUnwrap(
            attackerKey, EncryptionKeys.ExportPublicKey(senderKey), SessionId, ReceiverId, wrapped);

        Assert.Null(unwrapped);
    }

    [Fact]
    public void ContentKeyWrap_TamperedCiphertext_FailsToUnwrap()
    {
        using var senderKey = EncryptionKeys.Create();
        using var receiverKey = EncryptionKeys.Create();
        using var contentKey = ContentKey.Generate();

        var wrapped = ContentKeyWrap.Wrap(
            senderKey, EncryptionKeys.ExportPublicKey(receiverKey), SessionId, ReceiverId, contentKey.Export());
        wrapped[0] ^= 0xFF;

        var unwrapped = ContentKeyWrap.TryUnwrap(
            receiverKey, EncryptionKeys.ExportPublicKey(senderKey), SessionId, ReceiverId, wrapped);

        Assert.Null(unwrapped);
    }

    [Fact]
    public void ContentKeyWrap_MismatchedReceiverIdAad_FailsToUnwrap()
    {
        using var senderKey = EncryptionKeys.Create();
        using var receiverKey = EncryptionKeys.Create();
        using var contentKey = ContentKey.Generate();

        var wrapped = ContentKeyWrap.Wrap(
            senderKey, EncryptionKeys.ExportPublicKey(receiverKey), SessionId, ReceiverId, contentKey.Export());

        var otherReceiverId = new byte[16];
        var unwrapped = ContentKeyWrap.TryUnwrap(
            receiverKey, EncryptionKeys.ExportPublicKey(senderKey), SessionId, otherReceiverId, wrapped);

        Assert.Null(unwrapped);
    }

    [Fact]
    public void ChunkEncryptDecrypt_RoundTrips()
    {
        using var contentKey = ContentKey.Generate();
        var plaintext = RandomBytes(4096);

        var ciphertext = contentKey.EncryptChunk(SessionId, fileIndex: 2, chunkIndex: 7, plaintext);
        var recovered = contentKey.TryDecryptChunk(SessionId, fileIndex: 2, chunkIndex: 7, ciphertext);

        Assert.Equal(plaintext.Length + 16, ciphertext.Length); // + Poly1305 tag
        Assert.NotEqual(plaintext, ciphertext[..plaintext.Length]); // actually encrypted, not passthrough
        Assert.NotNull(recovered);
        Assert.Equal(plaintext, recovered);
    }

    [Fact]
    public void ChunkDecrypt_TamperedCiphertextByte_ReturnsNull()
    {
        using var contentKey = ContentKey.Generate();
        var ciphertext = contentKey.EncryptChunk(SessionId, 0, 0, RandomBytes(1000));

        ciphertext[5] ^= 0x01;

        Assert.Null(contentKey.TryDecryptChunk(SessionId, 0, 0, ciphertext));
    }

    [Theory]
    [InlineData(1, 0, 0)]  // wrong file index
    [InlineData(0, 1, 0)]  // wrong chunk index
    [InlineData(0, 0, 99)] // wrong session id
    public void ChunkDecrypt_WrongAad_ReturnsNull(int fileIndex, int chunkIndex, int sessionFill)
    {
        using var contentKey = ContentKey.Generate();
        var plaintext = RandomBytes(500);
        var ciphertext = contentKey.EncryptChunk(SessionId, fileIndex: 0, chunkIndex: 0, plaintext);

        var wrongSession = sessionFill == 99 ? Enumerable.Repeat((byte)0xAB, 16).ToArray() : SessionId;
        var recovered = contentKey.TryDecryptChunk(wrongSession, fileIndex, chunkIndex, ciphertext);

        Assert.Null(recovered);
    }

    [Fact]
    public void ChunkEncrypt_DifferentPositions_ProduceDifferentCiphertext()
    {
        using var contentKey = ContentKey.Generate();
        var plaintext = RandomBytes(256);

        var c00 = contentKey.EncryptChunk(SessionId, 0, 0, plaintext);
        var c01 = contentKey.EncryptChunk(SessionId, 0, 1, plaintext);
        var c10 = contentKey.EncryptChunk(SessionId, 1, 0, plaintext);

        Assert.NotEqual(c00, c01); // distinct nonce/AAD per chunk index
        Assert.NotEqual(c00, c10); // distinct nonce/AAD per file index
    }

    [Fact]
    public async Task EncryptedChunkHasher_HashesMatchSenderEncryption()
    {
        using var contentKey = ContentKey.Generate();
        var bytes = RandomBytes(5000);
        const int chunkSize = 1000;
        var source = new MemoryFileSource(bytes);

        var hashes = await EncryptedChunkHasher.ComputeAsync(source, chunkSize, SessionId, fileIndex: 3, contentKey);

        // The hasher must hash exactly what an independent EncryptChunk of the same plaintext produces.
        for (int i = 0; i < hashes.Length; i++)
        {
            var range = ChunkLayout.GetRange(bytes.Length, chunkSize, i);
            var expected = ChunkHash.Compute(contentKey.EncryptChunk(SessionId, 3, i, bytes.AsSpan((int)range.Offset, range.Length)));
            Assert.Equal(expected, hashes[i]);
        }
    }

    [Fact]
    public void ContentKey_Generate_ProducesDistinctKeys()
    {
        using var a = ContentKey.Generate();
        using var b = ContentKey.Generate();

        Assert.Equal(ContentKey.SizeBytes, a.Export().Length);
        Assert.NotEqual(a.Export(), b.Export());
    }

    private static byte[] RandomBytes(int length)
    {
        var bytes = new byte[length];
        RandomNumberGenerator.Fill(bytes);
        return bytes;
    }
}
