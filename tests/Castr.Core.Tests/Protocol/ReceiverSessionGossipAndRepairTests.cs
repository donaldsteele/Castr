using System.Runtime.CompilerServices;
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
/// Behavioral coverage for the M7 receiver-side changes: PEER_HAVE coalescing (rate-limited, off the state
/// gate, always emitted on completion) and repair gating by the carousel watermark (including the
/// dropped-final-chunk safety valve, without which the watermark would deadlock a transfer).
/// </summary>
public class ReceiverSessionGossipAndRepairTests
{
    private static readonly TimeSpan OverallTimeout = TimeSpan.FromSeconds(30);

    // ---- P1: PEER_HAVE coalescing ----

    [Fact]
    public async Task PeerHave_IsNotEmittedPerChunk_WhenTheIntervalNeverElapses()
    {
        // A clock that never advances means the PeerHaveInterval is never satisfied, so only the two
        // interval-exempt emissions should happen: the file's first chunk and its bitmap becoming complete.
        // Before coalescing this emitted one whole-bitmap broadcast per verified chunk — quadratic gossip.
        var (recorded, chunkCount) = await RunTransferAsync(chunkSize: 512, length: 20_480, clockStepPerPacket: TimeSpan.Zero);

        Assert.Equal(40, chunkCount);
        Assert.Equal(2, recorded.PeerHaves.Count);
        Assert.True(recorded.PeerHaves.Count < chunkCount, "coalescing must emit far fewer than one per chunk");
    }

    [Fact]
    public async Task PeerHave_FirstEmission_HappensOnTheFirstChunk_SoDiscoveryStillWorksEarly()
    {
        var (recorded, chunkCount) = await RunTransferAsync(chunkSize: 512, length: 20_480, clockStepPerPacket: TimeSpan.Zero);

        // A receiver must announce itself promptly, not only once the whole file has landed, or peers cannot
        // discover it mid-transfer (see wiki/concepts/repair-protocol.md's PEER_HAVE-as-discovery role).
        var first = recorded.PeerHaves[0];
        int setInFirst = ChunkBitmap.FromBytes(chunkCount, first.ChunkBitmap).CountSet();
        Assert.Equal(1, setInFirst);
    }

    [Fact]
    public async Task PeerHave_CompletionIsAlwaysAnnounced_EvenWhenTheIntervalHasNotElapsed()
    {
        var (recorded, chunkCount) = await RunTransferAsync(chunkSize: 512, length: 20_480, clockStepPerPacket: TimeSpan.Zero);

        // The interval is never satisfied here, so this can only be the completion exemption firing.
        var last = recorded.PeerHaves[^1];
        Assert.True(ChunkBitmap.FromBytes(chunkCount, last.ChunkBitmap).IsComplete,
            "peers must learn about a complete source promptly, regardless of the rate limit");
    }

    [Fact]
    public async Task PeerHave_EmitsRepeatedly_WhenTheIntervalDoesElapse()
    {
        // Same transfer, but the clock advances past PeerHaveInterval between packets, so the rate limit is
        // satisfied every time. This is the other half of the A/B: it proves the interval is genuinely what
        // gates emission, rather than PEER_HAVE having been throttled to "first and last" unconditionally.
        var step = ReceiverSession.DefaultPeerHaveInterval + TimeSpan.FromMilliseconds(50);
        var (recorded, chunkCount) = await RunTransferAsync(chunkSize: 512, length: 20_480, clockStepPerPacket: step);

        Assert.True(recorded.PeerHaves.Count > 2,
            $"expected many emissions once the interval elapses, got {recorded.PeerHaves.Count}");
        Assert.True(recorded.PeerHaves.Count <= chunkCount + 1, "still at most one per verified chunk");
    }

    [Fact]
    public async Task PeerHave_BitmapsAreMonotonic_AndTheLastOneIsComplete()
    {
        var step = ReceiverSession.DefaultPeerHaveInterval + TimeSpan.FromMilliseconds(50);
        var (recorded, chunkCount) = await RunTransferAsync(chunkSize: 512, length: 20_480, clockStepPerPacket: step);

        // Each emission carries a snapshot taken under the state gate, so the sequence must be non-decreasing —
        // a torn or stale snapshot (the risk of moving the send outside the gate) would show up here.
        int previous = 0;
        foreach (var peerHave in recorded.PeerHaves)
        {
            int set = ChunkBitmap.FromBytes(chunkCount, peerHave.ChunkBitmap).CountSet();
            Assert.True(set >= previous, $"PEER_HAVE bitmaps went backwards: {set} after {previous}");
            previous = set;
        }
        Assert.Equal(chunkCount, previous);
    }

    // ---- P0: carousel watermark, and the dropped-final-chunk safety valve ----

    [Fact]
    public async Task Transfer_FinalChunkOfFileDropped_StillCompletesViaRepair()
    {
        // THE critical correctness test for the carousel watermark. A missing index is normally only eligible for
        // repair once the carousel has demonstrably passed it (index <= highest seen). If the file's LAST chunk
        // is the one lost, no higher index will ever arrive, so the watermark can never rise above it — without
        // the CarouselIdleThreshold safety valve this transfer would hang forever rather than repairing.
        var originalBytes = RandomBytes(seed: 71, length: 12_000);
        const int chunkSize = 1000;
        int chunkCount = ChunkLayout.ComputeChunkCount(originalBytes.Length, chunkSize);
        int finalChunkIndex = chunkCount - 1;

        using var key = ManifestSigner.CreateSigningKey();
        var transfer = BuildTransfer(key, "tail-loss.bin", originalBytes, chunkSize);

        var network = new InMemoryNetwork();
        var sender = NewSender(transfer, network.CreateMulticastTransport(new Endpoint("sender", 1)));

        // Drop the final chunk exactly once: the carousel's only delivery of it is lost, so it can be recovered
        // only by a repair request that the watermark alone would never permit. The repair re-send (a
        // ChunkResponse, not a ChunkData) passes through.
        bool dropped = false;
        var lossy = new FilteringMulticastTransport(
            network.CreateMulticastTransport(new Endpoint("r", 1)),
            message =>
            {
                if (message is ChunkDataMessage cd && cd.ChunkIndex == finalChunkIndex && !dropped)
                {
                    dropped = true;
                    return false;
                }
                return true;
            });

        var (sink, sinkFactory) = MemorySinkFactory();
        // Real clock: the safety valve is a wall-clock idle threshold, and this test's whole point is that it
        // actually fires. A frozen FakeClock would never reach it — which is exactly the deadlock being guarded
        // against, so pinning time here would make the test vacuous.
        var receiver = new ReceiverSession(
            ReceiverId(1), TrustedStoreFor(key), lossy, SystemClock.Instance,
            new ReceiverSessionOptions("/root"), sinkFactory,
            repairCoordinator: new RepairCoordinator(
                new PeerTable(), SystemClock.Instance,
                // No jitter, so the test does not also depend on how many passes were randomly skipped.
                new RepairOptions(TimeSpan.FromSeconds(1), InitialRequestJitter: TimeSpan.Zero)));

        await RunUntilCompleteAsync(sender, receiver, repairPeriod: TimeSpan.FromMilliseconds(100));

        Assert.True(dropped, "fixture must actually have dropped the final chunk");
        Assert.True(receiver.IsComplete);
        Assert.Equal(originalBytes, sink().ToArray()); // including the tail chunk, recovered by repair
    }

