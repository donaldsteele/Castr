using Castr.Core.Chunking;
using Castr.Core.Manifest;
using Castr.Core.Protocol;
using Castr.Core.Security;
using Castr.Core.Tests.TestSupport;
using Castr.Core.Time;
using Castr.Core.Transport;
using Castr.Core.Transport.InMemory;
using Castr.Core.Trust;

namespace Castr.Core.Tests.Protocol;

/// <summary>
/// Coverage for the byte-bounded, LRU verified-chunk cache and its cold path.
///
/// <para><b>What used to be here.</b> <c>ReceiverSession</c> kept every verified chunk's ciphertext for the
/// whole transfer with no eviction of any kind, so retained memory was the size of the transfer — ~5 GB of
/// large-object-heap buffers for a 5 GB file, plus a Merkle proof object per chunk. A hard wall, not a rate
/// limit.</para>
///
/// <para><b>What replaces it.</b> Ciphertext lives in an LRU bounded by
/// <see cref="ReceiverSessionOptions.ChunkCacheBytes"/>; proofs (~170x smaller, and not reconstructible by a
/// receiver that holds only the signed root) are retained. A peer asking for an evicted chunk is served by
/// reading the plaintext back off the sink and re-encrypting it, which is byte-identical because
/// <see cref="ContentKey.EncryptChunk"/> is deterministic — see <c>ContentKeyDeterminismTests</c>.</para>
///
/// <para>Several tests here set <c>ChunkCacheBytes: 0</c> on purpose. At zero, nothing survives past the write
/// to disk, so <b>any</b> successful serve is necessarily a cold reconstruction — no cache hit can mask a broken
/// cold path.</para>
/// </summary>
public class ReceiverSessionChunkCacheTests
{
    private static readonly TimeSpan OverallTimeout = TimeSpan.FromSeconds(30);

    // Matches the other protocol tests: a budget large enough that every chunk travels as one whole
    // ChunkDataMessage / ChunkResponseMessage, so assertions can address chunks by index.
    private const int SingleDatagramBudget = 65_000;

    [Fact]
    public async Task ChunkCache_IsBoundedByBytes_NotByTransferSize()
    {
        // 40 chunks of 1000 bytes (1016 of ciphertext each, ~40 KB total) against a 4 KB budget: the old
        // behavior retained all of it, the new behavior must retain a handful.
        const int chunkSize = 1000;
        const long cacheBytes = 4_000;
        var originalBytes = RandomBytes(seed: 11, length: 40_000);

        using var key = ManifestSigner.CreateSigningKey();
        var transfer = BuildTransfer(key, "bounded.bin", originalBytes, chunkSize);

        var network = new InMemoryNetwork();
        var sender = NewSender(transfer, network.CreateMulticastTransport(new Endpoint("sender", 1)));
        var (sink, sinkFactory) = MemorySinkFactory();
        var receiver = NewReceiver(key, network.CreateMulticastTransport(new Endpoint("r", 1)), sinkFactory, cacheBytes);

        await RunUntilCompleteAsync(sender, receiver);

        Assert.Equal(originalBytes, sink().ToArray());
        Assert.True(receiver.CachedCiphertextBytes <= cacheBytes,
            $"cache held {receiver.CachedCiphertextBytes} bytes against a {cacheBytes}-byte budget");
        Assert.True(receiver.CachedChunkCount < 40,
            $"{receiver.CachedChunkCount} of 40 chunks still resident — nothing was evicted");
    }

    [Fact]
    public async Task ChunkCache_AtZeroBytes_RetainsNothing_AndTheTransferStillCompletes()
    {
        // The degenerate bound, which is also the strongest statement of the invariant: caching is an
        // optimisation over the cold path, never a correctness dependency of the receive path itself.
        const int chunkSize = 1000;
        var originalBytes = RandomBytes(seed: 12, length: 20_000);

        using var key = ManifestSigner.CreateSigningKey();
        var transfer = BuildTransfer(key, "zero-cache.bin", originalBytes, chunkSize);

        var network = new InMemoryNetwork();
        var sender = NewSender(transfer, network.CreateMulticastTransport(new Endpoint("sender", 1)));
        var (sink, sinkFactory) = MemorySinkFactory();
        var receiver = NewReceiver(key, network.CreateMulticastTransport(new Endpoint("r", 1)), sinkFactory, cacheBytes: 0);

        await RunUntilCompleteAsync(sender, receiver);

        Assert.Equal(originalBytes, sink().ToArray());
        Assert.Equal(0, receiver.CachedCiphertextBytes);
        Assert.Equal(0, receiver.CachedChunkCount);
    }

