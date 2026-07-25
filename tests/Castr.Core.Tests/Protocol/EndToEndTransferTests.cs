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
/// Capstone integration tests: a real SenderSession and one or more real ReceiverSessions, wired together
/// over InMemoryNetwork, proving the whole M1 stack (trust, manifest, chunking, Merkle verification,
/// transport, repair) works end-to-end — not just each piece in isolation.
/// </summary>
public class EndToEndTransferTests
{
    private static readonly TimeSpan OverallTimeout = TimeSpan.FromSeconds(15);

    // These receivers run on SystemClock.Instance rather than a FakeClock. They are already real-time tests (they
    // poll until completion against OverallTimeout), and the FakeClock they used to be given was constructed at
    // UtcNow and then never advanced — so it measured nothing, while the repair path legitimately depends on wall
    // clock progressing: the carousel-idle valve and the randomized first-request jitter are both wall-clock, by
    // design (see RepairOptions.InitialRequestJitter for why counting repair passes instead was wrong). A frozen
    // clock therefore froze repair. Real clock here exercises the shipped behavior; the clock-sensitive logic
    // itself is unit-tested deterministically with FakeClock in RepairCoordinatorTests.
    //
    // The repair-dependent tests additionally inject NoJitterCoordinator(): on a real clock they would otherwise
    // depend on wall-clock jitter they do not test, spending 0-500 ms of their own budget on a randomized
    // first-request delay. Zeroing it restores the timing headroom without touching any assertion.

    /// <summary>
    /// A coordinator with both randomized timing sources off, for tests whose subject is repair <i>recovery</i>
    /// rather than repair <i>scheduling</i>. Keeps their margins governed by the assertion under test instead of
    /// by a draw from <see cref="Random.Shared"/>.
    /// </summary>
    private static RepairCoordinator NoJitterCoordinator() => new(
        new PeerTable(), SystemClock.Instance,
        new RepairOptions(TimeSpan.FromSeconds(5), RetryJitterFraction: 0, InitialRequestJitter: TimeSpan.Zero));

    [Fact]
    public async Task HappyPath_SingleReceiver_NoLoss_ReceivesByteIdenticalFile()
    {
        var originalBytes = RandomBytes(seed: 1, length: 50_000);
        using var key = ManifestSigner.CreateSigningKey();
        var transfer = BuildTransfer(key, "photo.jpg", originalBytes, chunkSize: 4096);

        var network = new InMemoryNetwork();
        var senderTransport = network.CreateMulticastTransport(new Endpoint("sender", 1));
        var receiverTransport = network.CreateMulticastTransport(new Endpoint("receiver", 1));

        var sender = NewSender(transfer, senderTransport);
        var trustStore = TrustedStoreFor(key);
        var (sink, sinkFactory) = MemorySinkFactory();
        var receiver = new ReceiverSession(
            ReceiverId(1), trustStore, receiverTransport, SystemClock.Instance,
            new ReceiverSessionOptions("/virtual-root"), sinkFactory);

        await RunUntilCompleteAsync(sender, receiver);

        Assert.True(receiver.IsComplete);
        Assert.Equal(originalBytes, sink().ToArray());
    }

    [Fact]
    public async Task MultipleReceivers_AllReceiveTheSameFile_FromOneSend()
    {
        var originalBytes = RandomBytes(seed: 2, length: 30_000);
        using var key = ManifestSigner.CreateSigningKey();
        var transfer = BuildTransfer(key, "shared.bin", originalBytes, chunkSize: 2048);

        var network = new InMemoryNetwork();
        var sender = NewSender(transfer, network.CreateMulticastTransport(new Endpoint("sender", 1)));

        var receivers = new List<ReceiverSession>();
        var sinks = new List<Func<MemoryFileSink>>();
        for (int i = 0; i < 5; i++)
        {
            var (sink, sinkFactory) = MemorySinkFactory();
            sinks.Add(sink);
            receivers.Add(new ReceiverSession(
                ReceiverId((byte)(10 + i)), TrustedStoreFor(key), network.CreateMulticastTransport(new Endpoint($"r{i}", 1)),
                SystemClock.Instance, new ReceiverSessionOptions("/root"), sinkFactory));
        }

        await RunUntilAllCompleteAsync(sender, receivers);

        Assert.All(receivers, r => Assert.True(r.IsComplete));
        Assert.All(sinks, sink => Assert.Equal(originalBytes, sink().ToArray()));
    }

