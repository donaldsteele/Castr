using Castr.Core.Chunking;
using Castr.Core.Manifest;
using Castr.Core.Protocol;
using Castr.Core.Security;
using Castr.Core.Time;
using Castr.Core.Transport;
using Castr.Core.Transport.InMemory;
using Castr.Core.Trust;

namespace Castr.Core.Tests.Protocol;

/// <summary>
/// Covers the two M2 additions to the Castr.Core contract: the interactive <see cref="ITrustPrompt"/>
/// callback wired into <see cref="ReceiverSession"/>'s TOFU flow, and the <see cref="TransferProgress"/>
/// observability event exposed on both session types. All wired over <see cref="InMemoryNetwork"/>.
/// </summary>
public class TrustPromptAndProgressTests
{
    private static readonly TimeSpan OverallTimeout = TimeSpan.FromSeconds(15);

    // ---- Trust prompt ----

    [Fact]
    public async Task PromptAccepted_UnknownSender_TransferCompletes_AndSenderIsPersistedAsTrusted()
    {
        var originalBytes = RandomBytes(seed: 11, length: 10_000);
        using var key = ManifestSigner.CreateSigningKey();
        var transfer = BuildTransfer(key, "prompted.bin", originalBytes, chunkSize: 1024);

        var network = new InMemoryNetwork();
        var sender = NewSender(transfer, network.CreateMulticastTransport(new Endpoint("sender", 1)));

        var trustStore = new InMemoryTrustStore(); // sender is UNKNOWN
        var prompt = new StubTrustPrompt(answer: true);
        var (sink, sinkFactory) = MemorySinkFactory();
        var receiver = new ReceiverSession(
            ReceiverId(1), trustStore, network.CreateMulticastTransport(new Endpoint("r", 1)),
            new FakeClock(DateTimeOffset.UtcNow),
            new ReceiverSessionOptions("/root", UnknownSenderPolicy.Prompt, IsInteractive: true),
            sinkFactory, trustPrompt: prompt);

        await RunUntilCompleteAsync(sender, receiver);

        Assert.True(receiver.IsComplete);
        Assert.Equal(originalBytes, sink().ToArray());

        // Prompt was consulted with the right context, and a Trusted entry was persisted (TOFU).
        Assert.Equal(1, prompt.CallCount);
        Assert.Equal("test-transfer", prompt.LastContext!.TransferName);
        Assert.Equal(1, prompt.LastContext.FileCount);
        Assert.Equal(originalBytes.Length, prompt.LastContext.TotalBytes);

        var persisted = trustStore.Find(transfer.Signed.SenderId);
        Assert.NotNull(persisted);
        Assert.Equal(TrustStatus.Trusted, persisted!.Status);
    }

    [Fact]
    public async Task PromptRejected_UnknownSender_TransferDenied_AndNothingPersisted()
    {
        var originalBytes = RandomBytes(seed: 12, length: 6000);
        using var key = ManifestSigner.CreateSigningKey();
        var transfer = BuildTransfer(key, "rejected.bin", originalBytes, chunkSize: 1024);

        var network = new InMemoryNetwork();
        var sender = NewSender(transfer, network.CreateMulticastTransport(new Endpoint("sender", 1)));

        var trustStore = new InMemoryTrustStore();
        var prompt = new StubTrustPrompt(answer: false);
        var (_, sinkFactory) = MemorySinkFactory();
        var receiver = new ReceiverSession(
            ReceiverId(1), trustStore, network.CreateMulticastTransport(new Endpoint("r", 1)),
            new FakeClock(DateTimeOffset.UtcNow),
            new ReceiverSessionOptions("/root", UnknownSenderPolicy.Prompt, IsInteractive: true),
            sinkFactory, trustPrompt: prompt);

        TrustDecision? denial = null;
        receiver.SenderTrustDenied += (decision, _) => denial = decision;

        await RunUntilDeniedAsync(sender, receiver);

        Assert.True(prompt.CallCount >= 1);
        Assert.False(receiver.IsComplete);
        Assert.Null(receiver.Manifest);
        Assert.NotNull(denial);
        Assert.Equal(TrustOutcome.PromptRequired, denial!.Outcome);
        Assert.Null(trustStore.Find(transfer.Signed.SenderId)); // nothing persisted on rejection
    }