    [Fact]
    public async Task EvictedChunk_IsRebuiltByteIdenticallyToTheOriginalCiphertext_AndItsProofStillVerifies()
    {
        // THE headline test for this change. With a zero-byte cache every chunk has been evicted by the time
        // it is asked for, so each of these serves is a read-plaintext-back-and-re-encrypt reconstruction; each
        // is compared against the exact bytes the sender produced.
        const int chunkSize = 1000;
        var originalBytes = RandomBytes(seed: 13, length: 20_000);
        int chunkCount = ChunkLayout.ComputeChunkCount(originalBytes.Length, chunkSize);

        using var key = ManifestSigner.CreateSigningKey();
        var transfer = BuildTransfer(key, "rebuilt.bin", originalBytes, chunkSize);

        var network = new InMemoryNetwork();
        var sender = NewSender(transfer, network.CreateMulticastTransport(new Endpoint("sender", 1)));
        var (_, sinkFactory) = MemorySinkFactory();
        var receiver = NewReceiver(key, network.CreateMulticastTransport(new Endpoint("r", 1)), sinkFactory, cacheBytes: 0);

        await RunUntilCompleteAsync(sender, receiver);
        Assert.Equal(0, receiver.CachedChunkCount); // precondition: there is nothing left to serve from memory

        // CreateSwarmContentSource() is the same TryGetServableChunkAsync path HandleChunkRequestAsync uses,
        // reachable without racing a live receive loop.
        var source = receiver.CreateSwarmContentSource();
        var root = transfer.Signed.Manifest.Files[0].MerkleRoot;

        for (int chunkIndex = 0; chunkIndex < chunkCount; chunkIndex++)
        {
            var (expectedCiphertext, expectedProof) = EncryptChunk(transfer, 0, chunkIndex, chunkSize);
            var served = await source.TryGetChunkAsync(0, chunkIndex, CancellationToken.None);

            Assert.NotNull(served);
            Assert.Equal(expectedCiphertext, served!.Ciphertext);
            Assert.Equal(expectedProof.LeafIndex, served.Proof.LeafIndex);
            Assert.Equal(expectedProof.Steps.Length, served.Proof.Steps.Length);
            // And the reconstruction is not merely equal to our own recomputation — it verifies against the
            // signed Merkle root, which is what a peer will actually check.
            Assert.True(ManifestVerifier.VerifyChunk(root, ChunkHash.Compute(served.Ciphertext), served.Proof));
        }
    }

    [Fact]
    public async Task EvictedChunk_IsServedOverTheWire_ToAPeersChunkRequest()
    {
        // The same cold path, but through the real CHUNK_REQUEST handler on a live receive loop rather than
        // through the swarm source. The receiver is deliberately left incomplete (its final chunk is dropped and
        // no repair loop runs) because RunAsync returns on completion — a serving receiver is by definition one
        // that is still running.
        const int chunkSize = 1000;
        var originalBytes = RandomBytes(seed: 14, length: 20_000);
        int chunkCount = ChunkLayout.ComputeChunkCount(originalBytes.Length, chunkSize);
        int finalChunkIndex = chunkCount - 1;

        using var key = ManifestSigner.CreateSigningKey();
        var transfer = BuildTransfer(key, "wire-served.bin", originalBytes, chunkSize);
        var sessionId = transfer.Signed.Manifest.SessionId;

        var network = new InMemoryNetwork();
        var sender = NewSender(transfer, network.CreateMulticastTransport(new Endpoint("sender", 1)));
        var peer = network.CreateMulticastTransport(new Endpoint("peer", 1));

        // Drop the file's tail so the receiver never completes, and record everything it sends.
        var lossy = new FilteringMulticastTransport(
            network.CreateMulticastTransport(new Endpoint("r", 1)),
            message => message is not ChunkDataMessage { ChunkIndex: var i } || i != finalChunkIndex);
        var recorder = new OutboundRecorder(lossy);

        var (_, sinkFactory) = MemorySinkFactory();
        int verified = 0;
        var receiver = NewReceiver(key, recorder, sinkFactory, cacheBytes: 0);
        receiver.ProgressChanged += p => Volatile.Write(ref verified, p.CompletedChunks);

        using var cts = new CancellationTokenSource(OverallTimeout);
        var senderTask = sender.RunAsync(cts.Token);
        var receiverTask = receiver.RunAsync(cts.Token);

        await WaitUntilAsync(() => Volatile.Read(ref verified) >= chunkCount - 1, cts.Token);
        Assert.Equal(0, receiver.CachedChunkCount); // everything it holds has been evicted

        // A peer asks for chunk 0 — long since evicted.
        await peer.SendAsync(
            MessageCodec.Encode(new ChunkRequestMessage(sessionId, ReceiverId(9), new byte[16], 0, [0], "", 0)),
            cts.Token);

        await WaitUntilAsync(() => recorder.ChunkResponses.Any(r => r.FileIndex == 0 && r.ChunkIndex == 0), cts.Token);

        await cts.CancelAsync();
        await Swallow(senderTask);
        await Swallow(receiverTask);

        var served = recorder.ChunkResponses.First(r => r.FileIndex == 0 && r.ChunkIndex == 0);
        var (expectedCiphertext, _) = EncryptChunk(transfer, 0, 0, chunkSize);
        Assert.Equal(expectedCiphertext, served.Payload);
        Assert.True(ManifestVerifier.VerifyChunk(
            transfer.Signed.Manifest.Files[0].MerkleRoot, ChunkHash.Compute(served.Payload), served.Proof));
    }