    [Fact]
    public async Task Transfer_FinalChunkDropped_CompletesEvenWhileOtherChunkTrafficKeepsArriving()
    {
        // REGRESSION (QA round 2): the carousel-idle valve was originally one GLOBAL last-chunk-arrival timestamp
        // refreshed by any chunk-bearing datagram for any file. Because repair responses are multicast by design,
        // that meant any other receiver's repair traffic — or, as here, a re-delivery of a chunk already held —
        // refreshed the valve forever, so a file whose final chunk was lost could never become eligible and the
        // transfer hung. This is a liveness property ("always eventually completes"), so it needs no adversary
        // and no loss beyond the one dropped chunk.
        //
        // The fix keys the valve on that file's watermark actually ADVANCING from a carousel delivery, which makes
        // a re-delivery of an already-seen index inert — exactly what it is as evidence about carousel position.
        var originalBytes = RandomBytes(seed: 74, length: 12_000);
        const int chunkSize = 1000;
        int chunkCount = ChunkLayout.ComputeChunkCount(originalBytes.Length, chunkSize);
        int finalChunkIndex = chunkCount - 1;

        using var key = ManifestSigner.CreateSigningKey();
        var transfer = BuildTransfer(key, "tail-loss-with-noise.bin", originalBytes, chunkSize);

        var network = new InMemoryNetwork();
        var senderTransport = network.CreateMulticastTransport(new Endpoint("sender", 1));
        var sender = NewSender(transfer, senderTransport);

        // Drop the final chunk's only carousel delivery, and capture an early chunk to replay as background noise.
        byte[]? replayable = null;
        bool dropped = false;
        var lossy = new FilteringMulticastTransport(
            network.CreateMulticastTransport(new Endpoint("r", 1)),
            message =>
            {
                if (message is ChunkDataMessage cd)
                {
                    if (cd.ChunkIndex == finalChunkIndex && !dropped)
                    {
                        dropped = true;
                        return false;
                    }
                    replayable ??= MessageCodec.Encode(cd); // an already-held chunk, to re-inject repeatedly
                }
                return true;
            });

        var (sink, sinkFactory) = MemorySinkFactory();
        var receiver = new ReceiverSession(
            ReceiverId(1), TrustedStoreFor(key), lossy, SystemClock.Instance,
            new ReceiverSessionOptions("/root", CarouselIdleThreshold: TimeSpan.FromMilliseconds(300)), sinkFactory,
            repairCoordinator: new RepairCoordinator(
                new PeerTable(), SystemClock.Instance,
                new RepairOptions(TimeSpan.FromMilliseconds(500), InitialRequestJitter: TimeSpan.Zero)));

        using var cts = new CancellationTokenSource(OverallTimeout);
        var senderTask = sender.RunAsync(cts.Token);
        var receiverTask = receiver.RunAsync(cts.Token);
        var repairTask = RunRepairLoopAsync(receiver, TimeSpan.FromMilliseconds(100), cts.Token);

        // The noise generator: keep re-injecting a chunk the receiver already holds. Under the old global
        // arrival-based timer this alone was enough to hold the valve shut indefinitely and hang the transfer.
        var noiseTask = Task.Run(async () =>
        {
            while (!cts.IsCancellationRequested)
            {
                if (replayable is { } datagram)
                    await senderTransport.SendAsync(datagram, CancellationToken.None);
                await Task.Delay(200, cts.Token);
            }
        }, cts.Token);

        while (!receiver.IsComplete && !cts.IsCancellationRequested)
            await Task.Delay(20, CancellationToken.None);

        await cts.CancelAsync();
        await Swallow(senderTask);
        await Swallow(receiverTask);
        await Swallow(repairTask);
        await Swallow(noiseTask);

        Assert.True(dropped, "fixture must actually have dropped the final chunk");
        Assert.NotNull(replayable); // and must actually have generated noise
        Assert.True(receiver.IsComplete, "transfer hung: the carousel-idle valve never opened despite ongoing noise");
        Assert.Equal(originalBytes, sink().ToArray());
    }

    [Fact]
    public async Task MultiFileTransfer_WithAFileTailLost_CompletesWhileAnotherFileIsStillArriving()
    {
        // End-to-end completion cover for the multi-file case. NOTE this does not, on its own, demonstrate the
        // per-file valve property in its name-worthy form: over an in-memory transport both carousels finish in
        // single-digit milliseconds, well inside any workable threshold, so file 1 is long done either way. The
        // per-file property itself is tested directly and deterministically in
        // RepairEligibility_OneFilesTrafficDoesNotHoldAnotherFilesValveShut; this one exists to prove the whole
        // multi-file transfer still completes byte-identically with sustained cross-file traffic in flight.
        var fileBytes = new[] { RandomBytes(seed: 81, length: 6_000), RandomBytes(seed: 82, length: 30_000) };
        const int chunkSize = 1000;

        using var key = ManifestSigner.CreateSigningKey();
        var transfer = BuildMultiFileTransfer(key, fileBytes, chunkSize);
        int file0FinalChunk = ChunkLayout.ComputeChunkCount(fileBytes[0].Length, chunkSize) - 1;

        var network = new InMemoryNetwork();
        var senderTransport = network.CreateMulticastTransport(new Endpoint("sender", 1));
        var sender = NewSender(transfer, senderTransport);

        bool dropped = false;
        byte[]? file1Noise = null;
        var lossy = new FilteringMulticastTransport(
            network.CreateMulticastTransport(new Endpoint("r", 1)),
            message =>
            {
                if (message is ChunkDataMessage cd)
                {
                    if (cd.FileIndex == 0 && cd.ChunkIndex == file0FinalChunk && !dropped)
                    {
                        dropped = true;
                        return false; // file 0's tail lost, while file 1's carousel is still to come
                    }
                    if (cd.FileIndex == 1)
                        file1Noise ??= MessageCodec.Encode(cd); // sustained file-1 traffic, replayed below
                }
                return true;
            });

        var sinks = new Dictionary<int, MemoryFileSink>();
        var receiver = new ReceiverSession(
            ReceiverId(1), TrustedStoreFor(key), lossy, SystemClock.Instance,
            new ReceiverSessionOptions("/root", CarouselIdleThreshold: TimeSpan.FromMilliseconds(300)),
            (path, length) =>
            {
                var sink = new MemoryFileSink((int)length);
                sinks[sinks.Count] = sink;
                return sink;
            },
            repairCoordinator: new RepairCoordinator(
                new PeerTable(), SystemClock.Instance,
                new RepairOptions(TimeSpan.FromMilliseconds(500), InitialRequestJitter: TimeSpan.Zero)));

        using var cts = new CancellationTokenSource(OverallTimeout);
        var senderTask = sender.RunAsync(cts.Token);
        var receiverTask = receiver.RunAsync(cts.Token);
        var repairTask = RunRepairLoopAsync(receiver, TimeSpan.FromMilliseconds(100), cts.Token);

        // Keep FILE 1 traffic flowing for the whole test. Under a single global timer this refreshed file 0's
        // valve too, so file 0's lost tail stayed unrequestable and the transfer never completed.
        var noiseTask = Task.Run(async () =>
        {
            while (!cts.IsCancellationRequested)
            {
                if (file1Noise is { } datagram)
                    await senderTransport.SendAsync(datagram, CancellationToken.None);
                await Task.Delay(150, cts.Token);
            }
        }, cts.Token);

        while (!receiver.IsComplete && !cts.IsCancellationRequested)
            await Task.Delay(20, CancellationToken.None);

        await cts.CancelAsync();
        await Swallow(senderTask);
        await Swallow(receiverTask);
        await Swallow(repairTask);
        await Swallow(noiseTask);

        Assert.True(dropped);
        Assert.NotNull(file1Noise);
        Assert.True(receiver.IsComplete, "file 0's valve was held shut by file 1's traffic");
        Assert.Equal(fileBytes[0], sinks[0].ToArray());
        Assert.Equal(fileBytes[1], sinks[1].ToArray());
    }

