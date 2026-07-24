namespace Castr.Core.Security;

/// <summary>
/// A sender's trust identity: `ed25519:&lt;base64 raw public key&gt;`. Matches the format used in
/// trusted-senders.seed.json. Ed25519 public keys are only 32 bytes, so the raw key doubles as its
/// own stable identifier — no separate fingerprint hash is needed.
/// </summary>
public readonly record struct PublicKeyId(string Value)
{
    private const string Prefix = "ed25519:";

    public static PublicKeyId FromRawEd25519(ReadOnlySpan<byte> rawPublicKey)
    {
        if (rawPublicKey.Length != 32)
            throw new ArgumentException("Ed25519 public keys must be exactly 32 bytes.", nameof(rawPublicKey));
        return new PublicKeyId(Prefix + Convert.ToBase64String(rawPublicKey));
    }

    public byte[] ToRawEd25519()
    {
        if (!Value.StartsWith(Prefix, StringComparison.Ordinal))
            throw new FormatException($"'{Value}' is not an ed25519 public key id.");
        return Convert.FromBase64String(Value[Prefix.Length..]);
    }

    public override string ToString() => Value;
}
