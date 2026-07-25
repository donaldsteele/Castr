using System.Net;
using Castr.Core.Chunking;
using Castr.Core.Manifest;
using Castr.Core.Protocol;
using Castr.Core.Security;
using Castr.Core.Time;
using Castr.Core.Transport;
using Castr.Core.Transport.Udp;
using Castr.Core.Trust;

namespace Castr.Core.IntegrationTests;

/// <summary>
/// Regression coverage for the M3 two-level-chunking fix. Before packetization, a chunk whose encrypted
/// envelope exceeded ~65,507 bytes threw <c>SocketException(MessageSize)</c> on send and left an
/// already-listening receiver hanging forever with a 0-byte .part file. These tests drive chunk sizes at and
/// well above that old ceiling (256 KB — the documented default, and multi-chunk) over real loopback UDP and
/// confirm the transfer now completes byte-identically, including under induced sub-chunk packet loss where
/// recovery relies on the existing chunk-level repair path re-requesting whole chunks.
/// </summary>
public class LargeChunkTransferTests
{
    private static readonly TimeSpan OverallTimeout = TimeSpan.FromSeconds(30);

    [Fact]
    public async Task LargeChunkSize_OverRealSockets_CompletesByteIdentical()
    {
        // 3 chunks of 256 KB each => hundreds of MTU-safe wire packets, every one far past the old
        // single-datagram ceiling this fix removes.
        var group = IPAddress.Parse("239.192.55.64");
        const int port = 45005;
        const int chunkSize = 262_144; // 256 KiB, the documented default
        var originalBytes = new byte[chunkSize * 2 + 50_000];
        new Random(23).NextBytes(originalBytes);

        await RunTransferAsync(group, port, chunkSize, originalBytes, lossProbability: 0.0);
    }

    [Fact]
    public async Task LargeChunkSize_WithSubChunkPacketLoss_RecoversViaChunkLevelRepair()
    {
        // 10% of wire packets dropped: most 256 KB chunks now lose at least one of their many fragments, so
        // this exercises the design's core claim — a chunk missing any packet stays incomplete and the
        // existing whole-chunk repair recovers it, with no packet-level NACK machinery.
        var group = IPAddress.Parse("239.192.55.65");
        const int port = 45006;
        const int chunkSize = 262_144;
        var originalBytes = new byte[chunkSize + 20_000];
        new Random(29).NextBytes(originalBytes);

        await RunTransferAsync(group, port, chunkSize, originalBytes, lossProbability: 0.10);
    }

    private static async Task RunTransferAsync(IPAddress group, int port, int chunkSize, byte[] originalBytes, double lossProbability)
    {
        using var key = ManifestSigner.CreateSigningKey();
        var sessionId = new byte[16];
        var source = new MemoryFileSource(originalBytes);
        using var contentKey = ContentKey.Generate();
        using var senderEncryptionKey = EncryptionKeys.Create();
        var hashes = await EncryptedChunkHasher.ComputeAsync(source, chunkSize, sessionId, fileIndex: 0, contentKey);
        var tree = MerkleTree.Build(hashes);
        int chunkCount = ChunkLayout.ComputeChunkCount(originalBytes.Length, chunkSize);
        var manifest = new TransferManifest(
            sessionId, "large-chunk-transfer", DateTimeOffset.UtcNow, EncryptionKeys.ExportPublicKey(senderEncryptionKey),
            [new ManifestFileEntry("payload.bin", originalBytes.Length, chunkSize, chunkCount, tree.Root)]);
        var signed = ManifestSigner.Sign(manifest, key);

        await using IMulticastTransport senderTransport = new UdpMulticastTransport(group, port);
        var sender = new SenderSession(
            signed,
            new Dictionary<int, IFileSource> { [0] = source },
            new Dictionary<int, MerkleTree> { [0] = tree },
            senderTransport,
            senderEncryptionKey,
            contentKey);

        var trustStore = new InMemoryTrustStore();
        var publicKey = key.PublicKey.Export(NSec.Cryptography.KeyBlobFormat.RawPublicKey);
        trustStore.Upsert(new TrustEntry(PublicKeyId.FromRawEd25519(publicKey), "sender", TrustStatus.Trusted, DateTimeOffset.UnixEpoch, TrustEntrySource.Manual));

        await using IMulticastTransport rawReceiverTransport = new UdpMulticastTransport(group, port);
        IMulticastTransport receiverTransport = lossProbability > 0
            ? new ChaosTransport(rawReceiverTransport, lossProbability, seed: 91)
            : rawReceiverTransport;

        var peerTable = new PeerTable();
        // Deliberately on the shipped randomized jitter with an unseeded Random.Shared, over a real socket and a
        // real clock — together with RealTransferRepairTests this is the only integration coverage of the wall-
        // clock jitter behaving under genuine timing. Headroom is empirical (~10x measured), not structural: if
        // this flakes, suspect the jitter draw before widening the timeout.
        var repairCoordinator = new RepairCoordinator(peerTable, SystemClock.Instance, new RepairOptions(TimeSpan.FromSeconds(2)));

        MemoryFileSink? sink = null;
        var receiver = new ReceiverSession(
            ReceiverId(1), trustStore, receiverTransport, SystemClock.Instance,
            new ReceiverSessionOptions("/root"),
            sinkFactory: (_, length) => sink = new MemoryFileSink((int)length),
            peerTable, repairCoordinator);

        using var cts = new CancellationTokenSource(OverallTimeout);
        var senderTask = sender.RunAsync(cts.Token);
        var receiverTask = receiver.RunAsync(cts.Token);
        var repairLoopTask = RunRepairLoopAsync(receiver, cts.Token);

        while (!receiver.IsComplete && !cts.IsCancellationRequested)
            await Task.Delay(30, CancellationToken.None);

        await cts.CancelAsync();
        await Swallow(senderTask);
        await Swallow(receiverTask);
        await Swallow(repairLoopTask);

        Assert.True(receiver.IsComplete, "Large-chunk transfer did not complete within the timeout.");
        Assert.Equal(originalBytes, sink!.ToArray());
    }

    private static async Task RunRepairLoopAsync(ReceiverSession receiver, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            if (!receiver.IsComplete)
                await receiver.RequestRepairsAsync(ct);
            await Task.Delay(100, ct);
        }
    }

    private static async Task Swallow(Task task)
    {
        try { await task; }
        catch (OperationCanceledException) { }
    }

    private static byte[] ReceiverId(byte fill)
    {
        var bytes = new byte[16];
        Array.Fill(bytes, fill);
        return bytes;
    }
}