    [Fact]
    public async Task RepairEligibility_FileWhoseCarouselHasNotStarted_IsNeverRequested_EvenPastTheIdleThreshold()
    {
        // REGRESSION (systems-design round 3), tested directly rather than through a live carousel.
        //
        // Why not end-to-end: an in-memory carousel finishes in single-digit milliseconds, so the mid-carousel
        // window in which this defect is observable is far shorter than the time it takes the repair loop to
        // accumulate enough passes (each separated by Task.Delay(repairPeriod)) to sample it. The window is too
        // short to hit reliably — it is NOT that the repair pass is blocked: SemaphoreSlim.WaitAsync queues
        // waiters FIFO, so once RequestRepairsAsync is queued it takes the gate at RunAsync's next release and
        // cannot be starved. (Confirmed empirically: with this defect live, the end-to-end version passed.)
        // Feeding the receiver by hand with a FakeClock removes the race entirely.
        //
        // The bug: HandleManifestAsync seeded EVERY file's carousel-idle timer at manifest acceptance, but the
        // sender carousels files sequentially. So file 1's valve opened one threshold after the manifest —
        // before its carousel had begun — and its entire chunk set became repair-eligible while file 0 was still
        // transmitting, with the round-robin cursor handing it budget on alternating passes.
        const int chunkSize = 1000;
        var fileBytes = new[] { RandomBytes(seed: 96, length: 20_000), RandomBytes(seed: 97, length: 8_000) };
        var idleThreshold = TimeSpan.FromMilliseconds(500);

        using var key = ManifestSigner.CreateSigningKey();
        var transfer = BuildMultiFileTransfer(key, fileBytes, chunkSize);
        var sessionId = transfer.Signed.Manifest.SessionId;

        var network = new InMemoryNetwork();
        var injector = network.CreateMulticastTransport(new Endpoint("sender", 1));
        var observer = new FileTrafficObserver(network.CreateMulticastTransport(new Endpoint("r", 1)));

        var clock = new FakeClock(DateTimeOffset.Parse("2026-07-25T00:00:00Z"));
        int verifiedChunks = 0;
        var receiver = new ReceiverSession(
            ReceiverId(1), TrustedStoreFor(key), observer, clock,
            new ReceiverSessionOptions("/root", CarouselIdleThreshold: idleThreshold),
            (_, length) => new MemoryFileSink((int)length),
            repairCoordinator: new RepairCoordinator(
                new PeerTable(), clock,
                new RepairOptions(TimeSpan.FromSeconds(5), InitialRequestJitter: TimeSpan.Zero)));
        receiver.ProgressChanged += p => Volatile.Write(ref verifiedChunks, p.CompletedChunks);

        using var cts = new CancellationTokenSource(OverallTimeout);
        var run = receiver.RunAsync(cts.Token);

        await injector.SendAsync(MessageCodec.Encode(new ManifestMessage(transfer.Signed)), cts.Token);
        await WaitUntilAsync(() => receiver.Manifest is not null, cts.Token);

        // File 0's carousel is STILL RUNNING: it delivers 0, then 2-5 (chunk 1 lost), with the clock advanced by
        // half a threshold between chunks. That is the crux of the setup — total elapsed time since the manifest
        // ends up well past the idle threshold (which is what opened file 1's valve under the bug), while no
        // single gap in carousel activity ever reaches the threshold (so the session-level escape hatch stays
        // shut and cannot pass this test on file 1's behalf). Meanwhile file 1 has not been sent at all.
        int delivered = 0;
        for (int chunkIndex = 0; chunkIndex <= 5; chunkIndex++)
        {
            if (chunkIndex == 1)
                continue; // lost — the below-watermark gap used as this test's positive control
            await injector.SendAsync(MessageCodec.Encode(CarouselChunk(transfer, 0, chunkIndex, chunkSize)), cts.Token);
            delivered++;
            await WaitUntilAsync(() => Volatile.Read(ref verifiedChunks) >= delivered, cts.Token);
            clock.Advance(idleThreshold / 2);
        }

        await receiver.RequestRepairsAsync(cts.Token);

        await cts.CancelAsync();
        await Swallow(run);

        var file1Requests = observer.OutboundRequests.Where(r => r.FileIndex == 1).ToList();
        Assert.True(file1Requests.Count == 0,
            $"{file1Requests.Count} CHUNK_REQUEST(s) covering {file1Requests.Sum(r => r.ChunkIndices.Length)} " +
            "indices were sent for file 1, whose carousel has not started — the sender has transmitted none of them");

        // Positive control, so the rule cannot pass by suppressing repair wholesale: file 0 HAS started and has
        // gone quiet, so its own gap must be requested in the very same pass that correctly ignores file 1.
        var file0Requested = observer.OutboundRequests
            .Where(r => r.FileIndex == 0).SelectMany(r => r.ChunkIndices).ToHashSet();
        Assert.Contains(1, file0Requested);
    }

