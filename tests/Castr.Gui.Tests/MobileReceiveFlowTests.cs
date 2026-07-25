using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using NSec.Cryptography;
using Castr.Core.Chunking;
using Castr.Core.Discovery;
using Castr.Core.Discovery.InMemory;
using Castr.Core.Manifest;
using Castr.Core.Protocol;
using Castr.Core.Security;
using Castr.Core.Swarm;
using Castr.Core.Time;
using Castr.Core.Transport;
using Castr.Core.Transport.InMemory;
using Castr.Core.Trust;
using Castr.Gui.ViewModels;

namespace Castr.Gui.Tests;

/// <summary>
/// Drives the real <see cref="MobileReceiveViewModel"/> end-to-end over the in-memory discovery + in-memory
/// stream transport — the same view-model the iOS/Android heads bind to, minus native mDNS and real sockets.
/// Proves the mobile flow: browse discovers the advertised peer, selecting it and pulling runs a real
/// <see cref="SwarmPullSession"/> that verifies + decrypts + writes the file byte-identically; that an
/// untrusted sender is surfaced as a rejection; and that a partial pull resumes to completion from a second peer.
/// </summary>
public class MobileReceiveFlowTests
{
    private const string SenderHost = "sender-device";
    private const int SenderPort = 5000;
    private const string PeerHost = "peer-device";
    private const int PeerPort = 5001;
    private static readonly Endpoint SenderEndpoint = new(SenderHost, SenderPort);
    private static readonly Endpoint PeerEndpoint = new(PeerHost, PeerPort);

    [AvaloniaFact]
    public async Task Browse_ThenPullFromTrustedPeer_CompletesByteIdentical()
    {
        var payload = RandomBytes(7, 30_000);
        using var key = ManifestSigner.CreateSigningKey();
        var transfer = BuildTransfer(key, "photo.jpg", payload, chunkSize: 2048);

        var streamNetwork = new InMemoryStreamNetwork();
        await using var serve = StartServe(streamNetwork, SenderEndpoint, transfer.Sender.CreateSwarmContentSource());

        var discoveryNetwork = new InMemoryDiscoveryNetwork();
        await using var advertiser = new InMemoryServiceDiscovery(discoveryNetwork, host: SenderHost);
        await advertiser.AdvertiseAsync("Sender Device", SenderPort);

        var holder = new SinkHolder();
        var browser = new InMemoryServiceDiscovery(discoveryNetwork, host: "receiver-device");
        using var vm = new MobileReceiveViewModel(
            browser,
            prompt => NewSession(streamNetwork, TrustedStoreFor(key), holder, prompt));

        vm.StartBrowsingCommand.Execute(null);
        await PumpUntil(() => vm.Peers.Count > 0);

        Assert.Single(vm.Peers);
        Assert.Equal("Sender Device", vm.Peers[0].DisplayName);
        Assert.NotNull(vm.SelectedPeer); // first peer auto-selected

        var pull = vm.PullCommand.ExecuteAsync(null);
        await PumpUntil(() => vm.Progress.IsComplete);
        await pull;

        Assert.True(vm.Progress.IsComplete);
        Assert.Equal("Transfer complete.", vm.Status);
        Assert.NotNull(holder.Sink);
        Assert.Equal(payload, holder.Sink!.ToArray());
    }

    [AvaloniaFact]
    public async Task PullFromUntrustedSender_IsRejected_NoData()
    {
        var payload = RandomBytes(8, 8000);
        using var key = ManifestSigner.CreateSigningKey();
        var transfer = BuildTransfer(key, "secret.bin", payload, chunkSize: 1024);

        var streamNetwork = new InMemoryStreamNetwork();
        await using var serve = StartServe(streamNetwork, SenderEndpoint, transfer.Sender.CreateSwarmContentSource());

        var discoveryNetwork = new InMemoryDiscoveryNetwork();
        await using var advertiser = new InMemoryServiceDiscovery(discoveryNetwork, host: SenderHost);
        await advertiser.AdvertiseAsync("Untrusted Sender", SenderPort);

        var browser = new InMemoryServiceDiscovery(discoveryNetwork, host: "receiver-device");
        using var vm = new MobileReceiveViewModel(
            browser,
            prompt => NewSession(streamNetwork, new InMemoryTrustStore() /* never trusted */, new SinkHolder(), prompt));

        vm.StartBrowsingCommand.Execute(null);
        await PumpUntil(() => vm.Peers.Count > 0);

        var pull = vm.PullCommand.ExecuteAsync(null);
        await PumpUntil(() => !vm.IsPulling);
        await pull;

        Assert.False(vm.Progress.IsComplete);
        // PullAsync's synchronous "peer rejected" write and OnTrustDenied's Dispatcher.UIThread.Post callback
        // both fire for this scenario; whichever lands last wins the Status text (observed to vary by platform
        // dispatcher-pump timing - a real, harmless race, since either message correctly signals rejection).
        // Mirrors the same tolerance SwarmReceiveFlowTests already uses for the identical race.
        bool rejected = vm.Status.Contains("untrusted", StringComparison.OrdinalIgnoreCase)
                        || vm.Status.Contains("denied", StringComparison.OrdinalIgnoreCase);
        Assert.True(rejected, $"Expected a rejection status, got: {vm.Status}");
    }

