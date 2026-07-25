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
/// Drives the real mobile <see cref="SwarmReceiveViewModel"/> end-to-end over the in-memory discovery + stream
/// fakes (no device, emulator, or socket): browse → auto-select a discovered peer → pull a real signed,
/// encrypted transfer → verify byte-identical output and live progress. Also covers the two trust paths — an
/// in-app TOFU prompt accepted, and an untrusted sender denied. This is the same view-model the Android head
/// hosts, minus the native NsdManager.
/// </summary>
public class SwarmReceiveFlowTests
{
    private static readonly Endpoint ServeEndpoint = new("sender-host", 5001);

    [AvaloniaFact]
    public async Task Browse_Select_Pull_TrustedSender_CompletesByteIdentical()
    {
        var payload = RandomBytes(7, 30_000);
        using var signingKey = ManifestSigner.CreateSigningKey();
        var harness = BuildServedTransfer(signingKey, "photo.jpg", payload, chunkSize: 4096);
        await using var _ = harness;

        var (sink, sinkFactory) = MemorySinkFactory();
        var vm = new SwarmReceiveViewModel(
            harness.Browser, harness.StreamNetwork.CreateClient(new Endpoint("receiver-host", 1)),
            TrustedStoreFor(signingKey), ReceiverId(1),
            new SwarmPullSessionOptions("/root"), sinkFactory);

        vm.StartBrowsingCommand.Execute(null);
        await PumpUntil(() => vm.Peers.Count > 0);

        Assert.Single(vm.Peers);
        Assert.NotNull(vm.SelectedPeer); // first peer auto-selected
        Assert.Equal(ServeEndpoint, vm.SelectedPeer!.Endpoint);

        await vm.PullCommand.ExecuteAsync(null);
        await PumpUntil(() => vm.Progress.IsComplete);

        Assert.True(vm.Progress.IsComplete);
        Assert.Equal(100.0, vm.Progress.Percent, 3);
        Assert.Equal(payload, sink().ToArray());

        vm.Dispose();
    }

    [AvaloniaFact]
    public async Task UnknownSender_InAppPromptAccepted_ProceedsAndPersistsTofu()
    {
        var payload = RandomBytes(8, 12_000);
        using var signingKey = ManifestSigner.CreateSigningKey();
        var harness = BuildServedTransfer(signingKey, "prompted.bin", payload, chunkSize: 1024);
        await using var _ = harness;

        var store = new InMemoryTrustStore(); // sender not trusted yet
        var (sink, sinkFactory) = MemorySinkFactory();
        var vm = new SwarmReceiveViewModel(
            harness.Browser, harness.StreamNetwork.CreateClient(new Endpoint("receiver-host", 1)),
            store, ReceiverId(2),
            new SwarmPullSessionOptions("/root", UnknownSenderPolicy.Prompt, IsInteractive: true), sinkFactory);

        vm.StartBrowsingCommand.Execute(null);
        await PumpUntil(() => vm.SelectedPeer is not null);

        var pull = vm.PullCommand.ExecuteAsync(null);

        // The pull blocks on the TOFU decision; the in-app prompt surfaces it for binding.
        await PumpUntil(() => vm.Trust.Pending is not null);
        Assert.NotNull(vm.Trust.Pending);

        vm.Trust.Pending!.AcceptCommand.Execute(null);
        await pull;
        await PumpUntil(() => vm.Progress.IsComplete);

        Assert.True(vm.Progress.IsComplete);
        Assert.Null(vm.Trust.Pending); // overlay dismissed
        Assert.Equal(payload, sink().ToArray());
        Assert.NotNull(store.Find(harness.SenderId)); // TOFU entry persisted

        vm.Dispose();
    }

