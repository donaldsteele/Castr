using NSec.Cryptography;

namespace Castr.Gui.Services;

/// <summary>
/// Loads (or first-run creates and persists) this installation's long-lived Ed25519 manifest-signing
/// identity. A stable identity is what makes receiver-side Trust-On-First-Use meaningful across restarts: a
/// receiver that trusted us once keeps trusting the same key. The private key never leaves this machine and
/// is never placed in a trust store (which only ever holds public keys).
/// </summary>
public static class CastrIdentity
{
    public static Key LoadOrCreateSigningKey(string path)
    {
        var creation = new KeyCreationParameters { ExportPolicy = KeyExportPolicies.AllowPlaintextExport };

        if (File.Exists(path))
        {
            var raw = File.ReadAllBytes(path);
            try
            {
                return Key.Import(SignatureAlgorithm.Ed25519, raw, KeyBlobFormat.RawPrivateKey, creation);
            }
            catch
            {
                // Corrupt/incompatible key file — fall through and mint a fresh identity rather than crash.
            }
        }

        var key = Key.Create(SignatureAlgorithm.Ed25519, creation);
        var directory = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);
        File.WriteAllBytes(path, key.Export(KeyBlobFormat.RawPrivateKey));
        return key;
    }
}
