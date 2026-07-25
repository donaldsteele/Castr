using Castr.Core.Chunking;
using Castr.Core.Manifest;
using Castr.Core.Protocol;
using Castr.Core.Security;
using Castr.Core.Transport;
using Castr.Core.Transport.InMemory;

namespace Castr.Core.Tests.Protocol;

/// <summary>
/// Targeted coverage for the M6 send-path pipelining change: <see cref="SenderSession"/>'s chunk carousel
/// (and chunk-repair batches) now send up to <c>sendWindowSize</c> chunks concurrently instead of one
/// sequential <c>await</c> per wire packet — see the M6 write-up in wiki/synthesis/roadmap.md for the
/// benchmarked rationale. These tests don't re-prove correctness under loss/reorder/duplication (that's
/// EndToEndTransferTests and the real-socket ChaosTransport integration tests, all still passing unmodified
/// against this change); they specifically prove the concurrency behavior itself: sends really do overlap up
/// to the configured bound, and cancelling mid-window actually stops in-flight work rather than hanging.
/// </summary>
public class SenderSessionPipeliningTests
{
    [Fact]
    public async Task ChunkCarousel_SendsConcurrently_UpToConfiguredWindowBound()
    {
        const int windowSize = 4;
        const int chunkCount = 40;
        var originalBytes = new byte[chunkCount * 100];
        new Random(1).NextBytes(originalBytes);

        var transfer = BuildTransfer(originalBytes, chunkSize: 100);
        var network = new InMemoryNetwork();
        var probe = new ConcurrencyProbeTransport(
            network.CreateMulticastTransport(new Endpoint("sender", 1)),
            perSendDelay: TimeSpan.FromMilliseconds(30));

        var sender = new SenderSession(
            transfer.Signed, transfer.Sources, transfer.Trees, probe,
            transfer.SenderEncryptionKey, transfer.ContentKey,
            maxDatagramPayloadBytes: 65_000, sendWindowSize: windowSize);

        await RunUntilCarouselCompleteAsync(sender, TimeSpan.FromSeconds(20));

        // Sends genuinely overlapped (not the old fully-sequential one-at-a-time behavior)...
        Assert.True(probe.PeakConcurrency > 1, $"Expected overlapping sends, observed peak concurrency {probe.PeakConcurrency}.");
        // ...but never exceeded the configured window (the whole point of a *bounded* pipeline).
        Assert.True(probe.PeakConcurrency <= windowSize, $"Peak concurrency {probe.PeakConcurrency} exceeded the configured window of {windowSize}.");
    }

    [Fact]
    public async Task ChunkCarousel_Sequential_WindowSizeOne_NeverOverlaps()
    {
        // Sanity check for the probe itself and for windowSize=1 behaving like the old sequential code:
        // a window of exactly 1 must never show concurrent sends.
        const int chunkCount = 10;
        var originalBytes = new byte[chunkCount * 100];
        new Random(2).NextBytes(originalBytes);

        var transfer = BuildTransfer(originalBytes, chunkSize: 100);
        var network = new InMemoryNetwork();
        var probe = new ConcurrencyProbeTransport(
            network.CreateMulticastTransport(new Endpoint("sender", 1)),
            perSendDelay: TimeSpan.FromMilliseconds(10));

        var sender = new SenderSession(
            transfer.Signed, transfer.Sources, transfer.Trees, probe,
            transfer.SenderEncryptionKey, transfer.ContentKey,
            maxDatagramPayloadBytes: 65_000, sendWindowSize: 1);

        await RunUntilCarouselCompleteAsync(sender, TimeSpan.FromSeconds(20));

        Assert.Equal(1, probe.PeakConcurrency);
    }

