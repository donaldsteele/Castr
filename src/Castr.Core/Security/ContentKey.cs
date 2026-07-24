using System.Buffers.Binary;
using System.Security.Cryptography;
using NSec.Cryptography;
using Castr.Core.Manifest;

namespace Castr.Core.Security;

/// <summary>
/// A per-transfer symmetric "content key": a fresh random 256-bit key the sender generates once per
/// transfer and never reuses. Every chunk is encrypted under it with ChaCha20-Poly1305 (AEAD). Because
/// the key is fresh per transfer, the nonce can be a deterministic function of <c>(fileIndex, chunkIndex)</c>
/// without risking nonce reuse. The AAD binds each ciphertext to <c>(sessionId, fileIndex, chunkIndex)</c>
/// so a valid ciphertext cannot be replayed into a different position. See
/// wiki/synthesis/adr-0003-payload-encryption.md.
/// </summary>
public sealed class ContentKey : IDisposable
{
    /// <summary>256-bit content key.</summary>
    public const int SizeBytes = 32;

    private const int NonceSize = 12; // ChaCha20-Poly1305 nonce
    private const int TagSize = 16;   // Poly1305 tag
    private const int AadSize = TransferManifest.SessionIdSize + 4 + 4; // sessionId | fileIndex | chunkIndex

    private static readonly AeadAlgorithm Aead = AeadAlgorithm.ChaCha20Poly1305;

    private readonly Key _key;

    private ContentKey(Key key) => _key = key;

    /// <summary>Generates a fresh random content key. Use once per transfer.</summary>
    public static ContentKey Generate()
    {
        Span<byte> raw = stackalloc byte[SizeBytes];
        RandomNumberGenerator.Fill(raw);
        var key = Import(raw);
        CryptographicOperations.ZeroMemory(raw);
        return key;
    }

    /// <summary>Imports a raw 32-byte content key (e.g. after unwrapping a KEY_GRANT).</summary>
    public static ContentKey Import(ReadOnlySpan<byte> raw)
    {
        if (raw.Length != SizeBytes)
            throw new ArgumentException($"Content key must be exactly {SizeBytes} bytes, got {raw.Length}.", nameof(raw));

        var key = Key.Import(Aead, raw, KeyBlobFormat.RawSymmetricKey,
            new KeyCreationParameters { ExportPolicy = KeyExportPolicies.AllowPlaintextExport });
        return new ContentKey(key);
    }

    /// <summary>Exports the raw 32-byte key material — used only to wrap it for a KEY_GRANT.</summary>
    public byte[] Export() => _key.Export(KeyBlobFormat.RawSymmetricKey);

    /// <summary>Encrypts one chunk's plaintext, returning ciphertext+tag (plaintext length + 16).</summary>
    public byte[] EncryptChunk(ReadOnlySpan<byte> sessionId, int fileIndex, int chunkIndex, ReadOnlySpan<byte> plaintext)
    {
        Span<byte> nonce = stackalloc byte[NonceSize];
        BuildNonce(fileIndex, chunkIndex, nonce);
        Span<byte> aad = stackalloc byte[AadSize];
        BuildAad(sessionId, fileIndex, chunkIndex, aad);
        return Aead.Encrypt(_key, nonce, aad, plaintext);
    }

    /// <summary>
    /// Decrypts one chunk's ciphertext, returning the plaintext, or <c>null</c> if authentication fails
    /// (tampered ciphertext, or ciphertext replayed under a mismatched sessionId/fileIndex/chunkIndex).
    /// </summary>
    public byte[]? TryDecryptChunk(ReadOnlySpan<byte> sessionId, int fileIndex, int chunkIndex, ReadOnlySpan<byte> ciphertext)
    {
        if (ciphertext.Length < TagSize)
            return null;

        Span<byte> nonce = stackalloc byte[NonceSize];
        BuildNonce(fileIndex, chunkIndex, nonce);
        Span<byte> aad = stackalloc byte[AadSize];
        BuildAad(sessionId, fileIndex, chunkIndex, aad);

        var plaintext = new byte[ciphertext.Length - TagSize];
        return Aead.Decrypt(_key, nonce, aad, ciphertext, plaintext) ? plaintext : null;
    }

    private static void BuildNonce(int fileIndex, int chunkIndex, Span<byte> nonce)
    {
        // 12-byte deterministic nonce: fileIndex | chunkIndex | 0000. Unique per (file, chunk), which is
        // safe because the content key is fresh per transfer and never reused across transfers.
        nonce.Clear();
        BinaryPrimitives.WriteInt32BigEndian(nonce[..4], fileIndex);
        BinaryPrimitives.WriteInt32BigEndian(nonce[4..8], chunkIndex);
    }

    private static void BuildAad(ReadOnlySpan<byte> sessionId, int fileIndex, int chunkIndex, Span<byte> aad)
    {
        if (sessionId.Length != TransferManifest.SessionIdSize)
            throw new ArgumentException($"SessionId must be exactly {TransferManifest.SessionIdSize} bytes.", nameof(sessionId));
        sessionId.CopyTo(aad);
        BinaryPrimitives.WriteInt32BigEndian(aad.Slice(TransferManifest.SessionIdSize, 4), fileIndex);
        BinaryPrimitives.WriteInt32BigEndian(aad.Slice(TransferManifest.SessionIdSize + 4, 4), chunkIndex);
    }

    public void Dispose() => _key.Dispose();
}