    [AvaloniaFact]
    public async Task PartialPull_ResumesFromSecondPeer_Completes()
    {
        var payload = RandomBytes(9, 24_000);
        using var key = ManifestSigner.CreateSigningKey();
        var transfer = BuildTransfer(key, "movie.mp4", payload, chunkSize: 1000);
        int chunkCount = ChunkLayout.ComputeChunkCount(payload.Length, 1000);

        var streamNetwork = new InMemoryStreamNetwork();
        // Peer A (the advertised sender) serves only the first half, then a full peer B fills the rest.
        var partial = new HalfServingSource(transfer.Sender.CreateSwarmContentSource(), chunkCount / 2);
        await using var serveA = StartServe(streamNetwork, SenderEndpoint, partial);
        await using var serveB = StartServe(streamNetwork, PeerEndpoint, transfer.Sender.CreateSwarmContentSource());

        var discoveryNetwork = new InMemoryDiscoveryNetwork();
        await using var advA = new InMemoryServiceDiscovery(discoveryNetwork, host: SenderHost);
        await using var advB = new InMemoryServiceDiscovery(discoveryNetwork, host: PeerHost);
        await advA.AdvertiseAsync("Peer A (partial)", SenderPort);
        await advB.AdvertiseAsync("Peer B (full)", PeerPort);

        var holder = new SinkHolder();
        var browser = new InMemoryServiceDiscovery(discoveryNetwork, host: "receiver-device");
        using var vm = new MobileReceiveViewModel(
            browser,
            prompt => NewSession(streamNetwork, TrustedStoreFor(key), holder, prompt));

        vm.StartBrowsingCommand.Execute(null);
        await PumpUntil(() => vm.Peers.Count == 2);

        // Pull from Peer A (partial) first.
        vm.SelectedPeer = vm.Peers.Single(p => p.Endpoint == SenderEndpoint);
        var first = vm.PullCommand.ExecuteAsync(null);
        await PumpUntil(() => !vm.IsPulling);
        await first;
        Assert.False(vm.Progress.IsComplete);

        // Resume from Peer B (full) — only missing chunks are re-requested; the same session completes.
        vm.SelectedPeer = vm.Peers.Single(p => p.Endpoint == PeerEndpoint);
        var second = vm.PullCommand.ExecuteAsync(null);
        await PumpUntil(() => vm.Progress.IsComplete);
        await second;

        Assert.True(vm.Progress.IsComplete);
        Assert.NotNull(holder.Sink);
        Assert.Equal(payload, holder.Sink!.ToArray());
    }

    [AvaloniaFact]
    public async Task UnknownSender_SurfacesTofuPrompt_Accept_CompletesAndPersistsTrust()
    {
        var payload = RandomBytes(10, 9000);
        using var key = ManifestSigner.CreateSigningKey();
        var transfer = BuildTransfer(key, "prompted.bin", payload, chunkSize: 1024);

        var streamNetwork = new InMemoryStreamNetwork();
        await using var serve = StartServe(streamNetwork, SenderEndpoint, transfer.Sender.CreateSwarmContentSource());

        var discoveryNetwork = new InMemoryDiscoveryNetwork();
        await using var advertiser = new InMemoryServiceDiscovery(discoveryNetwork, host: SenderHost);
        await advertiser.AdvertiseAsync("Unknown Sender", SenderPort);

        var holder = new SinkHolder();
        var store = new InMemoryTrustStore(); // sender NOT pre-trusted — the prompt must fire
        var browser = new InMemoryServiceDiscovery(discoveryNetwork, host: "receiver-device");
        using var vm = new MobileReceiveViewModel(
            browser,
            prompt => NewSession(streamNetwork, store, holder, prompt,
                UnknownSenderPolicy.Prompt, interactive: true));

        vm.StartBrowsingCommand.Execute(null);
        await PumpUntil(() => vm.Peers.Count > 0);

        var pull = vm.PullCommand.ExecuteAsync(null);

        // The pull blocks until the user decides; the view-model surfaces the inline TOFU prompt.
        await PumpUntil(() => vm.PendingTrustPrompt is not null);
        Assert.Equal(transfer.Signed.SenderId.Value, vm.PendingTrustPrompt!.SenderId);

        vm.PendingTrustPrompt!.AcceptCommand.Execute(null);

        await PumpUntil(() => vm.Progress.IsComplete);
        await pull;

        Assert.True(vm.Progress.IsComplete);
        Assert.Null(vm.PendingTrustPrompt); // prompt cleared after the decision
        Assert.NotNull(holder.Sink);
        Assert.Equal(payload, holder.Sink!.ToArray());
        Assert.NotNull(store.Find(transfer.Signed.SenderId)); // TOFU persisted for next time
    }

