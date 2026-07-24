using System.Text;
using NSec.Cryptography;
using Castr.Core.Manifest;

namespace Castr.Core.Security;

/// <summary>
/// Confidential per-receiver distribution of the transfer's <see cref="ContentKey"/> (the KEY_GRANT payload).
/// The sender and each receiver perform a static-static X25519 ECDH, derive a one-time wrapping key with
/// HKDF-SHA256 (salted by the random per-transfer session ID, so the wrapping key differs every transfer
/// even though the ECDH shared secret between the same two identities is stable), and wrap the content key
/// with ChaCha20-Poly1305. The wrap's AAD binds it to the granting receiver's ID. See
/// wiki/synthesis/adr-0003-payload-encryption.md.
/// </summary>
public static class ContentKeyWrap
{
    private const int WrapKeySize = 32;
    private const int NonceSize = 12;
    private const int TagSize = 16;

    /// <summary>Wrapped payload = ChaCha20-Poly1305(contentKey) → 32 + 16 = 48 bytes.</summary>
    public const int WrappedSize = ContentKey.SizeBytes + TagSize;

    private static readonly AeadAlgorithm Aead = AeadAlgorithm.ChaCha20Poly1305;
    private static readonly KeyDerivationAlgorithm Hkdf = KeyDerivationAlgorithm.HkdfSha256;
    private static readonly KeyAgreementAlgorithm Ecdh = KeyAgreementAlgorithm.X25519;
    private static readonly byte[] HkdfInfo = Encoding.ASCII.GetBytes("castr/keygrant/v1");

    /// <summary>Sender side: wraps <paramref name="contentKey"/> for a specific receiver identified by its X25519 public key.</summary>
    public static byte[] Wrap(
        Key senderEncryptionKey,
        ReadOnlySpan<byte> receiverPublicKey,
        ReadOnlySpan<byte> sessionId,
        ReadOnlySpan<byte> receiverId,
        ReadOnlySpan<byte> contentKey)
    {
        var receiverPub = PublicKey.Import(Ecdh, receiverPublicKey, KeyBlobFormat.RawPublicKey);
        using var shared = Ecdh.Agree(senderEncryptionKey, receiverPub)
            ?? throw new System.Security.Cryptography.CryptographicException("X25519 key agreement failed.");

        Span<byte> wrapKey = stackalloc byte[WrapKeySize];
        Span<byte> nonce = stackalloc byte[NonceSize];
        DeriveWrapKey(shared, sessionId, wrapKey, nonce);

        using var aeadKey = Key.Import(Aead, wrapKey, KeyBlobFormat.RawSymmetricKey);
        return Aead.Encrypt(aeadKey, nonce, receiverId, contentKey);
    }

    /// <summary>Receiver side: reverses the ECDH and unwraps the content key, or returns <c>null</c> if authentication fails.</summary>
    public static byte[]? TryUnwrap(
        Key receiverEncryptionKey,
        ReadOnlySpan<byte> senderPublicKey,
        ReadOnlySpan<byte> sessionId,
        ReadOnlySpan<byte> receiverId,
        ReadOnlySpan<byte> wrapped)
    {
        if (wrapped.Length != WrappedSize)
            return null;

        try
        {
            var senderPub = PublicKey.Import(Ecdh, senderPublicKey, KeyBlobFormat.RawPublicKey);
            using var shared = Ecdh.Agree(receiverEncryptionKey, senderPub);
            if (shared is null)
                return null;

            Span<byte> wrapKey = stackalloc byte[WrapKeySize];
            Span<byte> nonce = stackalloc byte[NonceSize];
            DeriveWrapKey(shared, sessionId, wrapKey, nonce);

            using var aeadKey = Key.Import(Aead, wrapKey, KeyBlobFormat.RawSymmetricKey);
            var contentKey = new byte[ContentKey.SizeBytes];
            return Aead.Decrypt(aeadKey, nonce, receiverId, wrapped, contentKey) ? contentKey : null;
        }
        catch (Exception ex) when (ex is ArgumentException or FormatException)
        {
            // Malformed public-key bytes from a hostile or buggy peer — not a crash.
            return null;
        }
    }

    private static void DeriveWrapKey(SharedSecret shared, ReadOnlySpan<byte> sessionId, Span<byte> wrapKey, Span<byte> nonce)
    {
        Span<byte> okm = stackalloc byte[WrapKeySize + NonceSize];
        Hkdf.DeriveBytes(shared, sessionId, HkdfInfo, okm);
        okm[..WrapKeySize].CopyTo(wrapKey);
        okm.Slice(WrapKeySize, NonceSize).CopyTo(nonce);
    }
}
