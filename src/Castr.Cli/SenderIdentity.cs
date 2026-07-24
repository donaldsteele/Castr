using NSec.Cryptography;
using Castr.Core.Manifest;
using Castr.Core.Security;

namespace Castr.Cli;

/// <summary>
/// A persistent sender identity: the long-lived Ed25519 signing key whose public half is a sender's
/// <see cref="PublicKeyId"/> in every receiver's trust store. Persisting it (rather than generating a fresh
/// key each run) is what makes "trust this sender once" durable across sends. The raw 32-byte private key is
/// written to a file the user owns; the per-transfer X25519 encryption key is separate and generated fresh
/// per transfer (see <see cref="TransferPreparation"/>), so it is never persisted here.
/// </summary>
internal sealed class SenderIdentity : IDisposable
{
    public Key SigningKey { get; }

    public PublicKeyId PublicKeyId { get; }

    private SenderIdentity(Key signingKey)
    {
        SigningKey = signingKey;
        PublicKeyId = PublicKeyId.FromRawEd25519(signingKey.PublicKey.Export(KeyBlobFormat.RawPublicKey));
    }

    /// <summary>Loads the identity at <paramref name="path"/>, generating and persisting a new one if none exists.</summary>
    public static SenderIdentity LoadOrCreate(string path)
    {
        if (File.Exists(path))
        {
            var raw = File.ReadAllBytes(path);
            var key = Key.Import(
                SignatureAlgorithm.Ed25519, raw, KeyBlobFormat.RawPrivateKey,
                new KeyCreationParameters { ExportPolicy = KeyExportPolicies.AllowPlaintextExport });
            return new SenderIdentity(key);
        }

        var created = ManifestSigner.CreateSigningKey();
        var directory = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);
        File.WriteAllBytes(path, created.Export(KeyBlobFormat.RawPrivateKey));
        return new SenderIdentity(created);
    }

    public void Dispose() => SigningKey.Dispose();
}
