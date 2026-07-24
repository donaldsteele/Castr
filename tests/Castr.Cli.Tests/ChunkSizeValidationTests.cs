using System.Net;
using Castr.Cli;
using Spectre.Console.Testing;

namespace Castr.Cli.Tests;

/// <summary>
/// Covers the fail-fast validation of <c>--chunk-size</c> in <see cref="SendRunner"/>. As of M3, Castr.Core
/// packetizes each encrypted chunk into MTU-safe wire packets, so the old ~65 KB single-datagram ceiling is
/// gone; the remaining cap (<see cref="CastrPaths.MaxChunkSize"/>) is a memory-safety bound on chunk
/// reassembly. The check still runs upfront, before any file, socket, or session state is touched.
/// </summary>
public class ChunkSizeValidationTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "castr-cli-chunksize", Guid.NewGuid().ToString("N"));

    public ChunkSizeValidationTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    private static SendOptions Options(string filePath, int chunkSize, string identityPath) =>
        new(filePath, IPAddress.Parse("239.192.55.220"), 45220, InterfaceName: null, chunkSize, identityPath, UseTui: false);

    [Fact]
    public async Task DefaultChunkSize_IsAcceptedAndPassesValidation()
    {
        // The existing 8192 default must keep working: pre-cancel the token so RunAsync proceeds past the
        // chunk-size gate and the (real) file-exists/identity/prepare steps, then observes cancellation
        // during preparation, rather than being rejected outright by the new check.
        var srcPath = Path.Combine(_dir, "payload.bin");
        await File.WriteAllBytesAsync(srcPath, new byte[1024]);
        var identityPath = Path.Combine(_dir, "identity.key");

        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        var exit = await SendRunner.RunAsync(
            Options(srcPath, CastrPaths.DefaultChunkSize, identityPath), new TestConsole(), cts.Token);

        Assert.Equal(ExitCodes.Success, exit); // cancellation, not InvalidInput => it cleared the chunk-size gate
    }

    [Fact]
    public async Task ChunkSizeAtMax_IsAccepted()
    {
        var srcPath = Path.Combine(_dir, "payload.bin");
        await File.WriteAllBytesAsync(srcPath, new byte[1024]);
        var identityPath = Path.Combine(_dir, "identity.key");

        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        var exit = await SendRunner.RunAsync(
            Options(srcPath, CastrPaths.MaxChunkSize, identityPath), new TestConsole(), cts.Token);

        Assert.Equal(ExitCodes.Success, exit);
    }

    [Fact]
    public async Task DocumentedLargeChunkSize_IsNowAccepted()
    {
        // 256 KB is the documented default hash/repair chunk size (wiki/concepts/wire-protocol.md) and was
        // rejected outright pre-M3. Packetization makes it safe again, so it must now clear the gate.
        var srcPath = Path.Combine(_dir, "payload.bin");
        await File.WriteAllBytesAsync(srcPath, new byte[1024]);
        var identityPath = Path.Combine(_dir, "identity.key");

        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        var exit = await SendRunner.RunAsync(
            Options(srcPath, 262_144, identityPath), new TestConsole(), cts.Token);

        Assert.Equal(ExitCodes.Success, exit); // cancellation, not InvalidInput => it cleared the chunk-size gate
    }

    [Fact]
    public async Task ChunkSizeOverMax_IsRejected_WithExitCode5AndLimitMessage()
    {
        // A nonexistent source file and identity path: if validation ran after any real work, later steps
        // would fail/throw for unrelated reasons instead of cleanly returning InvalidInput.
        var srcPath = Path.Combine(_dir, "does-not-exist.bin");
        var identityPath = Path.Combine(_dir, "identity.key");
        var console = new TestConsole();
        int overMax = CastrPaths.MaxChunkSize + 1_000_000;

        var exit = await SendRunner.RunAsync(
            Options(srcPath, overMax, identityPath), console, CancellationToken.None);

        Assert.Equal(ExitCodes.InvalidInput, exit);
        Assert.Contains(overMax.ToString(), console.Output);
        Assert.Contains(CastrPaths.MaxChunkSize.ToString(), console.Output);
    }

    [Fact]
    public async Task ChunkSizeOverMax_RejectedBeforeAnyFileOrSocketActivity()
    {
        var srcPath = Path.Combine(_dir, "does-not-exist.bin");
        var identityPath = Path.Combine(_dir, "identity.key"); // never created if we bail out first

        var exit = await SendRunner.RunAsync(
            Options(srcPath, CastrPaths.MaxChunkSize + 1, identityPath), new TestConsole(), CancellationToken.None);

        Assert.Equal(ExitCodes.InvalidInput, exit);
        Assert.False(File.Exists(identityPath)); // SenderIdentity.LoadOrCreate never ran
    }
}