    [Fact]
    public async Task Cancellation_MidWindow_StopsPromptly_DoesNotHang()
    {
        // Every send blocks for a while unless cancelled (ANNOUNCE and MANIFEST each take one full delay
        // sequentially before the carousel even starts, so this is long enough to observe the carousel's
        // window actually fill, but short enough to keep the test fast). If cancellation of an in-flight
        // windowed send were broken (e.g. a fire-and-forget pattern that doesn't propagate the token),
        // RunAsync would hang until every in-flight delay elapsed on its own instead of unwinding promptly.
        var delay = TimeSpan.FromMilliseconds(300);
        var originalBytes = new byte[2000];
        new Random(3).NextBytes(originalBytes);
        var transfer = BuildTransfer(originalBytes, chunkSize: 100);

        var network = new InMemoryNetwork();
        var probe = new ConcurrencyProbeTransport(
            network.CreateMulticastTransport(new Endpoint("sender", 1)),
            perSendDelay: delay);

        var sender = new SenderSession(
            transfer.Signed, transfer.Sources, transfer.Trees, probe,
            transfer.SenderEncryptionKey, transfer.ContentKey,
            maxDatagramPayloadBytes: 65_000, sendWindowSize: 4);

        using var cts = new CancellationTokenSource();
        var runTask = sender.RunAsync(cts.Token);

        // Give the window a moment to actually fill with in-flight carousel sends before cancelling (past
        // ANNOUNCE + MANIFEST, which are sent sequentially first).
        await WaitUntilAsync(() => probe.CurrentInFlight >= 4, TimeSpan.FromSeconds(10));
        cts.Cancel();

        // Generous relative to perSendDelay, but far less than "every in-flight send ran to completion on its
        // own timer" would take if cancellation leaked through the window (which, at 40 remaining chunks,
        // would be seconds even at this short delay).
        var completed = await Task.WhenAny(runTask, Task.Delay(TimeSpan.FromSeconds(3)));
        Assert.Same(runTask, completed); // did not time out — cancellation actually unblocked the in-flight window

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => runTask);
        Assert.Equal(0, probe.CurrentInFlight); // no leaked in-flight sends after unwind
    }

    [Fact]
    public void Constructor_RejectsNonPositiveSendWindowSize()
    {
        var originalBytes = new byte[500];
        var transfer = BuildTransfer(originalBytes, chunkSize: 100);
        var network = new InMemoryNetwork();
        var transport = network.CreateMulticastTransport(new Endpoint("sender", 1));

        Assert.Throws<ArgumentOutOfRangeException>(() => new SenderSession(
            transfer.Signed, transfer.Sources, transfer.Trees, transport,
            transfer.SenderEncryptionKey, transfer.ContentKey, sendWindowSize: 0));
    }

    // ---- helpers ----

    /// <summary>
    /// Runs <paramref name="sender"/> until its chunk carousel finishes (observed via <see cref="TransferPhase.Serving"/>
    /// on <see cref="SenderSession.ProgressChanged"/>), then cancels and swallows the resulting
    /// <see cref="OperationCanceledException"/>. Needed because <see cref="SenderSession.RunAsync"/> also runs
    /// an unbounded repair/JOIN_REQUEST listener loop (<c>HandleIncomingAsync</c>) that only ever exits via
    /// cancellation — awaiting <c>RunAsync</c> directly would otherwise hang until <paramref name="timeout"/>
    /// elapsed even though the carousel itself (what these tests care about) finished immediately.
    /// </summary>
    private static async Task RunUntilCarouselCompleteAsync(SenderSession sender, TimeSpan timeout)
    {
        var carouselComplete = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        sender.ProgressChanged += progress =>
        {
            if (progress.Phase == TransferPhase.Serving)
                carouselComplete.TrySetResult();
        };

        using var cts = new CancellationTokenSource(timeout);
        var runTask = sender.RunAsync(cts.Token);

        var completed = await Task.WhenAny(carouselComplete.Task, runTask);
        if (completed == runTask)
            await runTask; // surfaces whatever RunAsync faulted with (e.g. a real bug) instead of masking it
        Assert.Same(carouselComplete.Task, completed); // carousel finished, RunAsync itself did not

        await cts.CancelAsync();
        try { await runTask; }
        catch (OperationCanceledException) { }
    }

    private static async Task WaitUntilAsync(Func<bool> condition, TimeSpan timeout)
    {
        using var cts = new CancellationTokenSource(timeout);
        while (!condition())
        {
            if (cts.IsCancellationRequested)
                Assert.Fail("Condition was not met within the timeout.");
            await Task.Delay(5, CancellationToken.None);
        }
    }

    private sealed record Transfer(
        SignedManifest Signed,
        Dictionary<int, IFileSource> Sources,
        Dictionary<int, MerkleTree> Trees,
        NSec.Cryptography.Key SenderEncryptionKey,
        ContentKey ContentKey);

    private static Transfer BuildTransfer(byte[] bytes, int chunkSize)
    {
        using var key = ManifestSigner.CreateSigningKey();
        var sessionId = new byte[16];
        var source = new MemoryFileSource(bytes);
        var contentKey = ContentKey.Generate();
        var senderEncryptionKey = EncryptionKeys.Create();

        var hashes = EncryptedChunkHasher.ComputeAsync(source, chunkSize, sessionId, fileIndex: 0, contentKey).GetAwaiter().GetResult();
        var tree = MerkleTree.Build(hashes);
        int chunkCount = ChunkLayout.ComputeChunkCount(bytes.Length, chunkSize);

        var manifest = new TransferManifest(
            sessionId, "pipelining-test-transfer", DateTimeOffset.UtcNow, EncryptionKeys.ExportPublicKey(senderEncryptionKey),
            [new ManifestFileEntry("payload.bin", bytes.Length, chunkSize, chunkCount, tree.Root)]);
        var signed = ManifestSigner.Sign(manifest, key);

        return new Transfer(
            signed,
            new Dictionary<int, IFileSource> { [0] = source },
            new Dictionary<int, MerkleTree> { [0] = tree },
            senderEncryptionKey,
            contentKey);
    }

    /// <summary>
    /// Wraps a transport so every <see cref="SendAsync"/> call is delayed by a fixed amount and tracked, so
    /// tests can observe how many sends were actually in flight at once (and that cancelling the caller's
    /// token during that delay unblocks it immediately rather than hanging for the full delay).
    /// </summary>
    private sealed class ConcurrencyProbeTransport(IMulticastTransport inner, TimeSpan perSendDelay) : IMulticastTransport
    {
        private readonly Lock _gate = new();
        private int _current;

        public int PeakConcurrency { get; private set; }
        public int CurrentInFlight { get { lock (_gate) return _current; } }

        public async ValueTask SendAsync(ReadOnlyMemory<byte> payload, CancellationToken cancellationToken = default)
        {
            lock (_gate)
            {
                _current++;
                if (_current > PeakConcurrency)
                    PeakConcurrency = _current;
            }
            try
            {
                await Task.Delay(perSendDelay, cancellationToken).ConfigureAwait(false);
                await inner.SendAsync(payload, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                lock (_gate) { _current--; }
            }
        }

        public IAsyncEnumerable<ReceivedPacket> ReceiveAsync(CancellationToken cancellationToken = default) =>
            inner.ReceiveAsync(cancellationToken);

        public ValueTask DisposeAsync() => inner.DisposeAsync();
    }
}