    [Fact]
    public async Task ColdRebuild_RunsOffTheStateGate_SoASlowSinkCannotStallPacketProcessing()
    {
        // REGRESSION GUARD, and the reason the serve path was split into plan-under-gate / execute-off-gate.
        //
        // A cold rebuild costs a disk read plus a re-encrypt plus a BLAKE3 re-verify — measured at ~0.8 ms per
        // 256 KiB chunk warm, and 5-10 ms when the read is a real seek. One CHUNK_REQUEST may name up to
        // MaxChunksPerRequest indices, so doing that inline under _stateGate let ONE unauthenticated datagram
        // hold the gate for hundreds of ms warm and seconds cold. That exceeds CarouselIdleThreshold, no
        // watermark can advance while the gate is held, and a false idle restores the full M7 repair storm.
        //
        // This test makes the rebuild arbitrarily slow and asserts the receiver keeps verifying chunks while it
        // is in flight. With the rebuild back under the gate, no chunk can be processed during the stall and the
        // assertion fails.
        const int chunkSize = 1000;
        var originalBytes = RandomBytes(seed: 21, length: 40_000);
        int chunkCount = ChunkLayout.ComputeChunkCount(originalBytes.Length, chunkSize);

        using var key = ManifestSigner.CreateSigningKey();
        var transfer = BuildTransfer(key, "off-gate.bin", originalBytes, chunkSize);
        var sessionId = transfer.Signed.Manifest.SessionId;

        var network = new InMemoryNetwork();
        var injector = network.CreateMulticastTransport(new Endpoint("sender", 1));
        var recorder = new OutboundRecorder(network.CreateMulticastTransport(new Endpoint("r", 1)));

        // A sink whose reads block until released — standing in for a seek on a transfer larger than RAM.
        var gate = new SemaphoreSlim(0, 1);
        BlockingReadSink? sink = null;
        int verified = 0;
        var receiver = NewReceiver(key, recorder, (_, length) => sink = new BlockingReadSink((int)length, gate), cacheBytes: 0);
        receiver.ProgressChanged += p => Volatile.Write(ref verified, p.CompletedChunks);

        using var cts = new CancellationTokenSource(OverallTimeout);
        var receiverTask = receiver.RunAsync(cts.Token);

        await injector.SendAsync(MessageCodec.Encode(new ManifestMessage(transfer.Signed)), cts.Token);
        await WaitUntilAsync(() => receiver.Manifest is not null, cts.Token);
        var join = await WaitForJoinAsync(recorder, cts.Token);
        var wrapped = ContentKeyWrap.Wrap(
            transfer.SenderEncryptionKey, join.ReceiverEncryptionPublicKey, sessionId, join.ReceiverId,
            transfer.ContentKey.Export());
        await injector.SendAsync(MessageCodec.Encode(new KeyGrantMessage(sessionId, join.ReceiverId, wrapped)), cts.Token);

        // Deliver the first half, so there is something evicted to ask for.
        int half = chunkCount / 2;
        for (int i = 0; i < half; i++)
            await injector.SendAsync(MessageCodec.Encode(CarouselChunk(transfer, 0, i, chunkSize)), cts.Token);
        await WaitUntilAsync(() => Volatile.Read(ref verified) >= half, cts.Token);

        // A peer asks for an evicted chunk; the sink read will block indefinitely until we release it.
        await injector.SendAsync(
            MessageCodec.Encode(new ChunkRequestMessage(sessionId, ReceiverId(9), new byte[16], 0, [0], "", 0)),
            cts.Token);
        await WaitUntilAsync(() => sink!.ReadsStarted > 0, cts.Token);

        // THE ASSERTION: with a rebuild stalled mid-flight, the receive loop must still be verifying chunks.
        for (int i = half; i < chunkCount; i++)
            await injector.SendAsync(MessageCodec.Encode(CarouselChunk(transfer, 0, i, chunkSize)), cts.Token);
        await WaitUntilAsync(() => Volatile.Read(ref verified) >= chunkCount, cts.Token);
        Assert.True(sink!.ReadsStarted > 0 && sink.ReadsCompleted == 0, "the rebuild must still be in flight");

        gate.Release();
        await WaitUntilAsync(() => recorder.ChunkResponses.Any(r => r.ChunkIndex == 0), cts.Token);

        await cts.CancelAsync();
        await Swallow(receiverTask);

        // And once unblocked it still serves the correct bytes.
        var (expected, _) = EncryptChunk(transfer, 0, 0, chunkSize);
        Assert.Equal(expected, recorder.ChunkResponses.First(r => r.ChunkIndex == 0).Payload);
    }

