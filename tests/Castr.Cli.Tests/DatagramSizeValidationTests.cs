using System.Net;
using System.CommandLine;
using Castr.Cli;
using Castr.Core.Protocol;
using Castr.Core.Trust;
using Spectre.Console.Testing;

namespace Castr.Cli.Tests;

/// <summary>
/// Covers <c>--datagram-size</c>: the option exists on both <c>send</c> and <c>receive</c>, defaults to the
/// MTU-derived <see cref="WirePacketizer.DefaultMaxDatagramPayload"/>, and is range-checked up front — before any
/// file, identity, or socket work — exactly like <c>--chunk-size</c>.
///
/// <para>The bounds are not arbitrary: the floor is the IPv4 minimum-MTU payload (576 − 20 − 8) and the ceiling
/// is the hard UDP-over-IPv4 limit. The default is deliberately the largest payload that does <b>not</b>
/// IP-fragment at a 1500-byte Ethernet MTU.</para>
/// </summary>
public class DatagramSizeValidationTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "castr-cli-datagramsize", Guid.NewGuid().ToString("N"));

    public DatagramSizeValidationTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
        GC.SuppressFinalize(this);
    }

    private static RootCommand Root() => CastrCli.BuildRootCommand(new TestConsole());

    private static SendOptions SendOpts(string filePath, string identityPath, int? datagramSize) =>
        new(filePath, IPAddress.Parse("239.192.55.221"), 45221, InterfaceName: null,
            CastrPaths.DefaultChunkSize, identityPath, UseTui: false, DatagramSize: datagramSize);

    private static ReceiveOptions ReceiveOpts(string destination, int? datagramSize) =>
        new(destination, IPAddress.Parse("239.192.55.222"), 45222, InterfaceName: null,
            UnknownSenderPolicy.Deny, Path.Combine(destination, "trust.json"), TrustSeedPath: null,
            UseTui: false, DatagramSize: datagramSize);

    [Fact]
    public void DefaultDatagramSize_Is1472_TheLargestNonFragmentingPayloadAt1500Mtu()
    {
        // 1500 MTU − 20 IPv4 header − 8 UDP header. Stated as an assertion so a future change to the default has
        // to come here and say why.
        Assert.Equal(1500 - 20 - 8, WirePacketizer.DefaultMaxDatagramPayload);
    }

    [Theory]
    [InlineData("send", "f.bin")]
    [InlineData("receive", null)]
    public void BothCommands_ExposeDatagramSize_AndOmittingItMeansDeriveIt(string command, string? arg)
    {
        // Null rather than a parsed 1472: the runner must be able to tell "the operator chose 1472" from
        // "nobody chose", because only the second may be overridden by the interface MTU.
        string[] args = arg is null ? [command] : [command, arg];
        var result = Root().Parse(args);

        Assert.Empty(result.Errors);
        Assert.Null(result.GetValue<int?>("--datagram-size"));
    }

    [Fact]
    public void Send_ExplicitDatagramSize_Parses()
    {
        var result = Root().Parse(["send", "f.bin", "--datagram-size", "1200"]);

        Assert.Empty(result.Errors);
        Assert.Equal(1200, result.GetValue<int?>("--datagram-size"));
    }

    [Fact]
    public void Resolve_IsExplicitOnly_AndNothingIsAutoDerived()
    {
        // Deliberately dumb, and that is the design. An MTU-derived per-host budget was implemented and then
        // removed in review: a laptop on a 1500-MTU LAN and a peer behind a 1400-MTU VPN would pick different
        // budgets with nobody deciding anything, and mismatched budgets silently lose peer-to-peer repair relay
        // (QA reproduced the strand with two same-version peers at 1372 vs 1472). If it is not explicit, it is
        // the shipped default.
        Assert.Equal(WirePacketizer.DefaultMaxDatagramPayload, DatagramBudget.Resolve(null));
        Assert.Equal(1200, DatagramBudget.Resolve(1200));
        Assert.Equal(9000, DatagramBudget.Resolve(9000));
    }

    [Fact]
    public async Task Send_LegalBudgetButImpossibleForThisFileAndChunkSize_IsRejected_BeforeAnyDatagramIsSent()
    {
        // The crash the new knob made reachable. --datagram-size 548 and --chunk-size 1 are each individually
        // legal, but 64 KiB at 1-byte chunks is 65,536 chunks => a depth-16 tree => a 538-byte proof, and
        // 548 - 43 - 538 < 1, so ChunkPacketizer.Split would have thrown out of the carousel MID-TRANSFER on a
        // configuration that passed every startup check. It must be a clean input rejection instead.
        //
        // This was unreachable while the budget was pinned at 1200, which is exactly why exposing it is what
        // made it reachable.
        var srcPath = Path.Combine(_dir, "deep-tree.bin");
        await File.WriteAllBytesAsync(srcPath, new byte[64 * 1024]);
        var identityPath = Path.Combine(_dir, "identity.key");
        var console = new TestConsole();

        var options = new SendOptions(
            srcPath, IPAddress.Parse("239.192.55.223"), 45223, InterfaceName: null,
            ChunkSize: 1, identityPath, UseTui: false,
            DatagramSize: WirePacketizer.MinMaxDatagramPayload);

        var exit = await SendRunner.RunAsync(options, console, CancellationToken.None);

        Assert.Equal(ExitCodes.InvalidInput, exit);          // rejected, not thrown out of the send loop
        Assert.Contains("--datagram-size", console.Output);  // names BOTH knobs, since either can be the culprit
        Assert.Contains("--chunk-size", console.Output);
    }

    [Fact]
    public async Task Preparation_AtTheShippedDefault_AcceptsTheSameDeepTree()
    {
        // The other direction, so the guard cannot be over-tight: the identical file and chunk size that the
        // 548-byte budget rejects must sail through at the 1472 default, where a proof would have to exceed
        // 1,428 bytes (about 2^43 chunks) to bind. Calls preparation directly — the gate runs after hashing, so a
        // pre-cancelled token would never reach it and a "cancelled means it passed" assertion would be vacuous.
        var srcPath = Path.Combine(_dir, "deep-tree-ok.bin");
        await File.WriteAllBytesAsync(srcPath, new byte[64 * 1024]);

        using var signingKey = Castr.Core.Manifest.ManifestSigner.CreateSigningKey();
        using var prepared = await TransferPreparation.PrepareFileAsync(
            srcPath, signingKey, chunkSize: 1, WirePacketizer.DefaultMaxDatagramPayload, CancellationToken.None);

        Assert.Equal(65_536, prepared.Signed.Manifest.Files[0].ChunkCount);
    }

    [Theory]
    [InlineData(WirePacketizer.MinMaxDatagramPayload - 1)]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(WirePacketizer.MaxMaxDatagramPayload + 1)]
    public async Task Send_DatagramSizeOutOfRange_IsRejected_BeforeAnyFileOrIdentityWork(int datagramSize)
    {
        // A nonexistent source file and identity path: if validation ran after any real work, later steps would
        // fail for unrelated reasons instead of cleanly returning InvalidInput.
        var srcPath = Path.Combine(_dir, "does-not-exist.bin");
        var identityPath = Path.Combine(_dir, "identity.key");
        var console = new TestConsole();

        var exit = await SendRunner.RunAsync(SendOpts(srcPath, identityPath, datagramSize), console, CancellationToken.None);

        Assert.Equal(ExitCodes.InvalidInput, exit);
        Assert.Contains(datagramSize.ToString(), console.Output);
        Assert.False(File.Exists(identityPath)); // SenderIdentity.LoadOrCreate never ran
    }

    [Theory]
    [InlineData(WirePacketizer.MinMaxDatagramPayload)]
    [InlineData(1200)] // the pre-M9 default must remain a legal explicit choice
    [InlineData(WirePacketizer.DefaultMaxDatagramPayload)]
    [InlineData(WirePacketizer.MaxMaxDatagramPayload)]
    public async Task Send_DatagramSizeAtAndInsideBounds_ClearsTheGate(int datagramSize)
    {
        var srcPath = Path.Combine(_dir, $"payload-{datagramSize}.bin");
        await File.WriteAllBytesAsync(srcPath, new byte[1024]);
        var identityPath = Path.Combine(_dir, "identity.key");

        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        var exit = await SendRunner.RunAsync(SendOpts(srcPath, identityPath, datagramSize), new TestConsole(), cts.Token);

        Assert.Equal(ExitCodes.Success, exit); // cancellation, not InvalidInput => it cleared the datagram-size gate
    }

    [Fact]
    public async Task Send_DatagramSizeAboveTheNonFragmentingDefault_IsAllowed_ButWarns()
    {
        // An informed opt-in for a measured jumbo-frame path — allowed, but it must say what it costs, because an
        // IP-fragmented datagram is lost in full when any single fragment is lost, and loopback cannot show that.
        var srcPath = Path.Combine(_dir, "payload-jumbo.bin");
        await File.WriteAllBytesAsync(srcPath, new byte[1024]);
        var identityPath = Path.Combine(_dir, "identity.key");
        var console = new TestConsole();

        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        var exit = await SendRunner.RunAsync(SendOpts(srcPath, identityPath, 9000), console, cts.Token);

        Assert.Equal(ExitCodes.Success, exit);
        Assert.Contains("warning", console.Output, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("9000", console.Output);
    }

    [Theory]
    [InlineData(WirePacketizer.MinMaxDatagramPayload - 1)]
    [InlineData(WirePacketizer.MaxMaxDatagramPayload + 1)]
    public async Task Receive_DatagramSizeOutOfRange_IsRejected(int datagramSize)
    {
        var console = new TestConsole();

        var exit = await ReceiveRunner.RunAsync(ReceiveOpts(_dir, datagramSize), console, CancellationToken.None);

        Assert.Equal(ExitCodes.InvalidInput, exit);
        Assert.Contains(datagramSize.ToString(), console.Output);
    }
}