    [Fact]
    public async Task UntrustedSender_ManifestRejected_NoBytesWritten_AndDenialEventFires()
    {
        var originalBytes = RandomBytes(seed: 3, length: 5000);
        using var key = ManifestSigner.CreateSigningKey();
        var transfer = BuildTransfer(key, "secret.doc", originalBytes, chunkSize: 1024);

        var network = new InMemoryNetwork();
        var sender = NewSender(transfer, network.CreateMulticastTransport(new Endpoint("sender", 1)));

        var (_, sinkFactory) = MemorySinkFactory();
        var receiver = new ReceiverSession(
            ReceiverId(1), new InMemoryTrustStore() /* sender key never trusted */, network.CreateMulticastTransport(new Endpoint("r", 1)),
            SystemClock.Instance, new ReceiverSessionOptions("/root", UnknownSenderPolicy.Deny), sinkFactory);

        TrustDecision? denial = null;
        receiver.SenderTrustDenied += (decision, _) => denial = decision;

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        var senderTask = sender.RunAsync(cts.Token);
        try { await receiver.RunAsync(cts.Token); } catch (OperationCanceledException) { }
        await cts.CancelAsync();
        await SwallowCancellation(senderTask);

        Assert.False(receiver.IsComplete);
        Assert.Null(receiver.Manifest);
        Assert.NotNull(denial);
        Assert.Equal(TrustOutcome.DeniedUnknown, denial!.Outcome);
    }

    [Fact]
    public async Task TamperedChunkPayload_FailsVerification_IsNeverWritten_RepairRecoversIt()
    {
        var originalBytes = RandomBytes(seed: 4, length: 8000);
        using var key = ManifestSigner.CreateSigningKey();
        var transfer = BuildTransfer(key, "data.bin", originalBytes, chunkSize: 1000);

        var network = new InMemoryNetwork();
        var senderTransport = network.CreateMulticastTransport(new Endpoint("sender", 1));
        var sender = NewSender(transfer, senderTransport);

        // A corrupting relay: tamper with chunk index 2's payload in-flight, everything else passes through.
        var rawReceiverTransport = network.CreateMulticastTransport(new Endpoint("r", 1));
        var corruptingTransport = new TamperingMulticastTransport(rawReceiverTransport, targetChunkIndex: 2);

        var (sink, sinkFactory) = MemorySinkFactory();
        var receiver = new ReceiverSession(
            ReceiverId(1), TrustedStoreFor(key), corruptingTransport, SystemClock.Instance,
            new ReceiverSessionOptions("/root"), sinkFactory, repairCoordinator: NoJitterCoordinator());

        // No second party holds the un-tampered chunk 2 in this scenario, so repair must fall back to
        // re-requesting from the original sender — and the sender's own carousel copy was never tampered.
        await RunUntilCompleteAsync(sender, receiver, extraRepairRounds: true);

        Assert.True(receiver.IsComplete);
        Assert.Equal(originalBytes, sink().ToArray());
    }

    [Fact]
    public async Task RelabeledGenuineChunk_ClaimingWrongPosition_NeverMarkedHave_RepairStillDeliversRealChunk()
    {
        // A relaying peer takes chunk 2's genuine (ciphertext, Merkle proof) off the wire and re-emits it
        // relabeled as chunk 5. Merkle verification alone would accept this — it only proves membership in the
        // tree, not that the claimed wire index matches the proof's own committed leaf index — so without the
        // ReceiverSession.HandleChunkAsync LeafIndex-vs-claimed-index binding, this would mark chunk 5 "have"
        // using chunk 2's ciphertext, which then fails AEAD decryption and is silently left pending forever
        // (never rewritten, never re-requested, since the bitmap already thinks chunk 5 is done). The fix
        // rejects the relabeled packet before the bitmap is ever touched, so the real chunk 5 — delivered
        // normally moments later by the same honest carousel/repair — still lands correctly.
        var originalBytes = RandomBytes(seed: 7, length: 8000); // chunkSize 1000 -> 8 chunks, indices 0-7
        using var key = ManifestSigner.CreateSigningKey();
        var transfer = BuildTransfer(key, "data.bin", originalBytes, chunkSize: 1000);

        var network = new InMemoryNetwork();
        var senderTransport = network.CreateMulticastTransport(new Endpoint("sender", 1));
        var sender = NewSender(transfer, senderTransport);

        var rawReceiverTransport = network.CreateMulticastTransport(new Endpoint("r", 1));
        var swappingTransport = new ChunkPositionSwappingMulticastTransport(rawReceiverTransport, sourceChunkIndex: 2, claimedChunkIndex: 5);

        var (sink, sinkFactory) = MemorySinkFactory();
        var receiver = new ReceiverSession(
            ReceiverId(1), TrustedStoreFor(key), swappingTransport, SystemClock.Instance,
            new ReceiverSessionOptions("/root"), sinkFactory, repairCoordinator: NoJitterCoordinator());

        await RunUntilCompleteAsync(sender, receiver, extraRepairRounds: true);

        Assert.True(receiver.IsComplete);
        Assert.Equal(originalBytes, sink().ToArray()); // chunk 5 holds its own genuine bytes, not chunk 2's
    }

