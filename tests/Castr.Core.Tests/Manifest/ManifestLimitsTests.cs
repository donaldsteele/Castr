using System.Runtime.CompilerServices;
using Castr.Core.Chunking;
using Castr.Core.Manifest;
using Castr.Core.Protocol;
using Castr.Core.Security;
using Castr.Core.Time;
using Castr.Core.Transport;
using Castr.Core.Trust;

namespace Castr.Core.Tests.Manifest;

/// <summary>
/// Structural validation of an accepted manifest. Being signed makes a manifest authentic, not well-formed:
/// <c>ManifestFileEntry.ChunkSize</c> was never range-checked in <c>ManifestCodec.Decode</c> or anywhere after
/// it, so a trusted-but-buggy or compromised sender's numbers were acted on as given.
/// </summary>
public class ManifestLimitsTests
{
    [Fact]
    public void ChunkSizeNearIntMax_OverflowsTheAssemblerBound_WhichIsWhyItMustNotBeAccepted()
    {
        // The consequence, stated first so the rejection below has something to point at: the reassembler's
        // per-chunk ceiling is chunkSize + 16, and at int.MaxValue that wraps negative — which the
        // ChunkPacketAssembler constructor rejects by throwing, from a receive loop that does not wrap manifest
        // handling. This assertion documents the hazard; the next test is the fix.
        Assert.True(ChunkPacketAssembler.CiphertextBoundForChunkSize(int.MaxValue) < 0);
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new ChunkPacketAssembler(ChunkPacketAssembler.CiphertextBoundForChunkSize(int.MaxValue)));
    }

    [Theory]
    [InlineData(int.MaxValue, 1L, 1)]                        // overflows CiphertextBoundForChunkSize
    [InlineData(ManifestLimits.MaxChunkSize + 1, 1L, 1)]     // one byte over the ceiling
    [InlineData(0, 10L, 1)]                                  // zero chunk size
    [InlineData(-4096, 10L, 1)]                              // negative chunk size
    public void OutOfRangeChunkSize_IsRejected(int chunkSize, long size, int chunkCount)
    {
        var manifest = Fixture(size, chunkSize, chunkCount);

        Assert.False(ManifestLimits.IsWellFormed(manifest));
        Assert.Contains("chunk size", ManifestLimits.Validate(manifest)!, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(4096, 10_000L, 1)]   // claims 1, is 3
    [InlineData(4096, 10_000L, 99)]  // claims 99, is 3
    [InlineData(4096, 0L, 1)]        // an empty file has no chunks
    public void ChunkCountThatDisagreesWithSizeAndChunkSize_IsRejected(int chunkSize, long size, int chunkCount)
    {
        // Self-consistency matters independently of the range check: ReceiverSession sizes every per-file
        // ChunkBitmap from ChunkCount while every offset comes from (Size, ChunkSize).
        Assert.False(ManifestLimits.IsWellFormed(Fixture(size, chunkSize, chunkCount)));
    }

    [Fact]
    public void NegativeSize_IsRejected()
    {
        Assert.False(ManifestLimits.IsWellFormed(Fixture(size: -1, chunkSize: 4096, chunkCount: 0)));
    }

    [Fact]
    public void EmptyRelativePath_IsRejected()
    {
        Assert.False(ManifestLimits.IsWellFormed(Fixture(size: 10, chunkSize: 4096, chunkCount: 1, relativePath: "")));
    }

    [Theory]
    [InlineData(0L, 4096, 0)]
    [InlineData(1L, 4096, 1)]
    [InlineData(10_000L, 4096, 3)]
    [InlineData(1L, 1, 1)]                                            // the smallest legal chunk size
    [InlineData(ManifestLimits.MaxChunkSize, ManifestLimits.MaxChunkSize, 1)] // exactly at the ceiling
    public void WellFormedManifests_AreAccepted(long size, int chunkSize, int chunkCount)
    {
        Assert.Null(ManifestLimits.Validate(Fixture(size, chunkSize, chunkCount)));
    }

    [Fact]
    public async Task SignedButMalformedManifest_IsRefusedAtAdmission()
    {
        using var key = ManifestSigner.CreateSigningKey();
        var signed = ManifestSigner.Sign(Fixture(size: 1, chunkSize: int.MaxValue, chunkCount: 1), key);

        var store = new InMemoryTrustStore();
        store.Upsert(new TrustEntry(signed.SenderId, "sender", TrustStatus.Trusted, DateTimeOffset.UnixEpoch, TrustEntrySource.Manual));

        var result = await ManifestAdmission.EvaluateAsync(
            signed, store, new FakeClock(DateTimeOffset.UnixEpoch), UnknownSenderPolicy.Deny,
            isInteractive: false, trustPrompt: null, CancellationToken.None);

        // Not SignatureInvalid: the sender really did sign this, which is exactly the case that made the gap a
        // robustness problem rather than a remote hole — and exactly why it still had to be closed.
        Assert.Equal(ManifestAdmissionOutcome.Malformed, result.Outcome);
        Assert.True(ManifestVerifier.VerifySignature(signed));
    }

    [Fact]
    public async Task MalformedManifestOnTheWire_DoesNotFaultTheReceiveLoop()
    {
        // The end of the chain, which is what the M8 note in ReceiverSession was actually worried about: manifest
        // handling is not wrapped in the receive loop, so an out-of-range ChunkSize reaching the
        // ChunkPacketAssembler constructor took the whole receiver down.
        using var signingKey = ManifestSigner.CreateSigningKey();
        var signed = ManifestSigner.Sign(Fixture(size: 1, chunkSize: int.MaxValue, chunkCount: 1), signingKey);

        var trustStore = new InMemoryTrustStore();
        trustStore.Upsert(new TrustEntry(signed.SenderId, "sender", TrustStatus.Trusted, DateTimeOffset.UnixEpoch, TrustEntrySource.Manual));

        var receiver = new ReceiverSession(
            ReceiverId(1), trustStore, new ScriptedTransport([MessageCodec.Encode(new ManifestMessage(signed))]),
            new FakeClock(DateTimeOffset.UtcNow), new ReceiverSessionOptions("/virtual-root"),
            (_, length) => new MemoryFileSink((int)length));

        using var cts = new CancellationTokenSource();
        var run = receiver.RunAsync(cts.Token);
        await Task.Delay(300, CancellationToken.None);
        await cts.CancelAsync();

        var fault = await Record.ExceptionAsync(() => run);

        Assert.True(fault is null or OperationCanceledException, $"receive loop faulted on a malformed manifest: {fault}");
        Assert.Null(receiver.Manifest); // and it was not accepted either
    }

    // ---- harness ----

    private static TransferManifest Fixture(long size, int chunkSize, int chunkCount, string relativePath = "f.bin")
    {
        using var encryptionKey = EncryptionKeys.Create();
        return new TransferManifest(
            new byte[TransferManifest.SessionIdSize], "limits-fixture", DateTimeOffset.UnixEpoch,
            EncryptionKeys.ExportPublicKey(encryptionKey),
            [new ManifestFileEntry(relativePath, size, chunkSize, chunkCount, ChunkHash.Compute([1]))]);
    }

    private static byte[] ReceiverId(byte fill)
    {
        var bytes = new byte[16];
        Array.Fill(bytes, fill);
        return bytes;
    }

    /// <summary>Replays a fixed script of inbound datagrams, then idles until cancelled. Sends are discarded.</summary>
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
            await Task.Delay(Timeout.Infinite, cancellationToken);
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
