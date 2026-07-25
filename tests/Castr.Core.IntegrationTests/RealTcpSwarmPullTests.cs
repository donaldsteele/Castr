using System.Net;
using Castr.Core.Chunking;
using Castr.Core.Manifest;
using Castr.Core.Protocol;
using Castr.Core.Security;
using Castr.Core.Swarm;
using Castr.Core.Time;
using Castr.Core.Transport;
using Castr.Core.Transport.InMemory;
using Castr.Core.Transport.Tcp;
using Castr.Core.Trust;
using NSec.Cryptography;

namespace Castr.Core.IntegrationTests;

/// <summary>
/// End-to-end over real TCP loopback sockets: a real SenderSession served by a SwarmServeListener on a real
/// TcpStreamListener, pulled by a real SwarmPullSession over a real TcpStreamClient. Proves the unicast-swarm
/// path works across actual sockets (not just the in-memory fake): byte-identical result, untrusted-peer
/// rejection, tampered-chunk rejection, and resume after a partial pull from a different peer.
/// </summary>
public class RealTcpSwarmPullTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(20);

    [Fact]
    public async Task RealTcpLoopback_PullFromSender_IsByteIdentical()
    {
        var original = RandomBytes(1, 60_000);
        using var key = ManifestSigner.CreateSigningKey();
        var transfer = BuildTransfer(key, "movie.bin", original, chunkSize: 2000);

        await using var serve = StartServe(transfer.Sender.CreateSwarmContentSource(), out var endpoint);

        var (sink, sinkFactory) = MemorySinkFactory();
        using var pull = NewPull(TrustedStoreFor(key), sinkFactory);

        using var cts = new CancellationTokenSource(Timeout);
        bool accepted = await pull.PullFromAsync(endpoint, cts.Token);

        Assert.True(accepted);
        Assert.True(pull.IsComplete);
        Assert.Equal(original, sink().ToArray());
    }

    [Fact]
    public async Task RealTcpLoopback_UntrustedSender_IsRejected()
    {
        var original = RandomBytes(2, 8000);
        using var key = ManifestSigner.CreateSigningKey();
        var transfer = BuildTransfer(key, "secret.bin", original, chunkSize: 1000);

        await using var serve = StartServe(transfer.Sender.CreateSwarmContentSource(), out var endpoint);

        var (_, sinkFactory) = MemorySinkFactory();
        using var pull = NewPull(new InMemoryTrustStore() /* never trusted */, sinkFactory);

        using var cts = new CancellationTokenSource(Timeout);
        bool accepted = await pull.PullFromAsync(endpoint, cts.Token);

        Assert.False(accepted);
        Assert.Null(pull.Manifest);
        Assert.False(pull.IsComplete);
    }

    [Fact]
    public async Task RealTcpLoopback_TamperedChunk_IsRejected_ThenRecoversFromCleanPeer()
    {
        var original = RandomBytes(3, 15_000);
        using var key = ManifestSigner.CreateSigningKey();
        var transfer = BuildTransfer(key, "data.bin", original, chunkSize: 1000);

        await using var badServe = StartServe(
            new TamperingChunkSource(transfer.Sender.CreateSwarmContentSource(), targetChunkIndex: 3), out var badEndpoint);

        var (sink, sinkFactory) = MemorySinkFactory();
        using var pull = NewPull(TrustedStoreFor(key), sinkFactory);

        using var cts = new CancellationTokenSource(Timeout);
        await pull.PullFromAsync(badEndpoint, cts.Token);
        Assert.False(pull.IsComplete); // tampered chunk failed Merkle verification

        await using var goodServe = StartServe(transfer.Sender.CreateSwarmContentSource(), out var goodEndpoint);
        await pull.PullFromAsync(goodEndpoint, cts.Token);

        Assert.True(pull.IsComplete);
        Assert.Equal(original, sink().ToArray());
    }

    [Fact]
    public async Task RealTcpLoopback_ResumeAfterPartialPull_Completes()
    {
        var original = RandomBytes(4, 24_000);
        using var key = ManifestSigner.CreateSigningKey();
        var transfer = BuildTransfer(key, "resume.bin", original, chunkSize: 1000);
        int chunkCount = ChunkLayout.ComputeChunkCount(original.Length, 1000);

        // First peer serves only the first half of the chunks.
        await using var partialServe = StartServe(
            new PartialChunkSource(transfer.Sender.CreateSwarmContentSource(), servedBelowIndex: chunkCount / 2), out var partialEndpoint);

        var (sink, sinkFactory) = MemorySinkFactory();
        using var pull = NewPull(TrustedStoreFor(key), sinkFactory);

        using var cts = new CancellationTokenSource(Timeout);
        await pull.PullFromAsync(partialEndpoint, cts.Token);
        Assert.False(pull.IsComplete);

        // A full peer supplies the rest; only the missing chunks are re-requested.
        await using var fullServe = StartServe(transfer.Sender.CreateSwarmContentSource(), out var fullEndpoint);
        await pull.PullFromAsync(fullEndpoint, cts.Token);

        Assert.True(pull.IsComplete);
        Assert.Equal(original, sink().ToArray());
    }

    // ---- harness ----

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
            sessionId, "tcp-transfer", DateTimeOffset.UtcNow, EncryptionKeys.ExportPublicKey(senderEncryptionKey),
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

    private static IAsyncDisposable StartServe(ISwarmContentSource source, out Endpoint endpoint)
    {
        var listener = new TcpStreamListener(IPAddress.Loopback, 0);
        endpoint = listener.LocalEndpoint;
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

    private static SwarmPullSession NewPull(ITrustStore store, Func<string, long, IFileSink> sinkFactory) =>
        new(ReceiverId(1), store, new TcpStreamClient(), new FakeClock(DateTimeOffset.UtcNow),
            new SwarmPullSessionOptions("/root"), sinkFactory);

    private static ITrustStore TrustedStoreFor(Key key)
    {
        var store = new InMemoryTrustStore();
        var publicKey = key.PublicKey.Export(KeyBlobFormat.RawPublicKey);
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

    private static byte[] ReceiverId(byte fill)
    {
        var bytes = new byte[16];
        Array.Fill(bytes, fill);
        return bytes;
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

    private sealed class PartialChunkSource(ISwarmContentSource inner, int servedBelowIndex) : ISwarmContentSource
    {
        public SignedManifest? Manifest => inner.Manifest;
        public ValueTask<SwarmChunk?> TryGetChunkAsync(int fileIndex, int chunkIndex, CancellationToken cancellationToken = default) =>
            chunkIndex < servedBelowIndex
                ? inner.TryGetChunkAsync(fileIndex, chunkIndex, cancellationToken)
                : ValueTask.FromResult<SwarmChunk?>(null);
        public KeyGrantMessage? TryGrantContentKey(JoinRequestMessage request) => inner.TryGrantContentKey(request);
    }
}
