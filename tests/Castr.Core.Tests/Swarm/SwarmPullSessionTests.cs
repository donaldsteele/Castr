using Castr.Core.Chunking;
using Castr.Core.Manifest;
using Castr.Core.Protocol;
using Castr.Core.Security;
using Castr.Core.Swarm;
using Castr.Core.Time;
using Castr.Core.Transport;
using Castr.Core.Transport.InMemory;
using Castr.Core.Trust;

namespace Castr.Core.Tests.Swarm;

/// <summary>
/// Unit coverage for the unicast-swarm (mobile TCP pull) path over the in-memory stream transport — no
/// sockets. Proves a SwarmPullSession pulls, verifies (Merkle + AEAD), decrypts, and writes byte-identically,
/// runs the same TOFU trust gate as ReceiverSession, resumes from a partial pull, offloads chunk fetching to a
/// peer receiver while getting the key from the sender, and rejects tampered / position-swapped chunks.
/// </summary>
public class SwarmPullSessionTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(15);
    private static readonly Endpoint SenderEndpoint = new("sender", 1);
    private static readonly Endpoint PeerEndpoint = new("peer", 1);

    [Fact]
    public async Task PullFromSender_HappyPath_ReceivesByteIdenticalFile()
    {
        var original = RandomBytes(1, 40_000);
        using var key = ManifestSigner.CreateSigningKey();
        var transfer = BuildTransfer(key, "photo.jpg", original, chunkSize: 4096);

        var network = new InMemoryStreamNetwork();
        await using var serve = StartServe(network, SenderEndpoint, transfer.Sender.CreateSwarmContentSource());

        var (sink, sinkFactory) = MemorySinkFactory();
        using var pull = NewPull(network, TrustedStoreFor(key), sinkFactory);

        using var cts = new CancellationTokenSource(Timeout);
        bool accepted = await pull.PullFromAsync(SenderEndpoint, cts.Token);

        Assert.True(accepted);
        Assert.True(pull.IsComplete);
        Assert.Equal(original, sink().ToArray());
    }

    [Fact]
    public async Task PullFromSender_SecondPull_IsIdempotent()
    {
        var original = RandomBytes(11, 20_000);
        using var key = ManifestSigner.CreateSigningKey();
        var transfer = BuildTransfer(key, "a.bin", original, chunkSize: 1000);

        var network = new InMemoryStreamNetwork();
        await using var serve = StartServe(network, SenderEndpoint, transfer.Sender.CreateSwarmContentSource());

        var (sink, sinkFactory) = MemorySinkFactory();
        using var pull = NewPull(network, TrustedStoreFor(key), sinkFactory);

        using var cts = new CancellationTokenSource(Timeout);
        await pull.PullFromAsync(SenderEndpoint, cts.Token);
        Assert.True(pull.IsComplete);

        await pull.PullFromAsync(SenderEndpoint, cts.Token); // nothing missing — stays complete
        Assert.True(pull.IsComplete);
        Assert.Equal(original, sink().ToArray());
    }

    [Fact]
    public async Task UntrustedSender_ManifestRejected_NoData_AndDenialFires()
    {
        var original = RandomBytes(2, 6000);
        using var key = ManifestSigner.CreateSigningKey();
        var transfer = BuildTransfer(key, "secret.doc", original, chunkSize: 1024);

        var network = new InMemoryStreamNetwork();
        await using var serve = StartServe(network, SenderEndpoint, transfer.Sender.CreateSwarmContentSource());

        var (_, sinkFactory) = MemorySinkFactory();
        using var pull = NewPull(network, new InMemoryTrustStore() /* never trusted */, sinkFactory);

        TrustDecision? denial = null;
        pull.SenderTrustDenied += (d, _) => denial = d;

        using var cts = new CancellationTokenSource(Timeout);
        bool accepted = await pull.PullFromAsync(SenderEndpoint, cts.Token);

        Assert.False(accepted);
        Assert.Null(pull.Manifest);
        Assert.False(pull.IsComplete);
        Assert.NotNull(denial);
        Assert.Equal(TrustOutcome.DeniedUnknown, denial!.Outcome);
    }

    [Fact]
    public async Task UnknownSenderPrompt_Accepted_ProceedsAndPersistsTofuEntry()
    {
        var original = RandomBytes(21, 5000);
        using var key = ManifestSigner.CreateSigningKey();
        var transfer = BuildTransfer(key, "prompted.bin", original, chunkSize: 1024);

        var network = new InMemoryStreamNetwork();
        await using var serve = StartServe(network, SenderEndpoint, transfer.Sender.CreateSwarmContentSource());

        var (sink, sinkFactory) = MemorySinkFactory();
        var store = new InMemoryTrustStore();
        using var pull = new SwarmPullSession(
            ReceiverId(1), store, network.CreateClient(PeerEndpoint), new FakeClock(DateTimeOffset.UtcNow),
            new SwarmPullSessionOptions("/root", UnknownSenderPolicy.Prompt, IsInteractive: true), sinkFactory,
            trustPrompt: new AlwaysAcceptPrompt());

        using var cts = new CancellationTokenSource(Timeout);
        await pull.PullFromAsync(SenderEndpoint, cts.Token);

        Assert.True(pull.IsComplete);
        Assert.Equal(original, sink().ToArray());
        Assert.NotNull(store.Find(transfer.Signed.SenderId)); // TOFU persisted
    }

    [Fact]
    public async Task BadSignature_ManifestRejectedSilently_NoDenialEvent()
    {
        var original = RandomBytes(22, 3000);
        using var key = ManifestSigner.CreateSigningKey();
        var transfer = BuildTransfer(key, "x.bin", original, chunkSize: 1024);

        var forged = transfer.Signed with { Signature = Tamper(transfer.Signed.Signature) };
        var forgedSource = new FixedManifestSource(forged, transfer.Sender.CreateSwarmContentSource());

        var network = new InMemoryStreamNetwork();
        await using var serve = StartServe(network, SenderEndpoint, forgedSource);

        var (_, sinkFactory) = MemorySinkFactory();
        using var pull = NewPull(network, TrustedStoreFor(key), sinkFactory);

        bool denialFired = false;
        pull.SenderTrustDenied += (_, _) => denialFired = true;

        using var cts = new CancellationTokenSource(Timeout);
        bool accepted = await pull.PullFromAsync(SenderEndpoint, cts.Token);

        Assert.False(accepted);
        Assert.Null(pull.Manifest);
        Assert.False(denialFired); // invalid signature is dropped silently, not reported as a trust denial
    }

    [Fact]
    public async Task ResumeFromPartial_SecondPull_RequestsOnlyMissingChunks_AndCompletes()
    {
        var original = RandomBytes(3, 20_000);
        using var key = ManifestSigner.CreateSigningKey();
        var transfer = BuildTransfer(key, "resume.bin", original, chunkSize: 1000);
        int chunkCount = ChunkLayout.ComputeChunkCount(original.Length, 1000);

        var network = new InMemoryStreamNetwork();
        // Peer A grants the key and serves only the first half of the chunks (returns not-found for the rest).
        var partialSource = new PartialChunkSource(transfer.Sender.CreateSwarmContentSource(), servedBelowIndex: chunkCount / 2);
        await using var serveA = StartServe(network, SenderEndpoint, partialSource);

        var (sink, sinkFactory) = MemorySinkFactory();
        using var pull = NewPull(network, TrustedStoreFor(key), sinkFactory);
        var progress = new ProgressRecorder(pull);

        using var cts = new CancellationTokenSource(Timeout);
        await pull.PullFromAsync(SenderEndpoint, cts.Token);

        Assert.False(pull.IsComplete); // only got the first half
        Assert.InRange(progress.Latest.CompletedChunks, 1, chunkCount - 1);

        // Bring up a full peer B at a new endpoint and resume — only the missing chunks are re-requested.
        await using var serveB = StartServe(network, PeerEndpoint, transfer.Sender.CreateSwarmContentSource());
        await pull.PullFromAsync(PeerEndpoint, cts.Token);

        Assert.True(pull.IsComplete);
        Assert.Equal(original, sink().ToArray());
    }

    [Fact]
    public async Task ChunksFromReceiverPeer_KeyFromSender_Completes()
    {
        // The peer-offload win: pull ciphertext chunks from another receiver (which cannot grant the key), then
        // obtain the content key from the sender, and decrypt. Proves chunks and key can come from different peers.
        var original = RandomBytes(4, 18_000);
        using var key = ManifestSigner.CreateSigningKey();
        var transfer = BuildTransfer(key, "shared.mp4", original, chunkSize: 1500);

        // A completed receiver, populated by a real in-memory multicast transfer, is the chunk-serving peer.
        var receiverB = await CompletedReceiverAsync(key, transfer);

        var streamNetwork = new InMemoryStreamNetwork();
        await using var senderServe = StartServe(streamNetwork, SenderEndpoint, transfer.Sender.CreateSwarmContentSource());
        await using var peerServe = StartServe(streamNetwork, PeerEndpoint, receiverB.CreateSwarmContentSource());

        var (sink, sinkFactory) = MemorySinkFactory();
        using var pull = NewPull(streamNetwork, TrustedStoreFor(key), sinkFactory);
        var progress = new ProgressRecorder(pull);

        using var cts = new CancellationTokenSource(Timeout);

        // Step 1: pull from the receiver peer — manifest + all ciphertext chunks verified, but NO key yet.
        await pull.PullFromAsync(PeerEndpoint, cts.Token);
        Assert.NotNull(pull.Manifest);
        Assert.False(pull.IsComplete);
        Assert.Equal(progress.Latest.TotalChunks, progress.Latest.CompletedChunks); // every chunk verified from the peer...
        Assert.Equal(TransferPhase.AwaitingKey, progress.Latest.Phase);              // ...but still awaiting the key

        // Step 2: get the key from the sender; pending ciphertext decrypts and the transfer completes.
        await pull.PullFromAsync(SenderEndpoint, cts.Token);

        Assert.True(pull.IsComplete);
        Assert.Equal(original, sink().ToArray());
    }

    [Fact]
    public async Task TamperedChunk_FailsVerification_NeverWritten_ThenRecoversFromCleanPeer()
    {
        var original = RandomBytes(5, 12_000);
        using var key = ManifestSigner.CreateSigningKey();
        var transfer = BuildTransfer(key, "data.bin", original, chunkSize: 1000);

        var network = new InMemoryStreamNetwork();
        var tamperingSource = new TamperingChunkSource(transfer.Sender.CreateSwarmContentSource(), targetChunkIndex: 2);
        await using var badServe = StartServe(network, SenderEndpoint, tamperingSource);

        var (sink, sinkFactory) = MemorySinkFactory();
        using var pull = NewPull(network, TrustedStoreFor(key), sinkFactory);

        using var cts = new CancellationTokenSource(Timeout);
        await pull.PullFromAsync(SenderEndpoint, cts.Token);

        Assert.False(pull.IsComplete); // the tampered chunk failed Merkle verification and was dropped

        // A clean peer supplies the un-tampered chunk and the transfer completes byte-identically.
        await using var goodServe = StartServe(network, PeerEndpoint, transfer.Sender.CreateSwarmContentSource());
        await pull.PullFromAsync(PeerEndpoint, cts.Token);

        Assert.True(pull.IsComplete);
        Assert.Equal(original, sink().ToArray());
    }

    [Fact]
    public async Task ChunkIndexSwap_IsRejected_ByLeafIndexBinding()
    {
        // A malicious peer serves chunk 0's genuine (ciphertext, proof) but labels it as chunk 1. Merkle
        // verification alone would accept it (a real leaf), and only the AEAD AAD would later object — after the
        // bitmap was set. The LeafIndex<->chunkIndex binding rejects it up front, so chunk 1 is never marked have.
        var original = RandomBytes(6, 10_000);
        using var key = ManifestSigner.CreateSigningKey();
        var transfer = BuildTransfer(key, "swap.bin", original, chunkSize: 1000);

        var network = new InMemoryStreamNetwork();
        var swapSource = new SwappingChunkSource(transfer.Sender.CreateSwarmContentSource(), requestedIndex: 1, servedInsteadIndex: 0);
        await using var serve = StartServe(network, SenderEndpoint, swapSource);

        var (_, sinkFactory) = MemorySinkFactory();
        using var pull = NewPull(network, TrustedStoreFor(key), sinkFactory);

        using var cts = new CancellationTokenSource(Timeout);
        await pull.PullFromAsync(SenderEndpoint, cts.Token);

        Assert.False(pull.IsComplete); // chunk 1 (served as a swapped chunk 0) was rejected, never marked received
    }

    [Fact]
    public async Task PullFromRelayWithoutKey_HeldCiphertextStaysWithinBudget()
    {
        // The unbounded-cache case this budget exists for: a relaying receiver serves every chunk but cannot
        // grant the content key, so nothing can be decrypted or written and every chunk's ciphertext is live at
        // once. Before the bound, a puller held the whole transfer in memory on this path.
        var original = RandomBytes(31, 20_000);           // exactly 20 chunks of 1000 => 1016 bytes ciphertext each
        using var key = ManifestSigner.CreateSigningKey();
        var transfer = BuildTransfer(key, "relay.bin", original, chunkSize: 1000);
        var relay = await CompletedReceiverAsync(key, transfer);

        const long budget = 3 * 1016;
        var network = new InMemoryStreamNetwork();
        await using var relayServe = StartServe(network, PeerEndpoint, relay.CreateSwarmContentSource());

        var (_, sinkFactory) = MemorySinkFactory();
        using var pull = NewPull(network, TrustedStoreFor(key), sinkFactory, chunkCacheBytes: budget);

        using var cts = new CancellationTokenSource(Timeout);
        await pull.PullFromAsync(PeerEndpoint, cts.Token);

        Assert.NotNull(pull.Manifest);
        Assert.False(pull.IsComplete);                    // no key from a relay, so nothing is written
        Assert.InRange(pull.HeldChunkCount, 1, 3);
        Assert.InRange(pull.HeldCiphertextBytes, 1, budget);
    }

    [Fact]
    public async Task CiphertextEvictedUnderBudget_IsRePulled_NotStranded()
    {
        // Dropping an undecrypted chunk destroys the only copy of its ciphertext, so eviction has to clear the
        // bitmap bit with it. If it did not, the chunk would read as "have" with nothing behind it, would never
        // be re-requested, and the file would finish short — silently.
        var original = RandomBytes(32, 20_000);
        using var key = ManifestSigner.CreateSigningKey();
        var transfer = BuildTransfer(key, "evicted.bin", original, chunkSize: 1000);
        var relay = await CompletedReceiverAsync(key, transfer);

        var network = new InMemoryStreamNetwork();
        await using var relayServe = StartServe(network, PeerEndpoint, relay.CreateSwarmContentSource());
        await using var senderServe = StartServe(network, SenderEndpoint, transfer.Sender.CreateSwarmContentSource());

        var (sink, sinkFactory) = MemorySinkFactory();
        using var pull = NewPull(network, TrustedStoreFor(key), sinkFactory, chunkCacheBytes: 3 * 1016);
        var progress = new ProgressRecorder(pull);

        using var cts = new CancellationTokenSource(Timeout);

        // Step 1: take everything the relay has. Most of it is evicted back to missing for want of the key.
        await pull.PullFromAsync(PeerEndpoint, cts.Token);
        Assert.False(pull.IsComplete);
        Assert.True(progress.Latest.CompletedChunks < progress.Latest.TotalChunks); // the evicted chunks went back

        // Step 2: the sender grants the key and re-serves whatever was walked back.
        await pull.PullFromAsync(SenderEndpoint, cts.Token);

        Assert.True(pull.IsComplete);
        Assert.Equal(original, sink().ToArray());
        Assert.Equal(0, pull.HeldChunkCount); // every chunk released once its plaintext reached the sink
    }

    [Fact]
    public async Task BudgetSmallerThanOneChunk_StillCompletes()
    {
        // Degenerate budget: eviction always keeps one chunk resident, so a budget below a single chunk degrades
        // to hold-one-at-a-time instead of livelocking on drop-everything-then-re-request-forever.
        var original = RandomBytes(33, 9000);
        using var key = ManifestSigner.CreateSigningKey();
        var transfer = BuildTransfer(key, "tiny-budget.bin", original, chunkSize: 1000);

        var network = new InMemoryStreamNetwork();
        await using var serve = StartServe(network, SenderEndpoint, transfer.Sender.CreateSwarmContentSource());

        var (sink, sinkFactory) = MemorySinkFactory();
        using var pull = NewPull(network, TrustedStoreFor(key), sinkFactory, chunkCacheBytes: 1);

        using var cts = new CancellationTokenSource(Timeout);
        await pull.PullFromAsync(SenderEndpoint, cts.Token);

        Assert.True(pull.IsComplete);
        Assert.Equal(original, sink().ToArray());
        Assert.Equal(0, pull.HeldChunkCount);
    }

    // ---- harness ----

    private sealed record Transfer(
        SignedManifest Signed,
        SenderSession Sender,
        IReadOnlyDictionary<int, IFileSource> Sources,
        IReadOnlyDictionary<int, MerkleTree> Trees,
        NSec.Cryptography.Key SenderEncryptionKey,
        ContentKey ContentKey);

    private static Transfer BuildTransfer(NSec.Cryptography.Key key, string relativePath, byte[] bytes, int chunkSize)
    {
        var sessionId = new byte[16];
        var source = new MemoryFileSource(bytes);
        var contentKey = ContentKey.Generate();
        var senderEncryptionKey = EncryptionKeys.Create();

        var hashes = EncryptedChunkHasher.ComputeAsync(source, chunkSize, sessionId, 0, contentKey).GetAwaiter().GetResult();
        var tree = MerkleTree.Build(hashes);
        int chunkCount = ChunkLayout.ComputeChunkCount(bytes.Length, chunkSize);

        var manifest = new TransferManifest(
            sessionId, "swarm-transfer", DateTimeOffset.UtcNow, EncryptionKeys.ExportPublicKey(senderEncryptionKey),
            [new ManifestFileEntry(relativePath, bytes.Length, chunkSize, chunkCount, tree.Root)]);
        var signed = ManifestSigner.Sign(manifest, key);

        var sources = new Dictionary<int, IFileSource> { [0] = source };
        var trees = new Dictionary<int, MerkleTree> { [0] = tree };

        var sender = new SenderSession(
            signed, sources, trees,
            new InMemoryNetwork().CreateMulticastTransport(new Endpoint("unused", 0)),
            senderEncryptionKey, contentKey, maxDatagramPayloadBytes: 65_000);

        return new Transfer(signed, sender, sources, trees, senderEncryptionKey, contentKey);
    }

    /// <summary>Runs a real in-memory multicast transfer so the returned ReceiverSession holds a full verified-chunk cache to serve.</summary>
    private static async Task<ReceiverSession> CompletedReceiverAsync(NSec.Cryptography.Key key, Transfer transfer)
    {
        var network = new InMemoryNetwork();
        var mcSender = new SenderSession(
            transfer.Signed, transfer.Sources, transfer.Trees,
            network.CreateMulticastTransport(new Endpoint("mc-sender", 1)),
            transfer.SenderEncryptionKey, transfer.ContentKey, maxDatagramPayloadBytes: 65_000);

        var (_, sinkFactory) = MemorySinkFactory();
        var receiver = new ReceiverSession(
            ReceiverId(9), TrustedStoreFor(key), network.CreateMulticastTransport(new Endpoint("mc-receiver", 1)),
            new FakeClock(DateTimeOffset.UtcNow), new ReceiverSessionOptions("/root"), sinkFactory);

        using var cts = new CancellationTokenSource(Timeout);
        var senderTask = mcSender.RunAsync(cts.Token);
        var receiverTask = receiver.RunAsync(cts.Token);
        while (!receiver.IsComplete && !cts.IsCancellationRequested)
            await Task.Delay(20, CancellationToken.None);
        await cts.CancelAsync();
        await Swallow(senderTask);
        await Swallow(receiverTask);

        Assert.True(receiver.IsComplete);
        return receiver;
    }

    private static IAsyncDisposable StartServe(InMemoryStreamNetwork network, Endpoint endpoint, ISwarmContentSource source)
    {
        var listener = network.CreateListener(endpoint);
        var serve = new SwarmServeListener(listener, source);
        var cts = new CancellationTokenSource();
        var task = serve.RunAsync(cts.Token);
        return new ServeHandle(cts, task, listener);
    }

    private sealed class ServeHandle(CancellationTokenSource cts, Task task, IAsyncDisposable listener) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            await cts.CancelAsync();
            await Swallow(task);
            await listener.DisposeAsync();
            cts.Dispose();
        }
    }

    private sealed class ProgressRecorder
    {
        private TransferProgress? _latest;
        public ProgressRecorder(SwarmPullSession pull) => pull.ProgressChanged += p => _latest = p;
        public TransferProgress Latest => _latest ?? throw new InvalidOperationException("No progress emitted yet.");
    }

    private static SwarmPullSession NewPull(
        InMemoryStreamNetwork network, ITrustStore store, Func<string, long, IFileSink> sinkFactory, long? chunkCacheBytes = null) =>
        new(ReceiverId(1), store, network.CreateClient(PeerEndpoint), new FakeClock(DateTimeOffset.UtcNow),
            new SwarmPullSessionOptions("/root", ChunkCacheBytes: chunkCacheBytes), sinkFactory);

    private static ITrustStore TrustedStoreFor(NSec.Cryptography.Key key)
    {
        var store = new InMemoryTrustStore();
        var publicKey = key.PublicKey.Export(NSec.Cryptography.KeyBlobFormat.RawPublicKey);
        store.Upsert(new TrustEntry(PublicKeyId.FromRawEd25519(publicKey), "sender", TrustStatus.Trusted, DateTimeOffset.UnixEpoch, TrustEntrySource.Manual));
        return store;
    }

    private static (Func<MemoryFileSink> GetSink, Func<string, long, IFileSink> Factory) MemorySinkFactory()
    {
        MemoryFileSink? sink = null;
        Func<string, long, IFileSink> factory = (_, length) => sink = new MemoryFileSink((int)length);
        return (() => sink!, factory);
    }

    private static byte[] RandomBytes(int seed, int length)
    {
        var bytes = new byte[length];
        new Random(seed).NextBytes(bytes);
        return bytes;
    }

    private static byte[] Tamper(byte[] bytes)
    {
        var copy = (byte[])bytes.Clone();
        copy[0] ^= 0xFF;
        return copy;
    }

    private static byte[] ReceiverId(byte fill)
    {
        var bytes = new byte[16];
        Array.Fill(bytes, fill);
        return bytes;
    }

    private static async Task Swallow(Task task)
    {
        try { await task; } catch (OperationCanceledException) { }
    }

    private sealed class AlwaysAcceptPrompt : ITrustPrompt
    {
        public Task<bool> RequestTrustAsync(TrustPromptContext context, CancellationToken cancellationToken) => Task.FromResult(true);
    }

    private sealed class FixedManifestSource(SignedManifest manifest, ISwarmContentSource inner) : ISwarmContentSource
    {
        public SignedManifest? Manifest => manifest;
        public ValueTask<SwarmChunk?> TryGetChunkAsync(int fileIndex, int chunkIndex, CancellationToken cancellationToken = default) =>
            inner.TryGetChunkAsync(fileIndex, chunkIndex, cancellationToken);
        public KeyGrantMessage? TryGrantContentKey(JoinRequestMessage request) => inner.TryGrantContentKey(request);
    }

    private sealed class PartialChunkSource(ISwarmContentSource inner, int servedBelowIndex) : ISwarmContentSource
    {
        public SignedManifest? Manifest => inner.Manifest;
        public ValueTask<SwarmChunk?> TryGetChunkAsync(int fileIndex, int chunkIndex, CancellationToken cancellationToken = default) =>
            chunkIndex < servedBelowIndex
                ? inner.TryGetChunkAsync(fileIndex, chunkIndex, cancellationToken)
                : ValueTask.FromResult<SwarmChunk?>(null);
        public KeyGrantMessage? TryGrantContentKey(JoinRequestMessage request) => inner.TryGrantContentKey(request);
    }

    private sealed class TamperingChunkSource(ISwarmContentSource inner, int targetChunkIndex) : ISwarmContentSource
    {
        public SignedManifest? Manifest => inner.Manifest;
        public async ValueTask<SwarmChunk?> TryGetChunkAsync(int fileIndex, int chunkIndex, CancellationToken cancellationToken = default)
        {
            var chunk = await inner.TryGetChunkAsync(fileIndex, chunkIndex, cancellationToken);
            if (chunk is null || chunkIndex != targetChunkIndex)
                return chunk;
            var corrupted = (byte[])chunk.Ciphertext.Clone();
            corrupted[0] ^= 0xFF;
            return new SwarmChunk(corrupted, chunk.Proof);
        }
        public KeyGrantMessage? TryGrantContentKey(JoinRequestMessage request) => inner.TryGrantContentKey(request);
    }

    private sealed class SwappingChunkSource(ISwarmContentSource inner, int requestedIndex, int servedInsteadIndex) : ISwarmContentSource
    {
        public SignedManifest? Manifest => inner.Manifest;
        public ValueTask<SwarmChunk?> TryGetChunkAsync(int fileIndex, int chunkIndex, CancellationToken cancellationToken = default) =>
            inner.TryGetChunkAsync(fileIndex, chunkIndex == requestedIndex ? servedInsteadIndex : chunkIndex, cancellationToken);
        public KeyGrantMessage? TryGrantContentKey(JoinRequestMessage request) => inner.TryGrantContentKey(request);
    }
}
