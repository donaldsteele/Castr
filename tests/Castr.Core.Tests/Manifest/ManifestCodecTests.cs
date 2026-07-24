using Castr.Core.Chunking;
using Castr.Core.Manifest;

namespace Castr.Core.Tests.Manifest;

public class ManifestCodecTests
{
    [Fact]
    public void EncodeThenDecode_RoundTripsExactly()
    {
        var manifest = SampleManifest();

        var encoded = ManifestCodec.Encode(manifest);
        var decoded = ManifestCodec.Decode(encoded);

        Assert.Equal(manifest.SessionId, decoded.SessionId);
        Assert.Equal(manifest.TransferName, decoded.TransferName);
        Assert.Equal(manifest.IssuedAt.ToUnixTimeSeconds(), decoded.IssuedAt.ToUnixTimeSeconds());
        Assert.Equal(manifest.Files.Count, decoded.Files.Count);
        for (int i = 0; i < manifest.Files.Count; i++)
        {
            Assert.Equal(manifest.Files[i].RelativePath, decoded.Files[i].RelativePath);
            Assert.Equal(manifest.Files[i].Size, decoded.Files[i].Size);
            Assert.Equal(manifest.Files[i].ChunkSize, decoded.Files[i].ChunkSize);
            Assert.Equal(manifest.Files[i].ChunkCount, decoded.Files[i].ChunkCount);
            Assert.Equal(manifest.Files[i].MerkleRoot, decoded.Files[i].MerkleRoot);
        }
    }

    [Fact]
    public void Encode_IsDeterministic()
    {
        var manifest = SampleManifest();

        Assert.Equal(ManifestCodec.Encode(manifest), ManifestCodec.Encode(manifest));
    }

    [Fact]
    public void Encode_DifferentTransferName_ProducesDifferentBytes()
    {
        var a = SampleManifest();
        var b = a with { TransferName = "different-name" };

        Assert.NotEqual(ManifestCodec.Encode(a), ManifestCodec.Encode(b));
    }

    [Fact]
    public void Encode_UnicodeTransferName_RoundTrips()
    {
        var manifest = SampleManifest() with { TransferName = "ééé 日本語 😀" };

        var decoded = ManifestCodec.Decode(ManifestCodec.Encode(manifest));

        Assert.Equal(manifest.TransferName, decoded.TransferName);
    }

    [Fact]
    public void Encode_ZeroFiles_RoundTrips()
    {
        var manifest = new TransferManifest(
            SessionId: new byte[16],
            TransferName: "empty-transfer",
            IssuedAt: DateTimeOffset.UnixEpoch,
            Files: []);

        var decoded = ManifestCodec.Decode(ManifestCodec.Encode(manifest));

        Assert.Empty(decoded.Files);
    }

    [Fact]
    public void Encode_WrongSessionIdLength_Throws()
    {
        var manifest = SampleManifest() with { SessionId = new byte[4] };

        Assert.Throws<ArgumentException>(() => ManifestCodec.Encode(manifest));
    }

    [Fact]
    public void Decode_UnsupportedVersion_Throws()
    {
        var encoded = ManifestCodec.Encode(SampleManifest());
        encoded[0] = 0xFF;

        Assert.Throws<InvalidDataException>(() => ManifestCodec.Decode(encoded));
    }

    private static TransferManifest SampleManifest() => new(
        SessionId: Enumerable.Range(0, 16).Select(i => (byte)i).ToArray(),
        TransferName: "photos.zip",
        IssuedAt: DateTimeOffset.Parse("2026-07-24T12:00:00Z"),
        Files:
        [
            new ManifestFileEntry("photos/beach.jpg", 4_500_000, 262_144, 18, ChunkHash.Compute("root-a"u8)),
            new ManifestFileEntry("photos/mountains.jpg", 6_100_000, 262_144, 24, ChunkHash.Compute("root-b"u8)),
        ]);
}
