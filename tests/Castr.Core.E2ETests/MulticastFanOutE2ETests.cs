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
    private const int PayloadBytes = 4 * 1024 * 1024; // ~4 MB => 512 chunks at the CLI's 8 KB default

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
            payloadBytes: PayloadBytes, completionTimeout: TimeSpan.FromSeconds(90));

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
        var result = await CastrFanOut.RunAsync(
            _fixture, receiverCount: 5, port: 45056, lossPercent: 20,
            payloadBytes: PayloadBytes, completionTimeout: TimeSpan.FromSeconds(150));

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
            payloadBytes: PayloadBytes, completionTimeout: TimeSpan.FromSeconds(150));

        _output.WriteLine($"netem-dropped packets (sender egress): {result.NetemDroppedPackets}");
        _output.WriteLine($"expected sha256: {result.ExpectedSha256}");
        for (int i = 0; i < result.ReceiverSha256.Count; i++)
            _output.WriteLine($"receiver {i} sha256: {result.ReceiverSha256[i]}");

        Assert.Equal(9, result.ReceiverSha256.Count);
        Assert.All(result.ReceiverSha256, h => Assert.Equal(result.ExpectedSha256, h));
        Assert.True(result.NetemDroppedPackets > 0, "Expected netem to have dropped packets.");
    }
}