    [Fact]
    public async Task ChunkResponse_DoesNotRefreshTheCarouselValve_OnlyCarouselDeliveriesDo()
    {
        // REGRESSION (QA round-3 mutation B): removing ONLY the `fromCarousel` guard — keeping per-file timers
        // and the novelty early-return — left the whole suite green, so the guard could have been deleted
        // silently. Nothing exercised the `fromCarousel: false` branch at all, because every existing test used
        // replayed CHUNK_DATA as noise, which the novelty early-return alone renders inert.
        //
        // This is the multi-receiver shape the guard exists for. CHUNK_RESPONSE is multicast, so the repair it
        // answers may be a DIFFERENT receiver's: peer B false-idles at a high carousel position, the sender
        // re-serves high indices, and this receiver observes them with its own valve still shut. A novel
        // above-watermark CHUNK_RESPONSE must lift the watermark (it is real evidence the chunk exists on the
        // wire) but must NOT refresh the idle timer, because it says nothing about where the carousel is.
        const int chunkSize = 1000;
        const int chunkCount = 30;
        var originalBytes = RandomBytes(seed: 101, length: chunkSize * chunkCount);
        var idleThreshold = TimeSpan.FromMilliseconds(500);

        using var key = ManifestSigner.CreateSigningKey();
        var transfer = BuildTransfer(key, "valve.bin", originalBytes, chunkSize);

        var network = new InMemoryNetwork();
        var injector = network.CreateMulticastTransport(new Endpoint("sender", 1));
        var observer = new FileTrafficObserver(network.CreateMulticastTransport(new Endpoint("r", 1)));

        var clock = new FakeClock(DateTimeOffset.Parse("2026-07-25T00:00:00Z"));
        int verifiedChunks = 0;
        var receiver = new ReceiverSession(
            ReceiverId(1), TrustedStoreFor(key), observer, clock,
            new ReceiverSessionOptions("/root", CarouselIdleThreshold: idleThreshold),
            (_, length) => new MemoryFileSink((int)length),
            repairCoordinator: new RepairCoordinator(
                new PeerTable(), clock,
                new RepairOptions(TimeSpan.FromSeconds(5), InitialRequestJitter: TimeSpan.Zero)));
        receiver.ProgressChanged += p => Volatile.Write(ref verifiedChunks, p.CompletedChunks);

        using var cts = new CancellationTokenSource(OverallTimeout);
        var run = receiver.RunAsync(cts.Token);

        await injector.SendAsync(MessageCodec.Encode(new ManifestMessage(transfer.Signed)), cts.Token);
        await WaitUntilAsync(() => receiver.Manifest is not null, cts.Token);

        // Carousel delivers 0-4 and then stops; 5-29 are lost. Watermark 4, idle timer set here.
        for (int chunkIndex = 0; chunkIndex <= 4; chunkIndex++)
            await injector.SendAsync(MessageCodec.Encode(CarouselChunk(transfer, 0, chunkIndex, chunkSize)), cts.Token);
        await WaitUntilAsync(() => Volatile.Read(ref verifiedChunks) >= 5, cts.Token);

        // The carousel has now been quiet for longer than the threshold, so the valve is open.
        clock.Advance(idleThreshold + TimeSpan.FromSeconds(1));

        // A repair reply for a novel, far-above-watermark index arrives (peer B's repair, served by the sender).
        // It lifts the watermark 4 -> 25. Under the fix it leaves the idle timer alone, so the valve stays open
        // and the still-missing tail 26-29 remains requestable. Without the `fromCarousel` guard this refreshes
        // the timer, slams the valve shut, and 26-29 become unrequestable again.
        await injector.SendAsync(MessageCodec.Encode(RepairChunk(transfer, 0, 25, chunkSize)), cts.Token);
        await WaitUntilAsync(() => Volatile.Read(ref verifiedChunks) >= 6, cts.Token);

        await receiver.RequestRepairsAsync(cts.Token);

        await cts.CancelAsync();
        await Swallow(run);

        var requested = observer.OutboundRequests.SelectMany(r => r.ChunkIndices).ToHashSet();
        var aboveWatermark = requested.Where(i => i > 25).ToList();

        Assert.True(aboveWatermark.Count > 0,
            "the tail above the watermark was not requested — a CHUNK_RESPONSE refreshed the carousel-idle " +
            "valve, which only a genuine carousel delivery may do");
        // Sanity: the below-watermark gaps were requested too, so this is not passing by requesting everything blindly.
        Assert.Contains(5, requested);
    }

    [Fact]
    public async Task RepairEligibility_OneFilesTrafficDoesNotHoldAnotherFilesValveShut()
    {
        // The per-file property, tested directly. The previous end-to-end version of this could not observe its
        // own stated property: over an in-memory transport both files' carousels completed in single-digit
        // milliseconds, well inside the threshold, so file 1 was long finished either way and the test collapsed
        // into a near-duplicate of the replay-noise one. Driving the receiver by hand with a FakeClock lets file
        // 1's traffic genuinely overlap file 0's idle window.
        const int chunkSize = 1000;
        var fileBytes = new[] { RandomBytes(seed: 102, length: 20_000), RandomBytes(seed: 103, length: 20_000) };
        var idleThreshold = TimeSpan.FromMilliseconds(500);

        using var key = ManifestSigner.CreateSigningKey();
        var transfer = BuildMultiFileTransfer(key, fileBytes, chunkSize);

        var network = new InMemoryNetwork();
        var injector = network.CreateMulticastTransport(new Endpoint("sender", 1));
        var observer = new FileTrafficObserver(network.CreateMulticastTransport(new Endpoint("r", 1)));

        var clock = new FakeClock(DateTimeOffset.Parse("2026-07-25T00:00:00Z"));
        int verifiedChunks = 0;
        var receiver = new ReceiverSession(
            ReceiverId(1), TrustedStoreFor(key), observer, clock,
            new ReceiverSessionOptions("/root", CarouselIdleThreshold: idleThreshold),
            (_, length) => new MemoryFileSink((int)length),
            repairCoordinator: new RepairCoordinator(
                new PeerTable(), clock,
                new RepairOptions(TimeSpan.FromSeconds(5), InitialRequestJitter: TimeSpan.Zero)));
        receiver.ProgressChanged += p => Volatile.Write(ref verifiedChunks, p.CompletedChunks);

        using var cts = new CancellationTokenSource(OverallTimeout);
        var run = receiver.RunAsync(cts.Token);

        await injector.SendAsync(MessageCodec.Encode(new ManifestMessage(transfer.Signed)), cts.Token);
        await WaitUntilAsync(() => receiver.Manifest is not null, cts.Token);

        // File 0's carousel runs 0-4 and then moves on to file 1; 5-19 of file 0 are lost.
        for (int chunkIndex = 0; chunkIndex <= 4; chunkIndex++)
            await injector.SendAsync(MessageCodec.Encode(CarouselChunk(transfer, 0, chunkIndex, chunkSize)), cts.Token);
        await WaitUntilAsync(() => Volatile.Read(ref verifiedChunks) >= 5, cts.Token);

        // File 0 has now been quiet for a full threshold — but file 1's carousel is actively running throughout,
        // which under a single global timer kept file 0's valve shut and stranded its lost tail.
        clock.Advance(idleThreshold + TimeSpan.FromSeconds(1));
        for (int chunkIndex = 0; chunkIndex <= 4; chunkIndex++)
            await injector.SendAsync(MessageCodec.Encode(CarouselChunk(transfer, 1, chunkIndex, chunkSize)), cts.Token);
        await WaitUntilAsync(() => Volatile.Read(ref verifiedChunks) >= 10, cts.Token);

        await receiver.RequestRepairsAsync(cts.Token);

        await cts.CancelAsync();
        await Swallow(run);

        var file0Requested = observer.OutboundRequests
            .Where(r => r.FileIndex == 0).SelectMany(r => r.ChunkIndices).ToHashSet();

        Assert.True(file0Requested.Any(i => i > 4),
            "file 0's lost tail (above its watermark) was not requested — file 1's concurrent carousel held " +
            "file 0's valve shut");
    }