    [Fact]
    public async Task RelabeledChunk_AlsoRewritingProofLeafIndex_StillNeverStalls()
    {
        // Stronger threat model than the test above: the relay rewrites BOTH the wire ChunkIndex AND the
        // proof's own (plaintext, wire-mutable) LeafIndex to the same lie, so a naive "proof.LeafIndex ==
        // claimed index" guard would pass. The chunk's Steps still commit to the genuine position, so binding
        // LeafIndex to the Steps inside MerkleProof.Verify is what rejects it; without that, chunk 5 would be
        // marked "have" from chunk 2's ciphertext, fail AEAD, and strand the transfer forever.
        var originalBytes = RandomBytes(seed: 11, length: 8000); // chunkSize 1000 -> 8 chunks, indices 0-7
        using var key = ManifestSigner.CreateSigningKey();
        var transfer = BuildTransfer(key, "data.bin", originalBytes, chunkSize: 1000);

        var network = new InMemoryNetwork();
        var sender = NewSender(transfer, network.CreateMulticastTransport(new Endpoint("sender", 1)));

        var rawReceiverTransport = network.CreateMulticastTransport(new Endpoint("r", 1));
        var swappingTransport = new ChunkAndLeafIndexSwappingTransport(rawReceiverTransport, sourceChunkIndex: 2, claimedChunkIndex: 5);

        var (sink, sinkFactory) = MemorySinkFactory();
        var receiver = new ReceiverSession(
            ReceiverId(1), TrustedStoreFor(key), swappingTransport, SystemClock.Instance,
            new ReceiverSessionOptions("/root"), sinkFactory, repairCoordinator: NoJitterCoordinator());

        await RunUntilCompleteAsync(sender, receiver, extraRepairRounds: true);

        Assert.True(receiver.IsComplete);
        Assert.Equal(originalBytes, sink().ToArray());
    }

    [Fact]
    public async Task ReceiverMissesChunk_RepairRecoversIt_ViaPeerOrSenderResponse()
    {
        // Note: this MVP broadcasts CHUNK_REQUEST over the shared multicast channel with no NACK
        // suppression, so both a peer that has the chunk AND the original sender may answer — it does
        // NOT prove "peer answered instead of sender," only that repair recovers the chunk regardless of
        // which source it came from. NACK-suppressed peer-preferred fulfillment is a documented follow-up.
        var originalBytes = RandomBytes(seed: 5, length: 12_000);
        using var key = ManifestSigner.CreateSigningKey();
        var transfer = BuildTransfer(key, "video.mp4", originalBytes, chunkSize: 1500);

        var network = new InMemoryNetwork();
        var senderTransport = network.CreateMulticastTransport(new Endpoint("sender", 1));
        var sender = NewSender(transfer, senderTransport);

        // Receiver B gets everything and will be available to serve repairs.
        var (sinkB, sinkFactoryB) = MemorySinkFactory();
        var receiverB = new ReceiverSession(
            ReceiverId(2), TrustedStoreFor(key), network.CreateMulticastTransport(new Endpoint("b", 1)),
            SystemClock.Instance, new ReceiverSessionOptions("/root"), sinkFactoryB);

        // Receiver A deterministically misses chunk index 3 of the carousel (simulated packet loss).
        var rawTransportA = network.CreateMulticastTransport(new Endpoint("a", 1));
        var lossyTransportA = new FilteringMulticastTransport(rawTransportA, message =>
            message is not ChunkDataMessage { ChunkIndex: 3 });
        var (sinkA, sinkFactoryA) = MemorySinkFactory();
        var receiverA = new ReceiverSession(
            ReceiverId(1), TrustedStoreFor(key), lossyTransportA, SystemClock.Instance,
            new ReceiverSessionOptions("/root"), sinkFactoryA, repairCoordinator: NoJitterCoordinator());

        await RunUntilAllCompleteAsync(sender, [receiverA, receiverB], extraRepairRounds: true);

        Assert.True(receiverA.IsComplete);
        Assert.True(receiverB.IsComplete);
        Assert.Equal(originalBytes, sinkA().ToArray());
        Assert.Equal(originalBytes, sinkB().ToArray());
    }