    [Fact]
    public async Task NoPromptSupplied_UnknownSender_DeniedExactlyAsBefore()
    {
        var originalBytes = RandomBytes(seed: 13, length: 6000);
        using var key = ManifestSigner.CreateSigningKey();
        var transfer = BuildTransfer(key, "noprompt.bin", originalBytes, chunkSize: 1024);

        var network = new InMemoryNetwork();
        var sender = NewSender(transfer, network.CreateMulticastTransport(new Endpoint("sender", 1)));

        var trustStore = new InMemoryTrustStore();
        var (_, sinkFactory) = MemorySinkFactory();
        // Interactive Prompt policy but NO ITrustPrompt supplied — must fail safe exactly as today.
        var receiver = new ReceiverSession(
            ReceiverId(1), trustStore, network.CreateMulticastTransport(new Endpoint("r", 1)),
            new FakeClock(DateTimeOffset.UtcNow),
            new ReceiverSessionOptions("/root", UnknownSenderPolicy.Prompt, IsInteractive: true),
            sinkFactory /* no trustPrompt */);

        TrustDecision? denial = null;
        receiver.SenderTrustDenied += (decision, _) => denial = decision;

        await RunUntilDeniedAsync(sender, receiver);

        Assert.False(receiver.IsComplete);
        Assert.Null(receiver.Manifest);
        Assert.NotNull(denial);
        Assert.Equal(TrustOutcome.PromptRequired, denial!.Outcome);
        Assert.Null(trustStore.Find(transfer.Signed.SenderId));
    }

    [Fact]
    public async Task BlockedSender_EvenWithAcceptingPrompt_IsDenied_AndPromptNeverConsulted()
    {
        // M1.5 QA finding, now a permanent regression test: an explicitly BLOCKED sender must be denied even
        // under an interactive Prompt policy, and the prompt must never even be consulted — "blocked" is a
        // hard short-circuit that a human (or a buggy always-yes prompt) cannot override.
        var originalBytes = RandomBytes(seed: 31, length: 6000);
        using var key = ManifestSigner.CreateSigningKey();
        var transfer = BuildTransfer(key, "blocked.bin", originalBytes, chunkSize: 1024);

        var network = new InMemoryNetwork();
        var sender = NewSender(transfer, network.CreateMulticastTransport(new Endpoint("sender", 1)));

        var trustStore = new InMemoryTrustStore();
        trustStore.Upsert(new TrustEntry(transfer.Signed.SenderId, "known-bad", TrustStatus.Blocked, DateTimeOffset.UnixEpoch, TrustEntrySource.Manual));
        var prompt = new StubTrustPrompt(answer: true); // would say YES if ever asked

        var (_, sinkFactory) = MemorySinkFactory();
        var receiver = new ReceiverSession(
            ReceiverId(1), trustStore, network.CreateMulticastTransport(new Endpoint("r", 1)),
            new FakeClock(DateTimeOffset.UtcNow),
            new ReceiverSessionOptions("/root", UnknownSenderPolicy.Prompt, IsInteractive: true),
            sinkFactory, trustPrompt: prompt);

        TrustDecision? denial = null;
        receiver.SenderTrustDenied += (decision, _) => denial = decision;

        await RunUntilDeniedAsync(sender, receiver);

        Assert.Equal(0, prompt.CallCount); // blocked short-circuits BEFORE any prompt
        Assert.False(receiver.IsComplete);
        Assert.Null(receiver.Manifest);
        Assert.NotNull(denial);
        Assert.Equal(TrustOutcome.Blocked, denial!.Outcome);
        Assert.Equal(TrustStatus.Blocked, trustStore.Find(transfer.Signed.SenderId)!.Status); // unchanged
    }