    [Fact]
    public async Task RepairEligibility_StartedFileWithAGapBelowItsWatermark_IsStillRequested()
    {
        // Positive control for the "must have started" rule, so it cannot pass by suppressing everything: file 0
        // has started, chunk 1 was lost, chunks 0 and 2 arrived — chunk 1 is below the watermark and must be
        // requested immediately, with no threshold wait at all.
        const int chunkSize = 1000;
        var fileBytes = new[] { RandomBytes(seed: 98, length: 20_000), RandomBytes(seed: 99, length: 8_000) };

        using var key = ManifestSigner.CreateSigningKey();
        var transfer = BuildMultiFileTransfer(key, fileBytes, chunkSize);

        var network = new InMemoryNetwork();
        var injector = network.CreateMulticastTransport(new Endpoint("sender", 1));
        var observer = new FileTrafficObserver(network.CreateMulticastTransport(new Endpoint("r", 1)));

        var clock = new FakeClock(DateTimeOffset.Parse("2026-07-25T00:00:00Z"));
        int verifiedChunks = 0;
        var receiver = new ReceiverSession(
            ReceiverId(1), TrustedStoreFor(key), observer, clock,
            new ReceiverSessionOptions("/root", CarouselIdleThreshold: TimeSpan.FromSeconds(30)),
            (_, length) => new MemoryFileSink((int)length),
            repairCoordinator: new RepairCoordinator(
                new PeerTable(), clock,
                new RepairOptions(TimeSpan.FromSeconds(5), InitialRequestJitter: TimeSpan.Zero)));
        receiver.ProgressChanged += p => Volatile.Write(ref verifiedChunks, p.CompletedChunks);

        using var cts = new CancellationTokenSource(OverallTimeout);
        var run = receiver.RunAsync(cts.Token);

        await injector.SendAsync(MessageCodec.Encode(new ManifestMessage(transfer.Signed)), cts.Token);
        await WaitUntilAsync(() => receiver.Manifest is not null, cts.Token);

        await injector.SendAsync(MessageCodec.Encode(CarouselChunk(transfer, 0, 0, chunkSize)), cts.Token);
        await injector.SendAsync(MessageCodec.Encode(CarouselChunk(transfer, 0, 2, chunkSize)), cts.Token); // 1 lost
        await WaitUntilAsync(() => Volatile.Read(ref verifiedChunks) >= 2, cts.Token);

        await receiver.RequestRepairsAsync(cts.Token); // no clock advance: the threshold must be irrelevant here

        await cts.CancelAsync();
        await Swallow(run);

        var file0Requests = observer.OutboundRequests.Where(r => r.FileIndex == 0).ToList();
        Assert.True(file0Requests.Count > 0, "a gap below the watermark must be requested without waiting");
        Assert.DoesNotContain(observer.OutboundRequests, r => r.FileIndex == 1); // still not started
    }

    [Fact]
    public async Task MultiFileTransfer_WithALargeFirstFile_CompletesWithoutRequestingUnsentFiles()
    {
        // End-to-end cover for the unstarted-file rule, with file 0 large enough that its carousel outlasts the
        // (shortened) idle threshold many times over — the condition the shipped 1 s threshold meets on any real
        // multi-file transfer.
        //
        // The premature-request assertion below is best-effort, NOT the primary guard, and was measured to pass
        // with the defect live: an in-memory carousel completes in single-digit milliseconds, so the window in
        // which the defect is visible is shorter than the repair loop's own polling cadence can reliably sample.
        // The deterministic version of this property is
        // RepairEligibility_FileWhoseCarouselHasNotStarted_IsNeverRequested_EvenPastTheIdleThreshold. This test
        // earns its place by proving the whole multi-file transfer still completes byte-identically, not by
        // catching the eligibility defect.
        const int chunkSize = 400;
        var fileBytes = new[] { RandomBytes(seed: 91, length: 600_000), RandomBytes(seed: 92, length: 8_000) };
        var idleThreshold = TimeSpan.FromMilliseconds(50);

        using var key = ManifestSigner.CreateSigningKey();
        var transfer = BuildMultiFileTransfer(key, fileBytes, chunkSize);

        var network = new InMemoryNetwork();
        var sender = NewSender(transfer, network.CreateMulticastTransport(new Endpoint("sender", 1)));

        var observer = new FileTrafficObserver(network.CreateMulticastTransport(new Endpoint("r", 1)));

        var sinks = new Dictionary<int, MemoryFileSink>();
        var receiver = new ReceiverSession(
            ReceiverId(1), TrustedStoreFor(key), observer, SystemClock.Instance,
            new ReceiverSessionOptions("/root", CarouselIdleThreshold: idleThreshold),
            (_, length) =>
            {
                var sink = new MemoryFileSink((int)length);
                sinks[sinks.Count] = sink;
                return sink;
            },
            repairCoordinator: new RepairCoordinator(
                new PeerTable(), SystemClock.Instance,
                new RepairOptions(TimeSpan.FromMilliseconds(500), InitialRequestJitter: TimeSpan.Zero)));

        await RunUntilCompleteAsync(sender, receiver, repairPeriod: TimeSpan.FromMilliseconds(20));

        Assert.True(receiver.IsComplete);
        Assert.Equal(fileBytes[0], sinks[0].ToArray());
        Assert.Equal(fileBytes[1], sinks[1].ToArray());

        // The property under test: nothing for file 1 may be asked for before file 1's carousel is observed.
        long firstFile1Chunk = observer.FirstInboundChunkTicks(fileIndex: 1);
        Assert.True(firstFile1Chunk > 0, "fixture must actually have delivered file 1 chunks");

        var prematureRequests = observer.OutboundRequests
            .Where(r => r.FileIndex == 1 && r.Ticks < firstFile1Chunk)
            .ToList();

        Assert.True(prematureRequests.Count == 0,
            $"{prematureRequests.Count} CHUNK_REQUEST(s) for file 1 were sent before file 1's carousel started " +
            $"(covering {prematureRequests.Sum(r => r.ChunkIndices.Length)} chunk indices the sender had not yet sent)");
    }

    [Fact]
    public async Task Transfer_EveryChunkOfTheOnlyFileLost_StillRecoversViaTheSessionLevelValve()
    {
        // The escape hatch the "file must have started" rule needs. If a file never starts at all, no per-file
        // signal can distinguish "carousel not reached yet" from "carousel ran and everything was lost" — and for
        // the last (or only) file there is no later file to prove the carousel moved past it either. The
        // session-level valve (no file's watermark has advanced for a whole threshold) is what covers it. This is
        // also the receiver-joins-late case for the single-file shape both shipped surfaces actually build.
        const int chunkSize = 1000;
        var originalBytes = RandomBytes(seed: 93, length: 10_000);
        int chunkCount = ChunkLayout.ComputeChunkCount(originalBytes.Length, chunkSize);

        using var key = ManifestSigner.CreateSigningKey();
        var transfer = BuildTransfer(key, "all-lost.bin", originalBytes, chunkSize);

        var network = new InMemoryNetwork();
        var sender = NewSender(transfer, network.CreateMulticastTransport(new Endpoint("sender", 1)));

        // Every carousel delivery of every chunk is dropped, so the receiver never observes the carousel at all.
        // Repair responses (ChunkResponse) pass through, so recovery depends entirely on the session-level valve
        // opening despite the watermark never having advanced even once.
        int droppedChunks = 0;
        var lossy = new FilteringMulticastTransport(
            network.CreateMulticastTransport(new Endpoint("r", 1)),
            message =>
            {
                if (message is ChunkDataMessage)
                {
                    droppedChunks++;
                    return false;
                }
                return true;
            });

        var (sink, sinkFactory) = MemorySinkFactory();
        var receiver = new ReceiverSession(
            ReceiverId(1), TrustedStoreFor(key), lossy, SystemClock.Instance,
            new ReceiverSessionOptions("/root", CarouselIdleThreshold: TimeSpan.FromMilliseconds(200)), sinkFactory,
            repairCoordinator: new RepairCoordinator(
                new PeerTable(), SystemClock.Instance,
                new RepairOptions(TimeSpan.FromMilliseconds(500), InitialRequestJitter: TimeSpan.Zero)));

        await RunUntilCompleteAsync(sender, receiver, repairPeriod: TimeSpan.FromMilliseconds(50));

        Assert.True(droppedChunks >= chunkCount, "fixture must have dropped the whole carousel");
        Assert.True(receiver.IsComplete, "session-level valve never opened for a file that never started");
        Assert.Equal(originalBytes, sink().ToArray());
    }