    [Fact]
    public async Task ChunkServeOutcomes_AreCounted_SoSilentDegradationIsDiagnosable()
    {
        // Peer-served repair degrades silently in several independent ways, and each one individually looks like
        // "the requester fell back to the sender". Collectively they can leave the bandwidth-offload feature
        // completely non-functional with nothing logged anywhere. These counters are the fix for that.
        const int chunkSize = 1000;
        var originalBytes = RandomBytes(seed: 22, length: 20_000);
        int chunkCount = ChunkLayout.ComputeChunkCount(originalBytes.Length, chunkSize);

        using var key = ManifestSigner.CreateSigningKey();
        var transfer = BuildTransfer(key, "counted.bin", originalBytes, chunkSize);

        var network = new InMemoryNetwork();
        var sender = NewSender(transfer, network.CreateMulticastTransport(new Endpoint("sender", 1)));
        var (_, sinkFactory) = MemorySinkFactory();
        var receiver = NewReceiver(key, network.CreateMulticastTransport(new Endpoint("r", 1)), sinkFactory, cacheBytes: 0);

        var observed = new List<ChunkServeOutcome>();
        receiver.ChunkServeObserved += (outcome, _, _) => { lock (observed) observed.Add(outcome); };

        await RunUntilCompleteAsync(sender, receiver);

        var source = receiver.CreateSwarmContentSource();
        Assert.NotNull(await source.TryGetChunkAsync(0, 0, CancellationToken.None));           // rebuild
        Assert.Null(await source.TryGetChunkAsync(0, chunkCount + 5, CancellationToken.None)); // never verified

        Assert.Equal(1, receiver.ChunkServeCount(ChunkServeOutcome.ServedByRebuild));
        Assert.Equal(1, receiver.ChunkServeCount(ChunkServeOutcome.DeclinedNotVerified));
        Assert.Equal(0, receiver.ChunkServeCount(ChunkServeOutcome.DeclinedReverifyFailed));
        lock (observed)
        {
            Assert.Contains(ChunkServeOutcome.ServedByRebuild, observed);
            Assert.Contains(ChunkServeOutcome.DeclinedNotVerified, observed);
        }
    }

    [Fact]
    public async Task WriteOnlySinkDecline_IsCountedAsSuch_NotAsGenericFailure()
    {
        const int chunkSize = 1000;
        var originalBytes = RandomBytes(seed: 23, length: 20_000);

        using var key = ManifestSigner.CreateSigningKey();
        var transfer = BuildTransfer(key, "counted-writeonly.bin", originalBytes, chunkSize);

        var network = new InMemoryNetwork();
        var sender = NewSender(transfer, network.CreateMulticastTransport(new Endpoint("sender", 1)));
        var receiver = NewReceiver(
            key, network.CreateMulticastTransport(new Endpoint("r", 1)),
            (_, length) => new WriteOnlySink(new MemoryFileSink((int)length)), cacheBytes: 0);

        await RunUntilCompleteAsync(sender, receiver);

        var source = receiver.CreateSwarmContentSource();
        Assert.Null(await source.TryGetChunkAsync(0, 0, CancellationToken.None));

        // The specific, actionable reason — this is the one that would otherwise be invisible to an operator
        // wondering why their swarm stopped offloading anything.
        Assert.Equal(1, receiver.ChunkServeCount(ChunkServeOutcome.DeclinedSinkNotReadable));
    }