    [Fact]
    public async Task PromptThatThrows_PropagatesException_PersistsNothing_DoesNotComplete()
    {
        // M2 Core contract (m2-ui-summary), now a permanent regression test: if ITrustPrompt throws,
        // ReceiverSession.RunAsync PROPAGATES rather than silently proceeding — every UI ITrustPrompt is
        // required to catch internally and resolve to false. This locks the contract so a future change can't
        // quietly turn a throwing/cancelled prompt into an accidental "trust."
        var originalBytes = RandomBytes(seed: 32, length: 4000);
        using var key = ManifestSigner.CreateSigningKey();
        var transfer = BuildTransfer(key, "boom.bin", originalBytes, chunkSize: 1024);

        var network = new InMemoryNetwork();
        var sender = NewSender(transfer, network.CreateMulticastTransport(new Endpoint("sender", 1)));

        var trustStore = new InMemoryTrustStore(); // unknown sender -> PromptRequired
        var (_, sinkFactory) = MemorySinkFactory();
        var receiver = new ReceiverSession(
            ReceiverId(1), trustStore, network.CreateMulticastTransport(new Endpoint("r", 1)),
            new FakeClock(DateTimeOffset.UtcNow),
            new ReceiverSessionOptions("/root", UnknownSenderPolicy.Prompt, IsInteractive: true),
            sinkFactory, trustPrompt: new ThrowingTrustPrompt());

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var senderTask = sender.RunAsync(cts.Token);

        await Assert.ThrowsAsync<PromptBoomException>(() => receiver.RunAsync(cts.Token));

        await cts.CancelAsync();
        await SwallowCancellation(senderTask);

        Assert.False(receiver.IsComplete);
        Assert.Null(receiver.Manifest);
        Assert.Null(trustStore.Find(transfer.Signed.SenderId)); // nothing persisted on a throwing prompt
    }

    // ---- Progress observability ----

    [Fact]
    public async Task ReceiverProgress_ReportsStartToCompletion_WithAccurateFinalCounts()
    {
        var originalBytes = RandomBytes(seed: 21, length: 20_000);
        const int chunkSize = 2048;
        using var key = ManifestSigner.CreateSigningKey();
        var transfer = BuildTransfer(key, "progress.bin", originalBytes, chunkSize);
        int expectedChunks = ChunkLayout.ComputeChunkCount(originalBytes.Length, chunkSize);

        var network = new InMemoryNetwork();
        var sender = NewSender(transfer, network.CreateMulticastTransport(new Endpoint("sender", 1)));

        var (sink, sinkFactory) = MemorySinkFactory();
        var receiver = new ReceiverSession(
            ReceiverId(1), TrustedStoreFor(key), network.CreateMulticastTransport(new Endpoint("r", 1)),
            new FakeClock(DateTimeOffset.UtcNow), new ReceiverSessionOptions("/root"), sinkFactory);

        var snapshots = new List<TransferProgress>();
        receiver.ProgressChanged += p => { lock (snapshots) snapshots.Add(p); };

        await RunUntilCompleteAsync(sender, receiver);

        List<TransferProgress> captured;
        lock (snapshots) captured = [.. snapshots];

        Assert.All(captured, p => Assert.Equal(TransferRole.Receiver, p.Role));
        Assert.Contains(captured, p => p.Phase == TransferPhase.Starting);
        // Chunks may verify while still AwaitingKey (fast local ordering) or during Transferring — either way
        // we must observe intermediate progress advancing before completion.
        Assert.Contains(captured, p => p.CompletedChunks > 0 && p.CompletedChunks < p.TotalChunks);

        var final = captured.Last();
        Assert.Equal(TransferPhase.Completed, final.Phase);
        Assert.True(final.IsComplete);
        Assert.Equal(expectedChunks, final.TotalChunks);
        Assert.Equal(expectedChunks, final.CompletedChunks);
        Assert.Equal(0, final.PendingChunks);
        Assert.Equal(originalBytes.Length, final.TotalBytes);
        Assert.Equal(originalBytes.Length, final.CompletedBytes);
        Assert.Equal(1.0, final.FractionComplete);

        // Monotonic non-decreasing completed-chunk count across the run.
        for (int i = 1; i < captured.Count; i++)
            Assert.True(captured[i].CompletedChunks >= captured[i - 1].CompletedChunks);
    }

