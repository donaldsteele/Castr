using Castr.Core.Chunking;
using Castr.Core.Manifest;
using Castr.Core.Protocol;
using Castr.Core.Security;
using Castr.Core.Time;
using Castr.Core.Transport;
using Castr.Core.Transport.InMemory;
using Castr.Core.Trust;
using Castr.Tui;
using NSec.Cryptography;
using Spectre.Console.Testing;

namespace Castr.Tui.Tests;

/// <summary>
/// Drives the dashboard against a real <see cref="SenderSession"/>/<see cref="ReceiverSession"/> pair wired
/// over <see cref="InMemoryNetwork"/> (the same transport the Core end-to-end tests use), confirming it
/// renders sensible output for an actual transfer rather than only synthetic snapshots.
/// </summary>
public class TransferDashboardEndToEndTests
{
    [Fact]
    public async Task Dashboard_Follows_A_Real_Transfer_To_Completion()
    {
        var originalBytes = RandomBytes(seed: 42, length: 40_000);
        using var signingKey = ManifestSigner.CreateSigningKey();
        var transfer = BuildTransfer(signingKey, "capstone.bin", originalBytes, chunkSize: 2048);

        var network = new InMemoryNetwork();
        var sender = new SenderSession(
            transfer.Signed, transfer.Sources, transfer.Trees,
            network.CreateMulticastTransport(new Endpoint("sender", 1)),
            transfer.SenderEncryptionKey, transfer.ContentKey);

        var (getSink, sinkFactory) = MemorySinkFactory();
        var receiver = new ReceiverSession(
            ReceiverId(1), TrustedStoreFor(signingKey),
            network.CreateMulticastTransport(new Endpoint("receiver", 1)),
            new FakeClock(DateTimeOffset.UtcNow),
            new ReceiverSessionOptions("/virtual-root"), sinkFactory);

        var console = new TestConsole();
        var dashboard = new TransferDashboard(console, TimeSpan.FromMilliseconds(20));

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));

        // Start the dashboard first: RunAsync subscribes to ProgressChanged synchronously before it awaits,
        // so no early progress events are missed even for a fast transfer. This is the intended CLI ordering.
        var dashboardTask = dashboard.RunAsync(receiver, cts.Token);
        var senderTask = sender.RunAsync(cts.Token);
        var receiverTask = receiver.RunAsync(cts.Token);
        var repairTask = RepairLoopAsync(receiver, cts.Token);

        // The dashboard returns when the receiver reports completion.
        await dashboardTask;

        await cts.CancelAsync();
        await SwallowCancellation(senderTask);
        await SwallowCancellation(receiverTask);
        await SwallowCancellation(repairTask);

        Assert.True(receiver.IsComplete, "transfer did not complete");
        Assert.Equal(originalBytes, getSink().ToArray());

        // The captured console output reflects a completed transfer.
        Assert.Contains("test-transfer", console.Output); // manifest transfer name
        Assert.Contains("Completed", console.Output);
        Assert.Contains("100%", console.Output);
    }

    // ---- helpers (trimmed from Castr.Core.Tests' EndToEndTransferTests) ----

    private sealed record Transfer(
        SignedManifest Signed,
        Dictionary<int, IFileSource> Sources,
        Dictionary<int, MerkleTree> Trees,
        Key SenderEncryptionKey,
        ContentKey ContentKey);

    private static Transfer BuildTransfer(Key signingKey, string relativePath, byte[] bytes, int chunkSize)
    {
        var sessionId = new byte[16];
        var source = new MemoryFileSource(bytes);
        var contentKey = ContentKey.Generate();
        var senderEncryptionKey = EncryptionKeys.Create();

        var hashes = EncryptedChunkHasher
            .ComputeAsync(source, chunkSize, sessionId, fileIndex: 0, contentKey)
            .GetAwaiter().GetResult();
        var tree = MerkleTree.Build(hashes);
        int chunkCount = ChunkLayout.ComputeChunkCount(bytes.Length, chunkSize);

        var manifest = new TransferManifest(
            sessionId, "test-transfer", DateTimeOffset.UtcNow,
            EncryptionKeys.ExportPublicKey(senderEncryptionKey),
            [new ManifestFileEntry(relativePath, bytes.Length, chunkSize, chunkCount, tree.Root)]);
        var signed = ManifestSigner.Sign(manifest, signingKey);

        return new Transfer(
            signed,
            new Dictionary<int, IFileSource> { [0] = source },
            new Dictionary<int, MerkleTree> { [0] = tree },
            senderEncryptionKey,
            contentKey);
    }

    private static ITrustStore TrustedStoreFor(Key key)
    {
        var store = new InMemoryTrustStore();
        var publicKey = key.PublicKey.Export(KeyBlobFormat.RawPublicKey);
        store.Upsert(new TrustEntry(
            PublicKeyId.FromRawEd25519(publicKey), "test-sender", TrustStatus.Trusted,
            DateTimeOffset.UnixEpoch, TrustEntrySource.Manual));
        return store;
    }

    private static (Func<MemoryFileSink> GetSink, Func<string, long, IFileSink> Factory) MemorySinkFactory()
    {
        MemoryFileSink? sink = null;
        Func<string, long, IFileSink> factory = (_, length) =>
        {
            sink = new MemoryFileSink((int)length);
            return sink;
        };
        return (() => sink!, factory);
    }

    private static async Task RepairLoopAsync(ReceiverSession receiver, CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested && !receiver.IsComplete)
            {
                await receiver.RequestRepairsAsync(ct);
                await Task.Delay(50, ct);
            }
        }
        catch (OperationCanceledException) { }
    }

    private static async Task SwallowCancellation(Task task)
    {
        try { await task; }
        catch (OperationCanceledException) { }
    }

    private static byte[] RandomBytes(int seed, int length)
    {
        var bytes = new byte[length];
        new Random(seed).NextBytes(bytes);
        return bytes;
    }

    private static byte[] ReceiverId(byte fill)
    {
        var bytes = new byte[16];
        Array.Fill(bytes, fill);
        return bytes;
    }
}
