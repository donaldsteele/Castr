using System.Runtime.CompilerServices;
using Castr.Core.Chunking;
using Castr.Core.Manifest;
using Castr.Core.Protocol;
using Castr.Core.Security;
using Castr.Core.Time;
using Castr.Core.Transport;
using Castr.Core.Trust;

namespace Castr.Core.Tests.Protocol;

/// <summary>
/// Regression tests for the receive-loop hardening (QA Defect #2): every message-handling path that takes a
/// file/chunk index straight off the wire must validate it is in range before indexing the per-file
/// <see cref="ChunkBitmap"/>. An out-of-range index used to reach <see cref="ChunkBitmap.Get"/>, which throws
/// <see cref="ArgumentOutOfRangeException"/> — and nothing above <see cref="ReceiverSession.RunAsync"/> caught
/// it, so a single crafted packet faulted the whole receiver. These prove such a packet is now dropped and the
/// loop stays up.
/// </summary>
public class ReceiverSessionIndexHardeningTests
{
    // The trusted manifest describes a single 1-chunk file, so any chunkIndex other than 0 is out of range.
    private const int ValidChunkIndex = 0;
    private const int OutOfRangeChunkIndex = int.MaxValue;

    [Fact]
    public async Task ChunkData_OutOfRangeChunkIndex_IsDropped_LoopSurvives()
    {
        var (sessionId, proof) = ManifestFixture();
        var malicious = new ChunkDataMessage(sessionId, FileIndex: 0, ChunkIndex: OutOfRangeChunkIndex, Payload: new byte[16], proof);

        var fault = await RunWithInjectedAsync(malicious);

        Assert.True(fault is null or OperationCanceledException, $"receive loop faulted on ChunkData: {fault}");
    }

    [Fact]
    public async Task ChunkResponse_OutOfRangeChunkIndex_IsDropped_LoopSurvives()
    {
        var (sessionId, proof) = ManifestFixture();
        var malicious = new ChunkResponseMessage(sessionId, RequestNonce: new byte[16], FileIndex: 0, ChunkIndex: OutOfRangeChunkIndex, Payload: new byte[16], proof);

        var fault = await RunWithInjectedAsync(malicious);

        Assert.True(fault is null or OperationCanceledException, $"receive loop faulted on ChunkResponse: {fault}");
    }

    [Fact]
    public async Task ChunkPacket_OutOfRangeChunkIndex_IsDropped_LoopSurvives()
    {
        var (sessionId, proof) = ManifestFixture();
        var malicious = new ChunkPacketMessage(
            sessionId, FileIndex: 0, ChunkIndex: OutOfRangeChunkIndex, FragmentOffset: 0, CiphertextLength: 16, Fragment: new byte[16], Proof: proof);

        var fault = await RunWithInjectedAsync(malicious);

        Assert.True(fault is null or OperationCanceledException, $"receive loop faulted on ChunkPacket: {fault}");
    }

    [Fact]
    public async Task ValidInRangeChunkIndex_IsNotRejectedByTheGuard()
    {
        // Sanity check that the new range guard does not reject a legitimately in-range index: an in-range but
        // unverifiable chunk is dropped by Merkle verification (not the guard) — either way, no fault.
        var (sessionId, proof) = ManifestFixture();
        var inRange = new ChunkDataMessage(sessionId, FileIndex: 0, ChunkIndex: ValidChunkIndex, Payload: new byte[16], proof);

        var fault = await RunWithInjectedAsync(inRange);

        Assert.True(fault is null or OperationCanceledException, $"receive loop faulted on in-range chunk: {fault}");
    }

    // ---- fixture ----

    /// <summary>Builds a validly-signed, trusted manifest (single 1-chunk file) and returns its session id plus a throwaway proof for crafting packets.</summary>
    private static (byte[] SessionId, MerkleProof Proof) ManifestFixture()
    {
        var sessionId = new byte[TransferManifest.SessionIdSize];
        var proof = MerkleTree.Build([ChunkHash.Compute(new byte[] { 1 })]).GetProof(0);
        return (sessionId, proof);
    }

    private static async Task<Exception?> RunWithInjectedAsync(object maliciousMessage)
    {
        using var signingKey = ManifestSigner.CreateSigningKey();
        var senderEncryptionKey = EncryptionKeys.Create();
        var sessionId = new byte[TransferManifest.SessionIdSize];
        var root = MerkleTree.Build([ChunkHash.Compute(new byte[] { 1 })]).Root;

        var manifest = new TransferManifest(
            sessionId, "hardening", DateTimeOffset.UtcNow, EncryptionKeys.ExportPublicKey(senderEncryptionKey),
            [new ManifestFileEntry("f.bin", Size: 10, ChunkSize: 10, ChunkCount: 1, root)]);
        var signed = ManifestSigner.Sign(manifest, signingKey);

        var trustStore = new InMemoryTrustStore();
        trustStore.Upsert(new TrustEntry(signed.SenderId, "sender", TrustStatus.Trusted, DateTimeOffset.UnixEpoch, TrustEntrySource.Manual));

        var inbound = new[]
        {
            MessageCodec.Encode(new ManifestMessage(signed)),
            MessageCodec.Encode(maliciousMessage),
        };
        var transport = new ScriptedTransport(inbound);

        var receiver = new ReceiverSession(
            ReceiverId(1), trustStore, transport, new FakeClock(DateTimeOffset.UtcNow),
            new ReceiverSessionOptions("/virtual-root"), (_, length) => new MemoryFileSink((int)length));

        using var cts = new CancellationTokenSource();
        var run = receiver.RunAsync(cts.Token);

        // Give the loop ample time to drain both injected datagrams (manifest, then the crafted packet). Before the
        // fix the crafted packet throws out of RunAsync here; after the fix it is dropped and the loop idles.
        await Task.Delay(300, CancellationToken.None);
        await cts.CancelAsync();
        return await Record.ExceptionAsync(() => run);
    }

    private static byte[] ReceiverId(byte fill)
    {
        var bytes = new byte[16];
        Array.Fill(bytes, fill);
        return bytes;
    }

    /// <summary>Test transport that replays a fixed script of inbound datagrams, then idles until cancelled. Sends are discarded.</summary>
    private sealed class ScriptedTransport(IReadOnlyList<byte[]> inbound) : IMulticastTransport
    {
        public ValueTask SendAsync(ReadOnlyMemory<byte> payload, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;

        public async IAsyncEnumerable<ReceivedPacket> ReceiveAsync([EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            foreach (var datagram in inbound)
            {
                cancellationToken.ThrowIfCancellationRequested();
                yield return new ReceivedPacket(datagram, new Endpoint("attacker", 0));
            }
            await Task.Delay(Timeout.Infinite, cancellationToken); // idle until the test cancels
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