    [Fact]
    public async Task RepairEligibility_FileEntirelyLost_BecomesEligibleOnceALaterFileStarts()
    {
        // The exact (non-timer) half of the "not started" rule: because the carousel sends files strictly in
        // index order, a LATER file starting is proof this file's carousel already ran to completion — so if we
        // hold none of its chunks they were lost, not merely unsent, and it becomes eligible without waiting out
        // any threshold.
        //
        // Hand-fed on a FakeClock, like the other eligibility tests here. The earlier end-to-end version was a
        // real-clock liveness test on a 30 s budget and flaked at ~10% under full-suite parallel execution (2 in
        // 19 runs) while being 14/14 clean standalone — its sender, receiver and repair tasks were competing for
        // the thread pool with seven other test projects. A flaky liveness test in the exact area this change has
        // already been wrong in twice is worse than no test: it trains people to re-run instead of investigate,
        // which is how the original defects survived three review rounds. This version asserts the rule directly
        // and runs in milliseconds, immune to load by construction.
        //
        // Note also the threshold: it must exceed anything the test could wait out, so that the session-level
        // valve provably cannot contribute and only the "a later file started" inference can make file 0
        // eligible. An earlier version used 10 s and passed with this very rule deleted, because the session
        // valve simply fired 10 s late and rescued it inside the budget.
        const int chunkSize = 1000;
        var fileBytes = new[] { RandomBytes(seed: 94, length: 6_000), RandomBytes(seed: 95, length: 6_000) };

        using var key = ManifestSigner.CreateSigningKey();
        var transfer = BuildMultiFileTransfer(key, fileBytes, chunkSize);

        var network = new InMemoryNetwork();
        var injector = network.CreateMulticastTransport(new Endpoint("sender", 1));
        var observer = new FileTrafficObserver(network.CreateMulticastTransport(new Endpoint("r", 1)));

        var clock = new FakeClock(DateTimeOffset.Parse("2026-07-25T00:00:00Z"));
        int verifiedChunks = 0;
        var receiver = new ReceiverSession(
            ReceiverId(1), TrustedStoreFor(key), observer, clock,
            new ReceiverSessionOptions("/root", CarouselIdleThreshold: TimeSpan.FromHours(1)),
            (_, length) => new MemoryFileSink((int)length),
            repairCoordinator: new RepairCoordinator(
                new PeerTable(), clock,
                new RepairOptions(TimeSpan.FromSeconds(5), InitialRequestJitter: TimeSpan.Zero)));
        receiver.ProgressChanged += p => Volatile.Write(ref verifiedChunks, p.CompletedChunks);

        using var cts = new CancellationTokenSource(OverallTimeout);
        var run = receiver.RunAsync(cts.Token);

        await injector.SendAsync(MessageCodec.Encode(new ManifestMessage(transfer.Signed)), cts.Token);
        await WaitUntilAsync(() => receiver.Manifest is not null, cts.Token);

        // File 0's carousel ran and was lost in its entirety (nothing delivered for it at all); file 1's carousel
        // has since started. Only the ordering inference can tell those apart from "file 0 not sent yet".
        await injector.SendAsync(MessageCodec.Encode(CarouselChunk(transfer, 1, 0, chunkSize)), cts.Token);
        await WaitUntilAsync(() => Volatile.Read(ref verifiedChunks) >= 1, cts.Token);

        await receiver.RequestRepairsAsync(cts.Token);

        await cts.CancelAsync();
        await Swallow(run);

        var file0Requested = observer.OutboundRequests
            .Where(r => r.FileIndex == 0).SelectMany(r => r.ChunkIndices).ToHashSet();

        Assert.True(file0Requested.Count > 0,
            "file 0 was never requested: a later file starting is proof its carousel already ran, so its missing " +
            "chunks are lost rather than unsent and must be eligible without waiting out any threshold");
        Assert.Contains(0, file0Requested); // including index 0, which no watermark could ever have covered
    }

    [Fact]
    public async Task Transfer_MiddleChunkDropped_IsRepairedWithoutWaitingForTheIdleThreshold()
    {
        // The common case the watermark is designed for: a gap below the watermark is eligible immediately, so
        // recovery does not have to wait out CarouselIdleThreshold.
        var originalBytes = RandomBytes(seed: 72, length: 12_000);
        const int chunkSize = 1000;

        using var key = ManifestSigner.CreateSigningKey();
        var transfer = BuildTransfer(key, "mid-loss.bin", originalBytes, chunkSize);

        var network = new InMemoryNetwork();
        var sender = NewSender(transfer, network.CreateMulticastTransport(new Endpoint("sender", 1)));

        bool dropped = false;
        var lossy = new FilteringMulticastTransport(
            network.CreateMulticastTransport(new Endpoint("r", 1)),
            message =>
            {
                if (message is ChunkDataMessage { ChunkIndex: 2 } && !dropped)
                {
                    dropped = true;
                    return false;
                }
                return true;
            });

        var (sink, sinkFactory) = MemorySinkFactory();
        var receiver = new ReceiverSession(
            ReceiverId(1), TrustedStoreFor(key), lossy, SystemClock.Instance,
            new ReceiverSessionOptions("/root"), sinkFactory,
            repairCoordinator: new RepairCoordinator(
                new PeerTable(), SystemClock.Instance,
                new RepairOptions(TimeSpan.FromSeconds(1), InitialRequestJitter: TimeSpan.Zero)));

        await RunUntilCompleteAsync(sender, receiver, repairPeriod: TimeSpan.FromMilliseconds(50));

        Assert.True(dropped);
        Assert.True(receiver.IsComplete);
        Assert.Equal(originalBytes, sink().ToArray());
    }