    [Fact]
    public async Task ChunkPayloadsOnTheWire_AreCiphertext_ReadableOnlyWithGrantedKey()
    {
        var originalBytes = RandomBytes(seed: 6, length: 9000);
        const int chunkSize = 1000;
        using var key = ManifestSigner.CreateSigningKey();
        var transfer = BuildTransfer(key, "top-secret.bin", originalBytes, chunkSize);

        var network = new InMemoryNetwork();
        var sender = NewSender(transfer, network.CreateMulticastTransport(new Endpoint("sender", 1)));

        // A passive observer of the multicast segment that never joins / trusts / holds the content key.
        var eavesdropperTransport = network.CreateMulticastTransport(new Endpoint("eve", 1));

        var (sink, sinkFactory) = MemorySinkFactory();
        var receiver = new ReceiverSession(
            ReceiverId(1), TrustedStoreFor(key), network.CreateMulticastTransport(new Endpoint("receiver", 1)),
            SystemClock.Instance, new ReceiverSessionOptions("/root"), sinkFactory);

        await RunUntilCompleteAsync(sender, receiver);

        Assert.True(receiver.IsComplete);
        Assert.Equal(originalBytes, sink().ToArray());

        // Every datagram the sender multicast during the transfer is already buffered in the eavesdropper's
        // inbox (an unbounded channel; no-chaos delivery is synchronous). Complete that channel and drain it in
        // the foreground so the capture is deterministic. The previous version read on a background Task.Run and
        // then cancelled a shared token: if the thread pool hadn't scheduled the reader before the cancel,
        // ReadAllAsync threw on the already-cancelled token *without draining the buffer*, capturing nothing —
        // an intermittent CI-only failure of the Assert.NotEmpty below.
        var captured = new List<ChunkDataMessage>();
        await eavesdropperTransport.DisposeAsync();
        await foreach (var packet in eavesdropperTransport.ReceiveAsync())
            if (MessageCodec.Decode(packet.Payload) is ChunkDataMessage cd)
                captured.Add(cd);

        // The eavesdropper saw ciphertext, and none of it equals the corresponding plaintext chunk.
        Assert.NotEmpty(captured);
        var sessionId = transfer.Signed.Manifest.SessionId;
        foreach (var cd in captured)
        {
            var range = ChunkLayout.GetRange(originalBytes.Length, chunkSize, cd.ChunkIndex);
            var plaintext = originalBytes.AsSpan((int)range.Offset, range.Length).ToArray();
            Assert.NotEqual(plaintext, cd.Payload);                       // not sent in the clear
            Assert.Equal(plaintext.Length + 16, cd.Payload.Length);       // ciphertext + AEAD tag

            // Only a party holding the content key (which the eavesdropper never obtained) can read it.
            var decrypted = transfer.ContentKey.TryDecryptChunk(sessionId, cd.FileIndex, cd.ChunkIndex, cd.Payload);
            Assert.Equal(plaintext, decrypted);
        }
    }

    // ---- helpers ----

    private sealed record Transfer(
        SignedManifest Signed,
        Dictionary<int, IFileSource> Sources,
        Dictionary<int, MerkleTree> Trees,
        NSec.Cryptography.Key SenderEncryptionKey,
        ContentKey ContentKey);