    [Fact]
    public async Task EvictedChunk_WithAWriteOnlySink_IsSimplyNotAnswered()
    {
        // Graceful degradation. IFileSink stays write-only by contract; only IReadableFileSink can feed the cold
        // path. A receiver whose sink cannot be read back must decline to serve — quietly, without faulting its
        // receive loop — and the requester's own repair timer then reaches another peer or the sender.
        const int chunkSize = 1000;
        var originalBytes = RandomBytes(seed: 15, length: 20_000);

        using var key = ManifestSigner.CreateSigningKey();
        var transfer = BuildTransfer(key, "write-only.bin", originalBytes, chunkSize);

        var network = new InMemoryNetwork();
        var sender = NewSender(transfer, network.CreateMulticastTransport(new Endpoint("sender", 1)));

        MemoryFileSink? backing = null;
        var receiver = NewReceiver(
            key, network.CreateMulticastTransport(new Endpoint("r", 1)),
            (_, length) => new WriteOnlySink(backing = new MemoryFileSink((int)length)),
            cacheBytes: 0);

        await RunUntilCompleteAsync(sender, receiver);

        // The transfer itself is unaffected — write-only is all the receive path ever needed.
        Assert.Equal(originalBytes, backing!.ToArray());

        var source = receiver.CreateSwarmContentSource();
        Assert.Null(await source.TryGetChunkAsync(0, 0, CancellationToken.None));
        Assert.Null(await source.TryGetChunkAsync(0, 5, CancellationToken.None));
    }

    [Fact]
    public async Task NeverVerifiedChunk_IsNotServed_EvenThoughTheDestinationFileWouldReadBack()
    {
        // The cold path must not become a way to serve a chunk this receiver never verified: the proof is the
        // gate, and the proof only exists for chunks that passed Merkle verification here.
        const int chunkSize = 1000;
        var originalBytes = RandomBytes(seed: 16, length: 20_000);
        int chunkCount = ChunkLayout.ComputeChunkCount(originalBytes.Length, chunkSize);
        int missingIndex = 3;

        using var key = ManifestSigner.CreateSigningKey();
        var transfer = BuildTransfer(key, "partial.bin", originalBytes, chunkSize);

        var network = new InMemoryNetwork();
        var sender = NewSender(transfer, network.CreateMulticastTransport(new Endpoint("sender", 1)));
        var lossy = new FilteringMulticastTransport(
            network.CreateMulticastTransport(new Endpoint("r", 1)),
            message => message is not ChunkDataMessage { ChunkIndex: var i } || i != missingIndex);

        var (_, sinkFactory) = MemorySinkFactory();
        int verified = 0;
        var receiver = NewReceiver(key, lossy, sinkFactory, cacheBytes: 0);
        receiver.ProgressChanged += p => Volatile.Write(ref verified, p.CompletedChunks);

        using var cts = new CancellationTokenSource(OverallTimeout);
        var senderTask = sender.RunAsync(cts.Token);
        var receiverTask = receiver.RunAsync(cts.Token);
        await WaitUntilAsync(() => Volatile.Read(ref verified) >= chunkCount - 1, cts.Token);
        await cts.CancelAsync();
        await Swallow(senderTask);
        await Swallow(receiverTask);

        var source = receiver.CreateSwarmContentSource();
        Assert.Null(await source.TryGetChunkAsync(0, missingIndex, CancellationToken.None));
        Assert.NotNull(await source.TryGetChunkAsync(0, missingIndex + 1, CancellationToken.None)); // positive control
    }