    [Fact]
    public async Task BulkRepair_SplitsIntoSingleDatagramRequests_AndStillRecoversEveryChunk()
    {
        // End-to-end guard on the P0 cap. 280 contiguous chunks (more than the ~268 cap) are lost on their only
        // carousel delivery, so the receiver must ask for all of them at once. Before the cap that was one
        // ~1.1 KB-per-268-indices message concatenated into a single oversized request, fragmented
        // all-or-nothing; now it must arrive as several single-datagram requests, and every chunk must still be
        // recovered.
        const int chunkSize = 200;
        var originalBytes = RandomBytes(seed: 73, length: 60_000); // 300 chunks
        int chunkCount = ChunkLayout.ComputeChunkCount(originalBytes.Length, chunkSize);
        const int lostRunLength = 280;
        Assert.True(lostRunLength > RepairOptions.DefaultMaxChunksPerRequest, "fixture must exceed the cap");
        Assert.True(chunkCount > lostRunLength);

        using var key = ManifestSigner.CreateSigningKey();
        var transfer = BuildTransfer(key, "capped.bin", originalBytes, chunkSize);

        var network = new InMemoryNetwork();
        var sender = NewSender(transfer, network.CreateMulticastTransport(new Endpoint("sender", 1)));

        // Drop chunks 0..279 on their carousel delivery only (ChunkData); the repair re-sends come back as
        // ChunkResponse and pass through, so recovery depends entirely on the repair path.
        var lossy = new FilteringMulticastTransport(
            network.CreateMulticastTransport(new Endpoint("r", 1)),
            message => message is not ChunkDataMessage cd || cd.ChunkIndex >= lostRunLength);

        var recorded = new RecordedOutbound();
        var recording = new RecordingMulticastTransport(lossy, recorded);

        var (sink, sinkFactory) = MemorySinkFactory();
        var receiver = new ReceiverSession(
            ReceiverId(1), TrustedStoreFor(key), recording, SystemClock.Instance,
            new ReceiverSessionOptions("/root"), sinkFactory,
            repairCoordinator: new RepairCoordinator(
                new PeerTable(), SystemClock.Instance,
                new RepairOptions(TimeSpan.FromSeconds(1), InitialRequestJitter: TimeSpan.Zero)));

        await RunUntilCompleteAsync(sender, receiver, repairPeriod: TimeSpan.FromMilliseconds(50));

        Assert.True(receiver.IsComplete);
        Assert.Equal(originalBytes, sink().ToArray()); // all 280 lost chunks recovered

        // The receiver did ask in bulk (so this is not vacuous), and every request it sent fits one datagram.
        Assert.NotEmpty(recorded.ChunkRequests);
        Assert.Contains(recorded.ChunkRequests, r => r.ChunkIndices.Length > 1);
        Assert.All(recorded.ChunkRequests, r =>
            Assert.True(r.ChunkIndices.Length <= RepairOptions.DefaultMaxChunksPerRequest,
                $"a CHUNK_REQUEST carried {r.ChunkIndices.Length} indices, over the {RepairOptions.DefaultMaxChunksPerRequest} cap"));

        // Nothing this receiver sent needed fragmenting at all. At 300 chunks its PEER_HAVE bitmap and its
        // JOIN_REQUEST both fit one datagram, so any outbound PacketFragment could only have come from an
        // oversized CHUNK_REQUEST — which is exactly the failure mode the cap removes.
        Assert.DoesNotContain(recorded.Datagrams, d => d.Length >= 2 && d[1] == (byte)MessageType.PacketFragment);
    }

    // ---- fixture ----

    /// <summary>
    /// Runs one full in-memory transfer with inbound delivery paced one datagram at a time, advancing a
    /// <see cref="FakeClock"/> by <paramref name="clockStepPerPacket"/> before each one. That makes the
    /// PEER_HAVE interval's effect deterministic: step 0 means the interval never elapses, a step past
    /// <see cref="ReceiverSession.DefaultPeerHaveInterval"/> means it always does.
    /// </summary>
    private static async Task<(RecordedOutbound Recorded, int ChunkCount)> RunTransferAsync(
        int chunkSize, int length, TimeSpan clockStepPerPacket)
    {
        var originalBytes = RandomBytes(seed: 91, length);
        int chunkCount = ChunkLayout.ComputeChunkCount(length, chunkSize);

        using var key = ManifestSigner.CreateSigningKey();
        var transfer = BuildTransfer(key, "gossip.bin", originalBytes, chunkSize);

        var network = new InMemoryNetwork();
        var sender = NewSender(transfer, network.CreateMulticastTransport(new Endpoint("sender", 1)));

        var clock = new FakeClock(DateTimeOffset.Parse("2026-07-25T00:00:00Z"));
        var recorded = new RecordedOutbound();
        var paced = new PacedRecordingTransport(
            network.CreateMulticastTransport(new Endpoint("r", 1)), recorded, clock, clockStepPerPacket);

        var (sink, sinkFactory) = MemorySinkFactory();
        var receiver = new ReceiverSession(
            ReceiverId(1), TrustedStoreFor(key), paced, clock, new ReceiverSessionOptions("/root"), sinkFactory);

        await RunUntilCompleteAsync(sender, receiver, repairPeriod: null);

        Assert.True(receiver.IsComplete);
        Assert.Equal(originalBytes, sink().ToArray());
        return (recorded, chunkCount);
    }

    private sealed class RecordedOutbound
    {
        private readonly Lock _gate = new();
        private readonly List<byte[]> _datagrams = [];
        private readonly List<PeerHaveMessage> _peerHaves = [];
        private readonly List<ChunkRequestMessage> _chunkRequests = [];

        public IReadOnlyList<byte[]> Datagrams { get { lock (_gate) return [.. _datagrams]; } }

        public IReadOnlyList<PeerHaveMessage> PeerHaves { get { lock (_gate) return [.. _peerHaves]; } }

        public IReadOnlyList<ChunkRequestMessage> ChunkRequests { get { lock (_gate) return [.. _chunkRequests]; } }

        public void Record(byte[] datagram)
        {
            object? decoded;
            try { decoded = MessageCodec.Decode(datagram); }
            catch { decoded = null; }

            lock (_gate)
            {
                _datagrams.Add(datagram);
                if (decoded is PeerHaveMessage peerHave)
                    _peerHaves.Add(peerHave);
                else if (decoded is ChunkRequestMessage request)
                    _chunkRequests.Add(request);
            }
        }
    }

    /// <summary>
    /// Timestamps outbound CHUNK_REQUESTs and inbound CHUNK_DATA arrivals per file, so a test can assert
    /// ordering between them (e.g. "nothing was requested for file 1 before file 1's carousel started").
    /// </summary>
    private sealed class FileTrafficObserver(IMulticastTransport inner) : IMulticastTransport
    {
        private readonly Lock _gate = new();
        private readonly List<(int FileIndex, int[] ChunkIndices, long Ticks)> _outboundRequests = [];
        private readonly Dictionary<int, long> _firstInboundChunkTicks = [];

        public IReadOnlyList<(int FileIndex, int[] ChunkIndices, long Ticks)> OutboundRequests
        {
            get { lock (_gate) return [.. _outboundRequests]; }
        }

        public long FirstInboundChunkTicks(int fileIndex)
        {
            lock (_gate) return _firstInboundChunkTicks.GetValueOrDefault(fileIndex);
        }

