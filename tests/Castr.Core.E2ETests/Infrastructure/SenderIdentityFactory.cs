using Castr.Core.Security;
using NSec.Cryptography;

namespace Castr.Core.E2ETests.Infrastructure;

/// <summary>
/// Generates an Ed25519 sender identity in-test, in exactly the on-disk form the shipped CLI persists and
/// reads (<c>Castr.Cli.SenderIdentity</c> writes/reads a raw 32-byte Ed25519 private key). Knowing the
/// identity up front lets the harness (a) pre-configure every receiver to trust the sender via
/// <c>castr trust add &lt;id&gt;</c>, and (b) start all receivers <i>before</i> the sender, so they are already
/// listening when the sender broadcasts its one-shot ANNOUNCE/MANIFEST (there is no manifest re-request path,
/// so late joiners would never initialize — see Castr.Core.Protocol.ReceiverSession).
/// </summary>
internal static class SenderIdentityFactory
{
    /// <returns>
    /// <c>PrivateKey</c>: the raw 32-byte Ed25519 private key bytes to mount at the sender's
    /// <c>--identity</c> path. <c>PublicKeyId</c>: the <c>ed25519:&lt;base64&gt;</c> id receivers must trust.
    /// </returns>
    public static (byte[] PrivateKey, string PublicKeyId) Create()
    {
        using var key = Key.Create(
            SignatureAlgorithm.Ed25519,
            new KeyCreationParameters { ExportPolicy = KeyExportPolicies.AllowPlaintextExport });

        var privateKey = key.Export(KeyBlobFormat.RawPrivateKey);
        var publicKey = key.PublicKey.Export(KeyBlobFormat.RawPublicKey);
        return (privateKey, PublicKeyId.FromRawEd25519(publicKey).Value);
    }
}
