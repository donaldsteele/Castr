using NSec.Cryptography;

namespace Castr.Core.Manifest;

public static class ManifestSigner
{
    /// <summary>Creates a new Ed25519 signing key. Callers own its lifetime and should keep it out of the trust store (which only ever holds public keys).</summary>
    public static Key CreateSigningKey() =>
        Key.Create(SignatureAlgorithm.Ed25519, new KeyCreationParameters { ExportPolicy = KeyExportPolicies.AllowPlaintextExport });

    public static SignedManifest Sign(TransferManifest manifest, Key signingKey)
    {
        var encoded = ManifestCodec.Encode(manifest);
        var signature = SignatureAlgorithm.Ed25519.Sign(signingKey, encoded);
        var publicKey = signingKey.PublicKey.Export(KeyBlobFormat.RawPublicKey);
        return new SignedManifest(manifest, publicKey, signature);
    }
}