    [Fact]
    public async Task SenderProgress_ReportsCarouselAndGrant_WithAccurateFinalCounts()
    {
        var originalBytes = RandomBytes(seed: 22, length: 16_000);
        const int chunkSize = 2048;
        using var key = ManifestSigner.CreateSigningKey();
        var transfer = BuildTransfer(key, "sendprogress.bin", originalBytes, chunkSize);
        int expectedChunks = ChunkLayout.ComputeChunkCount(originalBytes.Length, chunkSize);

        var network = new InMemoryNetwork();
        var sender = NewSender(transfer, network.CreateMulticastTransport(new Endpoint("sender", 1)));

        var snapshots = new List<TransferProgress>();
        sender.ProgressChanged += p => { lock (snapshots) snapshots.Add(p); };

        var (sink, sinkFactory) = MemorySinkFactory();
        var receiver = new ReceiverSession(
            ReceiverId(1), TrustedStoreFor(key), network.CreateMulticastTransport(new Endpoint("r", 1)),
            new FakeClock(DateTimeOffset.UtcNow), new ReceiverSessionOptions("/root"), sinkFactory);

        await RunUntilCompleteAsync(sender, receiver);

        List<TransferProgress> captured;
        lock (snapshots) captured = [.. snapshots];

        Assert.All(captured, p => Assert.Equal(TransferRole.Sender, p.Role));
        Assert.Contains(captured, p => p.Phase == TransferPhase.Starting);
        Assert.Contains(captured, p => p.Phase == TransferPhase.Transferring);
        Assert.Contains(captured, p => p.Phase == TransferPhase.Serving);

        // The whole carousel was reported and at least one receiver was granted the key.
        var carouselDone = captured.First(p => p.Phase == TransferPhase.Serving);
        Assert.Equal(expectedChunks, carouselDone.TotalChunks);
        Assert.Equal(expectedChunks, carouselDone.CompletedChunks);
        Assert.Equal(originalBytes.Length, carouselDone.CompletedBytes);
        Assert.Contains(captured, p => p.PeerCount >= 1);
    }

    // ---- helpers ----

    private sealed class StubTrustPrompt(bool answer) : ITrustPrompt
    {
        public int CallCount { get; private set; }
        public TrustPromptContext? LastContext { get; private set; }

        public Task<bool> RequestTrustAsync(TrustPromptContext context, CancellationToken cancellationToken)
        {
            CallCount++;
            LastContext = context;
            return Task.FromResult(answer);
        }
    }

    private sealed class ThrowingTrustPrompt : ITrustPrompt
    {
        public Task<bool> RequestTrustAsync(TrustPromptContext context, CancellationToken cancellationToken) =>
            Task.FromException<bool>(new PromptBoomException());
    }

    private sealed class PromptBoomException : Exception;

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

    private static SenderSession NewSender(Transfer t, IMulticastTransport transport) =>
        new(t.Signed, t.Sources, t.Trees, transport, t.SenderEncryptionKey, t.ContentKey);

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

    private static async Task RunUntilCompleteAsync(SenderSession sender, ReceiverSession receiver)
    {
        using var cts = new CancellationTokenSource(OverallTimeout);
        var senderTask = sender.RunAsync(cts.Token);
        var receiverTask = receiver.RunAsync(cts.Token);

        while (!receiver.IsComplete && !cts.IsCancellationRequested)
            await Task.Delay(20, CancellationToken.None);

        await cts.CancelAsync();
        await SwallowCancellation(senderTask);
        await SwallowCancellation(receiverTask);

        Assert.True(receiver.IsComplete, "Transfer did not complete within the test timeout.");
    }

    private static async Task RunUntilDeniedAsync(SenderSession sender, ReceiverSession receiver)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        var senderTask = sender.RunAsync(cts.Token);
        try { await receiver.RunAsync(cts.Token); } catch (OperationCanceledException) { }
        await cts.CancelAsync();
        await SwallowCancellation(senderTask);
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
}
