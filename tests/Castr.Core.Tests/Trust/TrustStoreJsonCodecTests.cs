using Castr.Core.Security;
using Castr.Core.Trust;

namespace Castr.Core.Tests.Trust;

public class TrustStoreJsonCodecTests
{
    [Fact]
    public void EncodeThenDecode_RoundTrips()
    {
        var entries = new[]
        {
            new TrustEntry(PublicKeyId.FromRawEd25519(new byte[32]), "build-server", TrustStatus.Trusted, DateTimeOffset.Parse("2026-01-01T00:00:00Z"), TrustEntrySource.PreDeployed),
        };

        var decoded = TrustStoreJsonCodec.Decode(TrustStoreJsonCodec.Encode(entries));

        Assert.Single(decoded);
        Assert.Equal(entries[0].PublicKeyId, decoded[0].PublicKeyId);
        Assert.Equal(entries[0].DisplayName, decoded[0].DisplayName);
        Assert.Equal(entries[0].Status, decoded[0].Status);
        Assert.Equal(entries[0].Source, decoded[0].Source);
    }

    [Fact]
    public void Decode_RealSeedFileTemplate_ParsesBothEntries()
    {
        // This is the actual shape of trusted-senders.seed.json committed at the repo root, including the
        // extra "$schema"/"comment" fields the codec must tolerate and ignore.
        const string json = """
        {
          "$schema": "https://raw.githubusercontent.com/dsteele/Castr/main/schemas/trusted-senders.seed.schema.json",
          "version": 1,
          "comment": "Example pre-deployment trust seed.",
          "entries": [
            {
              "publicKeyId": "ed25519:BASE64_ENCODED_32_BYTE_PUBLIC_KEY_GOES_HERE",
              "displayName": "example-build-server",
              "status": "trusted",
              "addedAt": "2026-01-01T00:00:00Z",
              "source": "pre-deployed"
            },
            {
              "publicKeyId": "ed25519:ANOTHER_BASE64_ENCODED_32_BYTE_PUBLIC_KEY",
              "displayName": "known-bad-actor-example",
              "status": "blocked",
              "addedAt": "2026-01-01T00:00:00Z",
              "source": "pre-deployed"
            }
          ]
        }
        """;

        var entries = TrustStoreJsonCodec.Decode(json);

        Assert.Equal(2, entries.Count);
        Assert.Equal(TrustStatus.Trusted, entries[0].Status);
        Assert.Equal("example-build-server", entries[0].DisplayName);
        Assert.Equal(TrustStatus.Blocked, entries[1].Status);
    }

    [Fact]
    public void Decode_UnknownStatus_Throws()
    {
        const string json = """{"version":1,"entries":[{"publicKeyId":"ed25519:AA==","displayName":"x","status":"maybe","addedAt":"2026-01-01T00:00:00Z","source":"manual"}]}""";

        Assert.Throws<InvalidDataException>(() => TrustStoreJsonCodec.Decode(json));
    }

    [Fact]
    public void Decode_MalformedJson_Throws_DoesNotSilentlyReturnEmpty()
    {
        // A corrupted/truncated trust store must fail loudly. Silently decoding to an empty store would
        // quietly drop previously-BLOCKED senders back to "unknown" — a fail-open downgrade.
        Assert.ThrowsAny<Exception>(() => TrustStoreJsonCodec.Decode("{ this is not valid json"));
    }

    [Fact]
    public void Decode_EntryMissingPublicKeyId_Throws()
    {
        const string json = """{"version":1,"entries":[{"displayName":"x","status":"trusted","addedAt":"2026-01-01T00:00:00Z","source":"manual"}]}""";

        Assert.Throws<InvalidDataException>(() => TrustStoreJsonCodec.Decode(json));
    }

    [Fact]
    public void Decode_MalformedPublicKeyId_IsInert_CannotAuthorizeARealSender()
    {
        // Hand-editing a bogus (non-ed25519) key id in as "trusted" doesn't crash decoding, but such an entry
        // can never match a genuine sender: every real sender id is a valid "ed25519:<base64>" string.
        const string json = """{"version":1,"entries":[{"publicKeyId":"totally-bogus","displayName":"x","status":"trusted","addedAt":"2026-01-01T00:00:00Z","source":"manual"}]}""";

        var entries = TrustStoreJsonCodec.Decode(json);

        Assert.Single(entries);
        Assert.Throws<FormatException>(() => entries[0].PublicKeyId.ToRawEd25519());
    }
}
