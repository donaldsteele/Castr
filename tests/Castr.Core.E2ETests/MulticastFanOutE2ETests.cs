using Castr.Core.E2ETests.Infrastructure;
using Xunit.Abstractions;

namespace Castr.Core.E2ETests;

/// <summary>
/// Real multi-container, end-to-end fan-out tests for the shipped <c>castr</c> CLI. Unlike the in-process
/// <c>Castr.Core.IntegrationTests</c> (many sockets, one OS network stack, simulated loss), each participant
/// here is a separate container with its own network namespace on a shared Docker bridge, and loss is real
/// kernel <c>tc netem</c> loss — the closest thing to a physical LAN a CI job can reach.
///
/// This whole class is the opt-in E2E tier: every test uses <see cref="E2EFactAttribute"/> (skipped unless
/// <c>CASTR_E2E</c> is set and Docker is reachable) and carries <c>[Trait("Category","E2E")]</c> so a CI job
/// can target it with <c>--filter Category=E2E</c>. See README.md.
/// </summary>
[Collection(CastrClusterCollection.Name)]
[Trait("Category", "E2E")]
public sealed class MulticastFanOutE2ETests
{
    // Payload sizes are per-test, and they are sized in CHUNKS rather than bytes — which is the thing that
    // actually decides what repair logic runs.
    //
    // This used to be one shared 4 MB constant. At the old 8 KiB default that was 512 chunks; when M8 raised
    // the default to 256 KiB the same constant silently became **16 chunks**, and at 16 chunks most of the
    // repair machinery this tier exists to defend is unreachable: MaxChunksPerRequest=268 fits the entire
    // file in a single request so the multi-batch split path never runs, MaxRequestsPerPass=4 can never bind,
    // and the carousel watermark has 16 positions to move through. Castr.Core.IntegrationTests do cover
    // many-chunk repair, but single-receiver over loopback — so fan-out + real kernel netem loss + many
    // chunks would have been covered nowhere. Keep these denominated in chunks if the default changes again.

    /// <summary>4 MB = 16 chunks at the 256 KiB default. Fan-out breadth only — no loss, no repair.</summary>
    private const int FanOutPayloadBytes = 4 * 1024 * 1024;

    /// <summary>64 MB = <b>256 chunks</b> at the 256 KiB default: the many-chunk repair-under-real-loss case.</summary>
    private const int LossPayloadBytes = 64 * 1024 * 1024;

    /// <summary>16 MB = 64 chunks. Smaller than the 5-receiver case on purpose — nine containers each writing
    /// 64 MB is a lot of Docker I/O for a marginal gain, and the multi-batch path is already covered there.</summary>
    private const int WideFanOutLossPayloadBytes = 16 * 1024 * 1024;

    private readonly CastrClusterFixture _fixture;
    private readonly ITestOutputHelper _output;

    public MulticastFanOutE2ETests(CastrClusterFixture fixture, ITestOutputHelper output)
    {
        _fixture = fixture;
        _output = output;
    }

    [E2EFact]
    public async Task SevenReceivers_NoLoss_AllReceiveByteIdenticalFile()
    {
        var result = await CastrFanOut.RunAsync(
            _fixture, receiverCount: 7, port: 45055, lossPercent: null,
            payloadBytes: FanOutPayloadBytes, completionTimeout: TimeSpan.FromSeconds(90));

        _output.WriteLine($"expected sha256: {result.ExpectedSha256}");
        for (int i = 0; i < result.ReceiverSha256.Count; i++)
            _output.WriteLine($"receiver {i} sha256: {result.ReceiverSha256[i]}");

        Assert.Equal(7, result.ReceiverSha256.Count);
        Assert.All(result.ReceiverSha256, h => Assert.Equal(result.ExpectedSha256, h));
    }

    [E2EFact]
    public async Task FiveReceivers_UnderRealNetemLoss_RecoverViaRepair()
    {
        // 20% real first-fragment loss on the sender's egress => ~20% of chunk datagrams genuinely dropped by
        // the kernel. The transfer can only complete via Castr's CHUNK_REQUEST/RESPONSE peer+sender repair.
        //
        // This is the tier's many-chunk case: 256 chunks, so the carousel watermark, per-chunk backoff/jitter
        // and the repair bookkeeping all run at a realistic scale under real fan-out and real kernel loss,
        // rather than the 16 positions a 4 MB payload gives at the 256 KiB default.
        //
        // Honest scope, because the obvious reading is wrong: this still does NOT exercise multi-batch
        // CHUNK_REQUEST splitting or RepairOptions.MaxRequestsPerPass. Both need MORE than 268 chunks missing
        // at once, and post-M7 that is only reachable in the cold-start failure mode the carousel watermark
        // exists to prevent. They are covered where those conditions can be constructed directly:
        // RepairCoordinatorTests.PlanRepairs_MoreMissingThanTheCap_SplitsIntoSingleDatagramRequests_EachWithItsOwnNonce,
        // ...HugeMissSet_IsBoundedByMaxRequestsPerPass_NotJustPerRequest, and the 280-chunk lost run in
        // ReceiverSessionGossipAndRepairTests. Raising this payload further would not reach them.
        //
        // The generous timeout is because recovery is quantised by the 5 s RepairOptions.RequestTimeout, so a
        // few unlucky rounds cost tens of seconds.
        var result = await CastrFanOut.RunAsync(
            _fixture, receiverCount: 5, port: 45056, lossPercent: 20,
            payloadBytes: LossPayloadBytes, completionTimeout: TimeSpan.FromSeconds(300));

        _output.WriteLine($"netem-dropped packets (sender egress): {result.NetemDroppedPackets}");
        _output.WriteLine($"expected sha256: {result.ExpectedSha256}");
        for (int i = 0; i < result.ReceiverSha256.Count; i++)
            _output.WriteLine($"receiver {i} sha256: {result.ReceiverSha256[i]}");

        Assert.Equal(5, result.ReceiverSha256.Count);
        Assert.All(result.ReceiverSha256, h => Assert.Equal(result.ExpectedSha256, h));
        // Prove the recovery was real: the kernel must actually have dropped chunk datagrams.
        Assert.True(result.NetemDroppedPackets > 0,
            "Expected netem to have dropped packets; otherwise repair was not exercised under real loss.");
    }

    [E2EFact]
    public async Task NineReceivers_UnderModerateLoss_AllRecoverByteIdentical()
    {
        // Top of the plan's suggested 5-9 fan-out range, under a lighter 10% real loss. With nine receivers,
        // peers that received a given chunk cleanly can serve it to peers that lost it (peer repair), not just
        // the sender.
        var result = await CastrFanOut.RunAsync(
            _fixture, receiverCount: 9, port: 45057, lossPercent: 10,
            payloadBytes: WideFanOutLossPayloadBytes, completionTimeout: TimeSpan.FromSeconds(300));

        _output.WriteLine($"netem-dropped packets (sender egress): {result.NetemDroppedPackets}");
        _output.WriteLine($"expected sha256: {result.ExpectedSha256}");
        for (int i = 0; i < result.ReceiverSha256.Count; i++)
            _output.WriteLine($"receiver {i} sha256: {result.ReceiverSha256[i]}");

        Assert.Equal(9, result.ReceiverSha256.Count);
        Assert.All(result.ReceiverSha256, h => Assert.Equal(result.ExpectedSha256, h));
        Assert.True(result.NetemDroppedPackets > 0, "Expected netem to have dropped packets.");
    }
}