    [AvaloniaFact]
    public async Task UnknownSender_DenyPolicy_RefusedAndNothingCompletes()
    {
        var payload = RandomBytes(9, 8_000);
        using var signingKey = ManifestSigner.CreateSigningKey();
        var harness = BuildServedTransfer(signingKey, "secret.doc", payload, chunkSize: 1024);
        await using var _ = harness;

        var (_, sinkFactory) = MemorySinkFactory();
        var vm = new SwarmReceiveViewModel(
            harness.Browser, harness.StreamNetwork.CreateClient(new Endpoint("receiver-host", 1)),
            new InMemoryTrustStore() /* never trusted */, ReceiverId(3),
            new SwarmPullSessionOptions("/root", UnknownSenderPolicy.Deny), sinkFactory);

        vm.StartBrowsingCommand.Execute(null);
        await PumpUntil(() => vm.SelectedPeer is not null);

        await vm.PullCommand.ExecuteAsync(null);
        Dispatcher.UIThread.RunJobs();

        Assert.False(vm.Progress.IsComplete);
        // Either the synchronous "peer refused" result or the posted "trust denied" callback may be the last
        // status write; both mean the untrusted sender was rejected and nothing was pulled.
        bool rejected = vm.Status.Contains("refused", StringComparison.OrdinalIgnoreCase)
                        || vm.Status.Contains("denied", StringComparison.OrdinalIgnoreCase);
        Assert.True(rejected, $"Expected a rejection status, got: {vm.Status}");

        vm.Dispose();
    }

    // ---- harness ----

    private sealed class ServedTransfer(
        InMemoryStreamNetwork streamNetwork,
        IServiceDiscovery browser,
        PublicKeyId senderId,
        CancellationTokenSource serveCts,
        Task serveTask,
        IAsyncDisposable listener,
        IAsyncDisposable advertiser) : IAsyncDisposable
    {
        public InMemoryStreamNetwork StreamNetwork => streamNetwork;
        public IServiceDiscovery Browser => browser;
        public PublicKeyId SenderId => senderId;

        public async ValueTask DisposeAsync()
        {
            await serveCts.CancelAsync();
            try { await serveTask; } catch (OperationCanceledException) { }
            await listener.DisposeAsync();
            await advertiser.DisposeAsync();
            await browser.DisposeAsync();
            serveCts.Dispose();
        }
    }

    private static ServedTransfer BuildServedTransfer(Key signingKey, string relativePath, byte[] bytes, int chunkSize)
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
        var signed = ManifestSigner.Sign(manifest, signingKey);

        var sender = new SenderSession(
            signed,
            new Dictionary<int, IFileSource> { [0] = source },
            new Dictionary<int, MerkleTree> { [0] = tree },
            new InMemoryNetwork().CreateMulticastTransport(new Endpoint("unused", 0)),
            senderEncryptionKey, contentKey, maxDatagramPayloadBytes: 65_000);

        var streamNetwork = new InMemoryStreamNetwork();
        var listener = streamNetwork.CreateListener(ServeEndpoint);
        var serve = new SwarmServeListener(listener, sender.CreateSwarmContentSource());
        var serveCts = new CancellationTokenSource();
        var serveTask = serve.RunAsync(serveCts.Token);

        var discoveryNetwork = new InMemoryDiscoveryNetwork();
        var advertiser = new InMemoryServiceDiscovery(discoveryNetwork, host: ServeEndpoint.Host);
        advertiser.AdvertiseAsync("Sender Device", ServeEndpoint.Port).GetAwaiter().GetResult();
        var browser = new InMemoryServiceDiscovery(discoveryNetwork, host: "receiver-host");

        var senderId = PublicKeyId.FromRawEd25519(signingKey.PublicKey.Export(KeyBlobFormat.RawPublicKey));
        return new ServedTransfer(streamNetwork, browser, senderId, serveCts, serveTask, listener, advertiser);
    }

    private static async Task PumpUntil(Func<bool> done, int seconds = 20)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(seconds);
        while (!done() && DateTime.UtcNow < deadline)
        {
            Dispatcher.UIThread.RunJobs();
            await Task.Delay(15);
        }
        Dispatcher.UIThread.RunJobs();
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

    private static byte[] ReceiverId(byte fill)
    {
        var bytes = new byte[16];
        Array.Fill(bytes, fill);
        return bytes;
    }
}