    private static Transfer BuildTransfer(
        NSec.Cryptography.Key key, string relativePath, byte[] bytes, int chunkSize)
    {
        var sessionId = SessionId();
        var source = new MemoryFileSource(bytes);
        var contentKey = ContentKey.Generate();
        var senderEncryptionKey = EncryptionKeys.Create();

        // Merkle tree is built over CIPHERTEXT chunk hashes (ADR-0003).
        var hashes = EncryptedChunkHasher.ComputeAsync(source, chunkSize, sessionId, fileIndex: 0, contentKey).GetAwaiter().GetResult();
        var tree = MerkleTree.Build(hashes);
        int chunkCount = ChunkLayout.ComputeChunkCount(bytes.Length, chunkSize);

        var manifest = new TransferManifest(
            sessionId, "test-transfer", DateTimeOffset.UtcNow, EncryptionKeys.ExportPublicKey(senderEncryptionKey),
            [new ManifestFileEntry(relativePath, bytes.Length, chunkSize, chunkCount, tree.Root)]);
        var signed = ManifestSigner.Sign(manifest, key);

        return new Transfer(
            signed,
            new Dictionary<int, IFileSource> { [0] = source },
            new Dictionary<int, MerkleTree> { [0] = tree },
            senderEncryptionKey,
            contentKey);
    }

    // These InMemory tests inspect/tamper/filter whole ChunkDataMessages on the wire, so the sender is pinned
    // to a large datagram budget that keeps each chunk in a single (unfragmented) datagram. Fragmentation
    // itself is exercised by the WirePacketizer/PacketReassembler unit tests and the real-socket integration
    // tests; receivers here still use the default ~1200-byte budget, so repair responses do get fragmented and
    // reassembled end-to-end.
    private const int SingleDatagramBudget = 65_000;

    private static SenderSession NewSender(Transfer t, IMulticastTransport transport) =>
        new(t.Signed, t.Sources, t.Trees, transport, t.SenderEncryptionKey, t.ContentKey, maxDatagramPayloadBytes: SingleDatagramBudget);

