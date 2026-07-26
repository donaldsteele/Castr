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
/// The full M1 stack (trust, manifest, chunking, Merkle verification, repair) driven over real UDP
/// multicast sockets with ChaosTransport-induced loss on the receiver side — real OS network stack
/// fidelity, complementing Castr.Core.Tests' InMemoryNetwork-based end-to-end tests.
/// </summary>
public class RealTransferRepairTests
{
    private static readonly TimeSpan OverallTimeout = TimeSpan.FromSeconds(20);

    [Fact]
    public async Task Transfer_OverRealSocketsWithInducedLoss_CompletesViaRepair()
    {
        var group = IPAddress.Parse("239.192.55.63");
        int port = TestPorts.FreeUdp();

        var originalBytes = new byte[40_000];
        new Random(11).NextBytes(originalBytes);
        const int chunkSize = 2000;

        using var key = ManifestSigner.CreateSigningKey();
        var sessionId = new byte[16];
        var source = new MemoryFileSource(originalBytes);
        using var contentKey = ContentKey.Generate();
        using var senderEncryptionKey = EncryptionKeys.Create();
        // Merkle tree over ciphertext chunk hashes (ADR-0003).
        var hashes = await EncryptedChunkHasher.ComputeAsync(source, chunkSize, sessionId, fileIndex: 0, contentKey);
        var tree = MerkleTree.Build(hashes);
        int chunkCount = ChunkLayout.ComputeChunkCount(originalBytes.Length, chunkSize);
        var manifest = new TransferManifest(
            sessionId, "real-socket-transfer", DateTimeOffset.UtcNow, EncryptionKeys.ExportPublicKey(senderEncryptionKey),
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
        // 15% of packets lost on the receive side — real socket, deterministic seeded loss on top.
        var lossyTransport = new ChaosTransport(rawReceiverTransport, lossProbability: 0.15, seed: 77);

        // Real clock, not FakeClock: this test runs over real wall-clock time (real sockets, real delays),
        // so RepairCoordinator's timeout-based retry needs time to actually advance to re-plan a repair
        // request whose response was also lost to the induced chaos. Shorter-than-default timeout so
        // enough retry rounds fit inside the test's overall time budget.
        //
        // NOTE: this is deliberately left on the shipped randomized jitter (RepairOptions defaults for
        // InitialRequestJitter and RetryJitterFraction) with an unseeded Random.Shared — it is one of only two
        // places the wall-clock jitter runs uncontrolled over a real socket, which is exactly the integration
        // coverage worth having. The margin is wide (a 2 s base timeout and up to 500 ms of first-request jitter
        // against a 20 s budget, measured at ~10x headroom), but the margin is empirical rather than structural:
        // if this ever flakes, suspect the jitter draw first and inject a seeded Random rather than widening the
        // timeout blindly.
        var peerTable = new PeerTable();
        var repairCoordinator = new RepairCoordinator(peerTable, SystemClock.Instance, new RepairOptions(TimeSpan.FromSeconds(2)));

        MemoryFileSink? sink = null;
        var receiver = new ReceiverSession(
            ReceiverId(1), trustStore, lossyTransport, SystemClock.Instance,
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

        Assert.True(receiver.IsComplete, "Transfer did not complete within the timeout despite repair.");
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