    [Fact]
    public async Task ChunksVerifiedBeforeTheKeyGrant_ArePinned_AndSurviveAZeroByteBudget()
    {
        // A chunk verified before the content key arrives has no plaintext on disk, so its ciphertext is the
        // only copy anywhere and the cold path cannot rebuild it. Evicting one would strand that position
        // permanently: the bitmap already reads "have", so repair never re-requests it. Pinning must therefore
        // beat the byte budget, and the overshoot is bounded by the JOIN_REQUEST/KEY_GRANT round trip.
        const int chunkSize = 1000;
        var originalBytes = RandomBytes(seed: 17, length: 8_000);
        int chunkCount = ChunkLayout.ComputeChunkCount(originalBytes.Length, chunkSize);

        using var key = ManifestSigner.CreateSigningKey();
        var transfer = BuildTransfer(key, "pinned.bin", originalBytes, chunkSize);
        var sessionId = transfer.Signed.Manifest.SessionId;

        var network = new InMemoryNetwork();
        var injector = network.CreateMulticastTransport(new Endpoint("sender", 1));
        var recorder = new OutboundRecorder(network.CreateMulticastTransport(new Endpoint("r", 1)));

        var (sink, sinkFactory) = MemorySinkFactory();
        int verified = 0;
        var receiver = NewReceiver(key, recorder, sinkFactory, cacheBytes: 0);
        receiver.ProgressChanged += p => Volatile.Write(ref verified, p.CompletedChunks);

        using var cts = new CancellationTokenSource(OverallTimeout);
        var receiverTask = receiver.RunAsync(cts.Token);

        // Manifest, then every chunk — but deliberately no KEY_GRANT yet.
        await injector.SendAsync(MessageCodec.Encode(new ManifestMessage(transfer.Signed)), cts.Token);
        await WaitUntilAsync(() => receiver.Manifest is not null, cts.Token);
        for (int chunkIndex = 0; chunkIndex < chunkCount; chunkIndex++)
            await injector.SendAsync(MessageCodec.Encode(CarouselChunk(transfer, 0, chunkIndex, chunkSize)), cts.Token);
        await WaitUntilAsync(() => Volatile.Read(ref verified) >= chunkCount, cts.Token);

        // Every chunk is verified and none is written, so the zero-byte budget must have been overridden.
        Assert.Equal(chunkCount, receiver.CachedChunkCount);
        Assert.True(receiver.CachedCiphertextBytes > 0);

        // Now grant the key, using the receiver's own JOIN_REQUEST as the sender would.
        var join = await WaitForJoinAsync(recorder, cts.Token);
        var wrapped = ContentKeyWrap.Wrap(
            transfer.SenderEncryptionKey, join.ReceiverEncryptionPublicKey,
            sessionId, join.ReceiverId, transfer.ContentKey.Export());
        await injector.SendAsync(
            MessageCodec.Encode(new KeyGrantMessage(sessionId, join.ReceiverId, wrapped)), cts.Token);

        await WaitUntilAsync(() => receiver.IsComplete, cts.Token);
        await cts.CancelAsync();
        await Swallow(receiverTask);

        // Nothing was stranded: every pinned chunk decrypted and wrote, and unpinning let the budget apply.
        Assert.Equal(originalBytes, sink().ToArray());
        Assert.Equal(0, receiver.CachedChunkCount);
        Assert.Equal(0, receiver.CachedCiphertextBytes);
    }

    [Fact]
    public async Task PeerServedRepair_StillRecoversALostChunk_WithATinyCacheOnBothReceivers()
    {
        // The behaviour most at risk from eviction: receiver B answers A's repair request for a chunk B no
        // longer holds in memory. Both receivers run a zero-byte cache, so if the cold path were broken this
        // would either hang or complete only because the sender also answered. The sender's carousel is allowed
        // to finish before A's repair loop starts, and A's loss is a chunk the sender has already sent, so the
        // recovery is real either way — see the note in EndToEndTransferTests about multicast CHUNK_REQUEST
        // having no NACK suppression, which is why this is framed as "repair still works" rather than
        // "the peer specifically answered".
        const int chunkSize = 1000;
        var originalBytes = RandomBytes(seed: 18, length: 24_000);

        using var key = ManifestSigner.CreateSigningKey();
        var transfer = BuildTransfer(key, "peer-repair.bin", originalBytes, chunkSize);

        var network = new InMemoryNetwork();
        var sender = NewSender(transfer, network.CreateMulticastTransport(new Endpoint("sender", 1)));

        var (sinkB, sinkFactoryB) = MemorySinkFactory();
        var receiverB = NewReceiver(key, network.CreateMulticastTransport(new Endpoint("b", 1)), sinkFactoryB, cacheBytes: 0);

        var lossyA = new FilteringMulticastTransport(
            network.CreateMulticastTransport(new Endpoint("a", 1)),
            message => message is not ChunkDataMessage { ChunkIndex: 3 });
        var (sinkA, sinkFactoryA) = MemorySinkFactory();
        var receiverA = NewReceiver(key, lossyA, sinkFactoryA, cacheBytes: 0, noJitterRepair: true);

        using var cts = new CancellationTokenSource(OverallTimeout);
        var senderTask = sender.RunAsync(cts.Token);
        var runA = receiverA.RunAsync(cts.Token);
        var runB = receiverB.RunAsync(cts.Token);
        var repairA = RunRepairLoopAsync(receiverA, TimeSpan.FromMilliseconds(100), cts.Token);

        while (!(receiverA.IsComplete && receiverB.IsComplete) && !cts.IsCancellationRequested)
            await Task.Delay(20, CancellationToken.None);

        await cts.CancelAsync();
        await Swallow(senderTask);
        await Swallow(runA);
        await Swallow(runB);
        await Swallow(repairA);

        Assert.True(receiverA.IsComplete, "the lost chunk was never recovered under a zero-byte chunk cache");
        Assert.True(receiverB.IsComplete);
        Assert.Equal(originalBytes, sinkA().ToArray());
        Assert.Equal(originalBytes, sinkB().ToArray());
    }