    private static ITrustStore TrustedStoreFor(NSec.Cryptography.Key key)
    {
        var store = new InMemoryTrustStore();
        var publicKey = key.PublicKey.Export(NSec.Cryptography.KeyBlobFormat.RawPublicKey);
        store.Upsert(new TrustEntry(PublicKeyId.FromRawEd25519(publicKey), "test-sender", TrustStatus.Trusted, DateTimeOffset.UnixEpoch, TrustEntrySource.Manual));
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

    private static async Task RunUntilCompleteAsync(SenderSession sender, ReceiverSession receiver, bool extraRepairRounds = false) =>
        await RunUntilAllCompleteAsync(sender, [receiver], extraRepairRounds);

    private static async Task RunUntilAllCompleteAsync(SenderSession sender, IReadOnlyList<ReceiverSession> receivers, bool extraRepairRounds = false)
    {
        using var cts = new CancellationTokenSource(OverallTimeout);
        var senderTask = sender.RunAsync(cts.Token);
        var receiverTasks = receivers.Select(r => r.RunAsync(cts.Token)).ToList();

        Task repairLoopTask = extraRepairRounds
            ? RunRepairLoopAsync(receivers, cts.Token)
            : Task.CompletedTask;

        while (!receivers.All(r => r.IsComplete) && !cts.IsCancellationRequested)
            await Task.Delay(20, CancellationToken.None);

        await cts.CancelAsync();
        await SwallowCancellation(senderTask);
        await SwallowCancellation(Task.WhenAll(receiverTasks));
        await SwallowCancellation(repairLoopTask);

        Assert.True(receivers.All(r => r.IsComplete), "Transfer did not complete within the test timeout.");
    }

    private static async Task RunRepairLoopAsync(IReadOnlyList<ReceiverSession> receivers, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            foreach (var receiver in receivers.Where(r => !r.IsComplete))
                await receiver.RequestRepairsAsync(ct);
            await Task.Delay(50, ct);
        }
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

    private static byte[] SessionId() => new byte[16];

    private static byte[] ReceiverId(byte fill)
    {
        var bytes = new byte[16];
        Array.Fill(bytes, fill);
        return bytes;
    }

    /// <summary>Test-only decorator that flips bytes in one specific chunk's payload in-flight, to prove Merkle verification actually rejects tampering rather than just trusting the sender.</summary>
    private sealed class TamperingMulticastTransport(IMulticastTransport inner, int targetChunkIndex) : IMulticastTransport
    {
        public ValueTask SendAsync(ReadOnlyMemory<byte> payload, CancellationToken cancellationToken = default) =>
            inner.SendAsync(payload, cancellationToken);

        public async IAsyncEnumerable<ReceivedPacket> ReceiveAsync(
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await foreach (var packet in inner.ReceiveAsync(cancellationToken))
            {
                object? decoded;
                try { decoded = MessageCodec.Decode(packet.Payload); }
                catch { decoded = null; }

                if (decoded is ChunkDataMessage { ChunkIndex: var idx } chunkData && idx == targetChunkIndex)
                {
                    var tamperedPayload = (byte[])chunkData.Payload.Clone();
                    tamperedPayload[0] ^= 0xFF;
                    var tampered = chunkData with { Payload = tamperedPayload };
                    yield return new ReceivedPacket(MessageCodec.Encode(tampered), packet.From);
                    continue;
                }

                yield return packet;
            }
        }

        public ValueTask DisposeAsync() => inner.DisposeAsync();
    }

    /// <summary>
    /// Simulates a relaying peer that takes one genuine (ciphertext, proof) pair for <paramref name="sourceChunkIndex"/>
    /// and re-emits it exactly once, unmodified except for the wire-claimed <c>ChunkIndex</c>, now lying that it's
    /// <paramref name="claimedChunkIndex"/>. Only the first matching packet is relabeled; everything else (including
    /// later genuine deliveries of both indices) passes through untouched, so the test can observe whether the
    /// system recovers cleanly rather than getting permanently stuck.
    /// </summary>
    private sealed class ChunkPositionSwappingMulticastTransport(IMulticastTransport inner, int sourceChunkIndex, int claimedChunkIndex) : IMulticastTransport
    {
        private bool _swapped;

        public ValueTask SendAsync(ReadOnlyMemory<byte> payload, CancellationToken cancellationToken = default) =>
            inner.SendAsync(payload, cancellationToken);

        public async IAsyncEnumerable<ReceivedPacket> ReceiveAsync(
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await foreach (var packet in inner.ReceiveAsync(cancellationToken))
            {
                object? decoded;
                try { decoded = MessageCodec.Decode(packet.Payload); }
                catch { decoded = null; }

                if (!_swapped && decoded is ChunkDataMessage { ChunkIndex: var idx } chunkData && idx == sourceChunkIndex)
                {
                    _swapped = true;
                    var relabeled = chunkData with { ChunkIndex = claimedChunkIndex };
                    yield return new ReceivedPacket(MessageCodec.Encode(relabeled), packet.From);
                    continue;
                }

                yield return packet;
            }
        }

        public ValueTask DisposeAsync() => inner.DisposeAsync();
    }

    /// <summary>
    /// Like <see cref="ChunkPositionSwappingMulticastTransport"/> but also rewrites the proof's own LeafIndex to
    /// the lie, modeling a relay that knows the naive guard compares those two fields. Only the first matching
    /// packet is relabeled; everything else passes through, so the test observes clean recovery vs. a stall.
    /// </summary>
    private sealed class ChunkAndLeafIndexSwappingTransport(IMulticastTransport inner, int sourceChunkIndex, int claimedChunkIndex) : IMulticastTransport
    {
        private bool _swapped;

        public ValueTask SendAsync(ReadOnlyMemory<byte> payload, CancellationToken cancellationToken = default) =>
            inner.SendAsync(payload, cancellationToken);

        public async IAsyncEnumerable<ReceivedPacket> ReceiveAsync(
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await foreach (var packet in inner.ReceiveAsync(cancellationToken))
            {
                object? decoded;
                try { decoded = MessageCodec.Decode(packet.Payload); }
                catch { decoded = null; }

                if (!_swapped && decoded is ChunkDataMessage { ChunkIndex: var idx } chunkData && idx == sourceChunkIndex)
                {
                    _swapped = true;
                    var relabeled = chunkData with
                    {
                        ChunkIndex = claimedChunkIndex,
                        Proof = chunkData.Proof with { LeafIndex = claimedChunkIndex },
                    };
                    yield return new ReceivedPacket(MessageCodec.Encode(relabeled), packet.From);
                    continue;
                }

                yield return packet;
            }
        }

        public ValueTask DisposeAsync() => inner.DisposeAsync();
    }
}
