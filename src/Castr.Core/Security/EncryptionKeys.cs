using NSec.Cryptography;

namespace Castr.Core.Security;

/// <summary>
/// X25519 encryption-key helpers, deliberately kept separate from the Ed25519 signing keys created by
/// <see cref="Castr.Core.Manifest.ManifestSigner"/>. Every Castr identity holds both: Ed25519 for signing
/// the manifest / TOFU trust, and X25519 purely for the per-transfer content-key handshake
/// (JOIN_REQUEST/KEY_GRANT). Keys are never derived one from the other — see
/// wiki/synthesis/adr-0003-payload-encryption.md for why cross-protocol key reuse is avoided.
/// </summary>
public static class EncryptionKeys
{
    /// <summary>Raw X25519 public keys are 32 bytes, matching the on-the-wire encoding used by the manifest and JOIN_REQUEST.</summary>
    public const int PublicKeySize = 32;

    /// <summary>Creates a new X25519 key-agreement key. The caller owns its lifetime; only the raw public key is ever shared (in the manifest or a JOIN_REQUEST).</summary>
    public static Key Create() =>
        Key.Create(KeyAgreementAlgorithm.X25519, new KeyCreationParameters { ExportPolicy = KeyExportPolicies.AllowPlaintextExport });

    /// <summary>Exports the raw 32-byte X25519 public key for inclusion in a manifest or JOIN_REQUEST.</summary>
    public static byte[] ExportPublicKey(Key key) => key.PublicKey.Export(KeyBlobFormat.RawPublicKey);
}