    // ---- harness ----

    private sealed class SinkHolder
    {
        public MemoryFileSink? Sink { get; set; }
    }

    private static async Task PumpUntil(Func<bool> condition, int timeoutMs = 20_000)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromMilliseconds(timeoutMs);
        while (!condition() && DateTime.UtcNow < deadline)
        {
            Dispatcher.UIThread.RunJobs();
            await Task.Delay(15);
        }
        Dispatcher.UIThread.RunJobs();
        Assert.True(condition(), "Timed out waiting for the expected view-model state.");
    }

    private sealed record Transfer(SignedManifest Signed, SenderSession Sender);

    private static Transfer BuildTransfer(Key key, string relativePath, byte[] bytes, int chunkSize)
    {
        var sessionId = new byte[16];
        var source = new MemoryFileSource(bytes);
        var contentKey = ContentKey.Generate();
        var senderEncryptionKey = EncryptionKeys.Create();

        var hashes = EncryptedChunkHasher.ComputeAsync(source, chunkSize, sessionId, 0, contentKey).GetAwaiter().GetResult();
        var tree = MerkleTree.Build(hashes);
        int chunkCount = ChunkLayout.ComputeChunkCount(bytes.Length, chunkSize);

        var manifest = new TransferManifest(
            sessionId, "mobile-transfer", DateTimeOffset.UtcNow, EncryptionKeys.ExportPublicKey(senderEncryptionKey),
            [new ManifestFileEntry(relativePath, bytes.Length, chunkSize, chunkCount, tree.Root)]);
        var signed = ManifestSigner.Sign(manifest, key);

        var sender = new SenderSession(
            signed,
            new Dictionary<int, IFileSource> { [0] = source },
            new Dictionary<int, MerkleTree> { [0] = tree },
            new InMemoryNetwork().CreateMulticastTransport(new Endpoint("unused", 0)),
            senderEncryptionKey, contentKey, maxDatagramPayloadBytes: 65_000);

        return new Transfer(signed, sender);
    }

    private static SwarmPullSession NewSession(
        InMemoryStreamNetwork network, ITrustStore store, SinkHolder holder, ITrustPrompt prompt,
        UnknownSenderPolicy policy = UnknownSenderPolicy.Deny, bool interactive = false)
    {
        // The sink is created lazily by the session once it accepts the manifest; the holder bridges it back
        // to the test so it can assert the written bytes after the pull completes.
        Func<string, long, IFileSink> sinkFactory = (_, length) =>
            holder.Sink = new MemoryFileSink((int)length);
        return new SwarmPullSession(
            ReceiverId(1), store, network.CreateClient(new Endpoint("receiver-device", 1)),
            new FakeClock(DateTimeOffset.UtcNow),
            new SwarmPullSessionOptions("/root", policy, interactive), sinkFactory, prompt);
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
            try { await task; } catch (OperationCanceledException) { }
            await listener.DisposeAsync();
            cts.Dispose();
        }
    }

    private static ITrustStore TrustedStoreFor(Key key)
    {
        var store = new InMemoryTrustStore();
        var publicKey = key.PublicKey.Export(KeyBlobFormat.RawPublicKey);
        store.Upsert(new TrustEntry(
            PublicKeyId.FromRawEd25519(publicKey), "sender", TrustStatus.Trusted,
            DateTimeOffset.UnixEpoch, TrustEntrySource.Manual));
        return store;
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

    private sealed class HalfServingSource(ISwarmContentSource inner, int servedBelowIndex) : ISwarmContentSource
    {
        public SignedManifest? Manifest => inner.Manifest;
        public ValueTask<SwarmChunk?> TryGetChunkAsync(int fileIndex, int chunkIndex, CancellationToken cancellationToken = default) =>
            chunkIndex < servedBelowIndex
                ? inner.TryGetChunkAsync(fileIndex, chunkIndex, cancellationToken)
                : ValueTask.FromResult<SwarmChunk?>(null);
        public KeyGrantMessage? TryGrantContentKey(JoinRequestMessage request) => inner.TryGrantContentKey(request);
    }
}
