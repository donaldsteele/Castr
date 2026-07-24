using System.Net;
using System.Security.Cryptography;
using Castr.Cli;
using Castr.Core.Security;
using Castr.Core.Trust;
using Spectre.Console.Testing;

namespace Castr.Cli.Tests;

/// <summary>
/// True end-to-end: drives the CLI's real <see cref="SendRunner"/> and <see cref="ReceiveRunner"/> against
/// each other over real UDP multicast loopback (the way Castr.Core.IntegrationTests does), proving the whole
/// CLI wiring — identity, manifest, trust, key handshake, chunk carousel, repair, disk sink — actually moves a
/// byte-identical file, not just that arguments parse.
/// </summary>
public class EndToEndLoopbackTests : IDisposable
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "castr-cli-e2e", Guid.NewGuid().ToString("N"));

    public EndToEndLoopbackTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    [Fact]
    public async Task Send_ThenReceive_TransfersByteIdenticalFile()
    {
        var group = IPAddress.Parse("239.192.55.211");
        const int port = 45211;

        var payload = RandomNumberGenerator.GetBytes(40_000);
        var srcPath = Path.Combine(_dir, "payload.bin");
        await File.WriteAllBytesAsync(srcPath, payload);

        var identityPath = Path.Combine(_dir, "identity.key");
        var storePath = Path.Combine(_dir, "trust.json");
        var destDir = Path.Combine(_dir, "recv");

        // Establish the sender identity and pre-trust it (as `castr trust add` would).
        using (var identity = SenderIdentity.LoadOrCreate(identityPath))
        {
            var store = new FileTrustStore(storePath);
            store.Upsert(new TrustEntry(identity.PublicKeyId, "e2e-sender", TrustStatus.Trusted, DateTimeOffset.UtcNow, TrustEntrySource.Manual));
        }

        var sendOptions = new SendOptions(srcPath, group, port, InterfaceName: null, ChunkSize: 2048, identityPath, UseTui: false);
        var receiveOptions = new ReceiveOptions(destDir, group, port, InterfaceName: null,
            UnknownSenderPolicy.Deny, storePath, TrustSeedPath: null, UseTui: false);

        using var receiveCts = new CancellationTokenSource(Timeout);
        using var sendCts = new CancellationTokenSource();

        // Receiver must be listening before the sender's one-shot manifest goes out.
        var receiveTask = ReceiveRunner.RunAsync(receiveOptions, new TestConsole(), receiveCts.Token);
        await Task.Delay(500);
        var sendTask = SendRunner.RunAsync(sendOptions, new TestConsole(), sendCts.Token);

        int receiveExit = await receiveTask;
        await sendCts.CancelAsync();
        int sendExit = await sendTask;

        Assert.Equal(ExitCodes.Success, receiveExit);
        Assert.Equal(ExitCodes.Success, sendExit);

        var received = await File.ReadAllBytesAsync(Path.Combine(destDir, "payload.bin"));
        Assert.Equal(payload, received);
    }

    [Fact]
    public async Task Receive_FromUntrustedSenderUnderDeny_ReturnsTrustDenied()
    {
        var group = IPAddress.Parse("239.192.55.212");
        const int port = 45212;

        var payload = RandomNumberGenerator.GetBytes(8_000);
        var srcPath = Path.Combine(_dir, "payload.bin");
        await File.WriteAllBytesAsync(srcPath, payload);

        var identityPath = Path.Combine(_dir, "identity.key");
        var storePath = Path.Combine(_dir, "trust.json"); // empty store => sender is unknown
        var destDir = Path.Combine(_dir, "recv");

        var sendOptions = new SendOptions(srcPath, group, port, InterfaceName: null, ChunkSize: 2048, identityPath, UseTui: false);
        var receiveOptions = new ReceiveOptions(destDir, group, port, InterfaceName: null,
            UnknownSenderPolicy.Deny, storePath, TrustSeedPath: null, UseTui: false);

        using var receiveCts = new CancellationTokenSource(Timeout);
        using var sendCts = new CancellationTokenSource();

        var receiveTask = ReceiveRunner.RunAsync(receiveOptions, new TestConsole(), receiveCts.Token);
        await Task.Delay(500);
        var sendTask = SendRunner.RunAsync(sendOptions, new TestConsole(), sendCts.Token);

        int receiveExit = await receiveTask;
        await sendCts.CancelAsync();
        await sendTask;

        Assert.Equal(ExitCodes.TrustDenied, receiveExit);
    }
}