    // ---- harness ----

    private static ReceiverSession NewReceiver(
        NSec.Cryptography.Key key,
        IMulticastTransport transport,
        Func<string, long, IFileSink> sinkFactory,
        long cacheBytes,
        bool noJitterRepair = false) =>
        new(ReceiverId(1), TrustedStoreFor(key), transport, SystemClock.Instance,
            new ReceiverSessionOptions("/root", ChunkCacheBytes: cacheBytes), sinkFactory,
            repairCoordinator: noJitterRepair
                ? new RepairCoordinator(
                    new PeerTable(), SystemClock.Instance,
                    new RepairOptions(TimeSpan.FromMilliseconds(500), InitialRequestJitter: TimeSpan.Zero))
                : null,
            maxDatagramPayloadBytes: SingleDatagramBudget);

    /// <summary>An <see cref="IFileSink"/> that is only that — no read-back, so the cold path is unavailable.</summary>
    private sealed class WriteOnlySink(MemoryFileSink inner) : IFileSink
    {
        public ValueTask WriteAsync(long offset, ReadOnlyMemory<byte> data, CancellationToken cancellationToken = default) =>
            inner.WriteAsync(offset, data, cancellationToken);
    }

    /// <summary>
    /// A readable sink whose reads block on a caller-controlled gate — a stand-in for a cold-cache seek on a
    /// transfer larger than RAM, so a test can hold a rebuild in flight and observe what the receive loop does
    /// meanwhile. Writes are never blocked.
    /// </summary>
    private sealed class BlockingReadSink(int length, SemaphoreSlim gate) : IReadableFileSink
    {
        private readonly MemoryFileSink _inner = new(length);
        private int _readsStarted;
        private int _readsCompleted;

        public int ReadsStarted => Volatile.Read(ref _readsStarted);
        public int ReadsCompleted => Volatile.Read(ref _readsCompleted);

        public ValueTask WriteAsync(long offset, ReadOnlyMemory<byte> data, CancellationToken cancellationToken = default) =>
            _inner.WriteAsync(offset, data, cancellationToken);