        public ValueTask SendAsync(ReadOnlyMemory<byte> payload, CancellationToken cancellationToken = default)
        {
            object? decoded;
            try { decoded = MessageCodec.Decode(payload.Span); }
            catch { decoded = null; }

            if (decoded is ChunkRequestMessage request)
            {
                lock (_gate)
                    _outboundRequests.Add((request.FileIndex, request.ChunkIndices, DateTime.UtcNow.Ticks));
            }

            return inner.SendAsync(payload, cancellationToken);
        }

        public async IAsyncEnumerable<ReceivedPacket> ReceiveAsync(
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await foreach (var packet in inner.ReceiveAsync(cancellationToken))
            {
                object? decoded;
                try { decoded = MessageCodec.Decode(packet.Payload); }
                catch { decoded = null; }

                if (decoded is ChunkDataMessage chunkData)
                {
                    lock (_gate)
                    {
                        if (!_firstInboundChunkTicks.ContainsKey(chunkData.FileIndex))
                            _firstInboundChunkTicks[chunkData.FileIndex] = DateTime.UtcNow.Ticks;
                    }
                }

                yield return packet;
            }
        }

        public ValueTask DisposeAsync() => inner.DisposeAsync();
    }

    /// <summary>Records everything the receiver sends, passing it through untouched.</summary>
    private sealed class RecordingMulticastTransport(IMulticastTransport inner, RecordedOutbound recorded) : IMulticastTransport
    {
        public ValueTask SendAsync(ReadOnlyMemory<byte> payload, CancellationToken cancellationToken = default)
        {
            recorded.Record(payload.ToArray());
            return inner.SendAsync(payload, cancellationToken);
        }

        public IAsyncEnumerable<ReceivedPacket> ReceiveAsync(CancellationToken cancellationToken = default) =>
            inner.ReceiveAsync(cancellationToken);

        public ValueTask DisposeAsync() => inner.DisposeAsync();
    }

    /// <summary>
    /// Records outbound traffic and advances an injected <see cref="FakeClock"/> by a fixed step before handing
    /// over each inbound datagram — so a test can make the receiver's clock move a known amount per packet
    /// without racing real time.
    /// </summary>
    private sealed class PacedRecordingTransport(
        IMulticastTransport inner, RecordedOutbound recorded, FakeClock clock, TimeSpan stepPerPacket) : IMulticastTransport
    {
        public ValueTask SendAsync(ReadOnlyMemory<byte> payload, CancellationToken cancellationToken = default)
        {
            recorded.Record(payload.ToArray());
            return inner.SendAsync(payload, cancellationToken);
        }

        public async IAsyncEnumerable<ReceivedPacket> ReceiveAsync(
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await foreach (var packet in inner.ReceiveAsync(cancellationToken))
            {
                if (stepPerPacket > TimeSpan.Zero)
                    clock.Advance(stepPerPacket);
                yield return packet;
            }
        }

        public ValueTask DisposeAsync() => inner.DisposeAsync();
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
            sessionId, "m7-test", DateTimeOffset.UtcNow, EncryptionKeys.ExportPublicKey(senderEncryptionKey),
            [new ManifestFileEntry(relativePath, bytes.Length, chunkSize, chunkCount, tree.Root)]);

        return new Transfer(
            ManifestSigner.Sign(manifest, key),
            new Dictionary<int, IFileSource> { [0] = source },
            new Dictionary<int, MerkleTree> { [0] = tree },
            senderEncryptionKey,
            contentKey);
    }

    /// <summary>Same as <see cref="BuildTransfer"/> but with several files, so per-file behavior can be exercised.</summary>
    private static Transfer BuildMultiFileTransfer(NSec.Cryptography.Key key, byte[][] files, int chunkSize)
    {
        var sessionId = new byte[16];
        var contentKey = ContentKey.Generate();
        var senderEncryptionKey = EncryptionKeys.Create();

        var sources = new Dictionary<int, IFileSource>();
        var trees = new Dictionary<int, MerkleTree>();
        var entries = new List<ManifestFileEntry>();
        for (int fileIndex = 0; fileIndex < files.Length; fileIndex++)
        {
            var source = new MemoryFileSource(files[fileIndex]);
            var hashes = EncryptedChunkHasher
                .ComputeAsync(source, chunkSize, sessionId, fileIndex, contentKey).GetAwaiter().GetResult();
            var tree = MerkleTree.Build(hashes);
            sources[fileIndex] = source;
            trees[fileIndex] = tree;
            entries.Add(new ManifestFileEntry(
                $"f{fileIndex}.bin", files[fileIndex].Length, chunkSize,
                ChunkLayout.ComputeChunkCount(files[fileIndex].Length, chunkSize), tree.Root));
        }

        var manifest = new TransferManifest(
            sessionId, "m7-multifile", DateTimeOffset.UtcNow, EncryptionKeys.ExportPublicKey(senderEncryptionKey), entries);

        return new Transfer(ManifestSigner.Sign(manifest, key), sources, trees, senderEncryptionKey, contentKey);
    }

    // Matches EndToEndTransferTests: a large sender-side datagram budget keeps each chunk in one unfragmented
    // ChunkDataMessage, so the FilteringMulticastTransport decorators here can drop whole chunks by index.
    private const int SingleDatagramBudget = 65_000;

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

    private static async Task RunUntilCompleteAsync(
        SenderSession sender, ReceiverSession receiver, TimeSpan? repairPeriod)
    {
        using var cts = new CancellationTokenSource(OverallTimeout);
        var senderTask = sender.RunAsync(cts.Token);
        var receiverTask = receiver.RunAsync(cts.Token);
        var repairTask = repairPeriod is { } period
            ? RunRepairLoopAsync(receiver, period, cts.Token)
            : Task.CompletedTask;

        while (!receiver.IsComplete && !cts.IsCancellationRequested)
            await Task.Delay(20, CancellationToken.None);

        await cts.CancelAsync();
        await Swallow(senderTask);
        await Swallow(receiverTask);
        await Swallow(repairTask);

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

    /// <summary>A genuine carousel delivery (CHUNK_DATA) for one chunk — real ciphertext and Merkle proof.</summary>
    private static ChunkDataMessage CarouselChunk(Transfer transfer, int fileIndex, int chunkIndex, int chunkSize)
    {
        var (ciphertext, proof) = EncryptChunk(transfer, fileIndex, chunkIndex, chunkSize);
        return new ChunkDataMessage(transfer.Signed.Manifest.SessionId, fileIndex, chunkIndex, ciphertext, proof);
    }

    /// <summary>
    /// A genuine repair reply (CHUNK_RESPONSE) for one chunk. Byte-for-byte as valid as a carousel delivery —
    /// the difference that matters is that it is evidence about somebody's <i>repair request</i>, not about the
    /// sender's carousel position, which is exactly what <c>ObserveChunkActivity</c>'s <c>fromCarousel</c>
    /// distinction encodes.
    /// </summary>
    private static ChunkResponseMessage RepairChunk(Transfer transfer, int fileIndex, int chunkIndex, int chunkSize)
    {
        var (ciphertext, proof) = EncryptChunk(transfer, fileIndex, chunkIndex, chunkSize);
        return new ChunkResponseMessage(
            transfer.Signed.Manifest.SessionId, new byte[16], fileIndex, chunkIndex, ciphertext, proof);
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
