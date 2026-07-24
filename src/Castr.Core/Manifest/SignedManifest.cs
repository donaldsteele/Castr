using Castr.Core.Security;

namespace Castr.Core.Manifest;

/// <summary>A manifest plus the sender's Ed25519 signature over its canonical encoding, and the raw public key that produced it.</summary>
public sealed record SignedManifest(TransferManifest Manifest, byte[] SenderPublicKey, byte[] Signature)
{
    public PublicKeyId SenderId => PublicKeyId.FromRawEd25519(SenderPublicKey);
}