        public async ValueTask<int> ReadAsync(long offset, Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _readsStarted);
            await gate.WaitAsync(cancellationToken);
            var read = await _inner.ReadAsync(offset, buffer, cancellationToken);
            Interlocked.Increment(ref _readsCompleted);
            return read;
        }
    }

    /// <summary>Decodes and records everything the session under test sends, so outbound behavior can be asserted.</summary>
    private sealed class OutboundRecorder(IMulticastTransport inner) : IMulticastTransport
    {
        private readonly List<ChunkResponseMessage> _chunkResponses = [];
        private readonly List<JoinRequestMessage> _joins = [];
        private readonly Lock _gate = new();

        public IReadOnlyList<ChunkResponseMessage> ChunkResponses
        {
            get { lock (_gate) return _chunkResponses.ToList(); }
        }

        public IReadOnlyList<JoinRequestMessage> Joins
        {
            get { lock (_gate) return _joins.ToList(); }
        }

        public ValueTask SendAsync(ReadOnlyMemory<byte> payload, CancellationToken cancellationToken = default)
        {
            object? decoded;
            try { decoded = MessageCodec.Decode(payload.ToArray()); }
            catch { decoded = null; }

            lock (_gate)
            {
                if (decoded is ChunkResponseMessage response)
                    _chunkResponses.Add(response);
                else if (decoded is JoinRequestMessage join)
                    _joins.Add(join);
            }

            return inner.SendAsync(payload, cancellationToken);
        }

        public IAsyncEnumerable<ReceivedPacket> ReceiveAsync(CancellationToken cancellationToken = default) =>
            inner.ReceiveAsync(cancellationToken);

        public ValueTask DisposeAsync() => inner.DisposeAsync();
    }

    private static async Task<JoinRequestMessage> WaitForJoinAsync(OutboundRecorder recorder, CancellationToken ct)
    {
        await WaitUntilAsync(() => recorder.Joins.Count > 0, ct);
        return recorder.Joins[0];
    }

    private sealed record Transfer(
        SignedManifest Signed,
        Dictionary<int, IFileSource> Sources,
        Dictionary<int, MerkleTree> Trees,
        NSec.Cryptography.Key SenderEncryptionKey,
        ContentKey ContentKey);

    private static Transfer BuildTransfer(NSec.Cryptography.Key key, string relativePath, byte[] bytes, int chunkSize)
    {
        var sessionId = new byte[16];
        var source = new MemoryFileSource(bytes);
        var contentKey = ContentKey.Generate();
        var senderEncryptionKey = EncryptionKeys.Create();

        var hashes = EncryptedChunkHasher
            .ComputeAsync(source, chunkSize, sessionId, fileIndex: 0, contentKey).GetAwaiter().GetResult();
        var tree = MerkleTree.Build(hashes);
        int chunkCount = ChunkLayout.ComputeChunkCount(bytes.Length, chunkSize);

        var manifest = new TransferManifest(
            sessionId, "chunk-cache-test", DateTimeOffset.UtcNow, EncryptionKeys.ExportPublicKey(senderEncryptionKey),
            [new ManifestFileEntry(relativePath, bytes.Length, chunkSize, chunkCount, tree.Root)]);

        return new Transfer(
            ManifestSigner.Sign(manifest, key),
            new Dictionary<int, IFileSource> { [0] = source },
            new Dictionary<int, MerkleTree> { [0] = tree },
            senderEncryptionKey,
            contentKey);
    }

    private static SenderSession NewSender(Transfer t, IMulticastTransport transport) =>
        new(t.Signed, t.Sources, t.Trees, transport, t.SenderEncryptionKey, t.ContentKey,
            maxDatagramPayloadBytes: SingleDatagramBudget);

    private static ITrustStore TrustedStoreFor(NSec.Cryptography.Key key)
    {
        var store = new InMemoryTrustStore();
        var publicKey = key.PublicKey.Export(NSec.Cryptography.KeyBlobFormat.RawPublicKey);
        store.Upsert(new TrustEntry(
            PublicKeyId.FromRawEd25519(publicKey), "test-sender", TrustStatus.Trusted, DateTimeOffset.UnixEpoch, TrustEntrySource.Manual));
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

    private static async Task RunUntilCompleteAsync(SenderSession sender, ReceiverSession receiver)
    {
        using var cts = new CancellationTokenSource(OverallTimeout);
        var senderTask = sender.RunAsync(cts.Token);
        var receiverTask = receiver.RunAsync(cts.Token);

        while (!receiver.IsComplete && !cts.IsCancellationRequested)
            await Task.Delay(20, CancellationToken.None);

        await cts.CancelAsync();
        await Swallow(senderTask);
        await Swallow(receiverTask);

        Assert.True(receiver.IsComplete, "Transfer did not complete within the test timeout.");
    }

    private static async Task RunRepairLoopAsync(ReceiverSession receiver, TimeSpan period, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            if (!receiver.IsComplete)
                await receiver.RequestRepairsAsync(ct);
            await Task.Delay(period, ct);
        }
    }

    private static async Task Swallow(Task task)
    {
        try { await task; }
        catch (OperationCanceledException) { }
    }

    private static ChunkDataMessage CarouselChunk(Transfer transfer, int fileIndex, int chunkIndex, int chunkSize)
    {
        var (ciphertext, proof) = EncryptChunk(transfer, fileIndex, chunkIndex, chunkSize);
        return new ChunkDataMessage(transfer.Signed.Manifest.SessionId, fileIndex, chunkIndex, ciphertext, proof);
    }

    private static (byte[] Ciphertext, MerkleProof Proof) EncryptChunk(
        Transfer transfer, int fileIndex, int chunkIndex, int chunkSize)
    {
        var sessionId = transfer.Signed.Manifest.SessionId;
        var plaintext = Chunker
            .ReadChunkAsync(transfer.Sources[fileIndex], chunkSize, chunkIndex).GetAwaiter().GetResult();
        return (transfer.ContentKey.EncryptChunk(sessionId, fileIndex, chunkIndex, plaintext),
                transfer.Trees[fileIndex].GetProof(chunkIndex));
    }

    private static async Task WaitUntilAsync(Func<bool> condition, CancellationToken ct)
    {
        while (!condition() && !ct.IsCancellationRequested)
            await Task.Delay(5, CancellationToken.None);
        Assert.True(condition(), "condition was not met before the test timeout");
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
