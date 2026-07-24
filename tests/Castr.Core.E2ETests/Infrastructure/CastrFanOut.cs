using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Docker.DotNet.Models;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Configurations;
using DotNet.Testcontainers.Containers;

namespace Castr.Core.E2ETests.Infrastructure;

/// <summary>Outcome of one fan-out run: what each receiver ended up with, plus diagnostics worth asserting/printing.</summary>
public sealed record FanOutResult(
    string ExpectedSha256,
    IReadOnlyList<string> ReceiverSha256,
    IReadOnlyList<string> ReceiverLogs,
    string SenderLog,
    long NetemDroppedPackets);

/// <summary>
/// Drives one real multi-container transfer end-to-end through the shipped <c>castr</c> CLI:
/// generates a sender identity, writes a random payload, starts N receiver containers that pre-trust the
/// sender and listen, then starts the sender. Receivers are started <b>before</b> the sender so they catch
/// the sender's one-shot ANNOUNCE/MANIFEST. Each receiver hashes its received file and prints it; the caller
/// asserts byte-identity against the source hash.
/// </summary>
internal static class CastrFanOut
{
    private static readonly Regex HashLine = new(@"RESULTHASH=([0-9a-fA-F]{64})", RegexOptions.Compiled);

    public static async Task<FanOutResult> RunAsync(
        CastrClusterFixture fixture,
        int receiverCount,
        int port,
        int? lossPercent,
        int payloadBytes,
        TimeSpan completionTimeout,
        CancellationToken ct = default)
    {
        const string group = "239.192.55.55";

        var payload = RandomNumberGenerator.GetBytes(payloadBytes);
        var expectedHash = Convert.ToHexString(SHA256.HashData(payload)).ToLowerInvariant();
        var (identityKey, publicKeyId) = SenderIdentityFactory.Create();

        var receivers = new List<IContainer>();
        IContainer? sender = null;
        try
        {
            // 1. Start every receiver first, each pre-trusting the sender, and wait until it is listening.
            for (int i = 0; i < receiverCount; i++)
                receivers.Add(BuildReceiver(fixture, publicKeyId, group, port));

            await Task.WhenAll(receivers.Select(r => r.StartAsync(ct)));

            // Small settle margin so bridge IGMP membership is fully established before the one-shot manifest.
            await Task.Delay(TimeSpan.FromSeconds(1), ct);

            // 2. Start the sender (reusing the pre-generated identity), optionally behind real netem loss.
            sender = BuildSender(fixture, payload, identityKey, group, port, lossPercent);
            await sender.StartAsync(ct);

            // 3. Wait for each receiver to print its RESULTHASH (printed only after "transfer complete").
            var hashes = await WaitForHashesAsync(receivers, completionTimeout, ct);

            var receiverLogs = new List<string>();
            foreach (var r in receivers)
            {
                var (stdout, _) = await r.GetLogsAsync(timestampsEnabled: false, ct: ct);
                receiverLogs.Add(stdout);
            }

            var (senderLog, _) = await sender.GetLogsAsync(timestampsEnabled: false, ct: ct);
            long dropped = lossPercent is null ? 0 : await ReadNetemDropsAsync(sender, ct);

            return new FanOutResult(expectedHash, hashes, receiverLogs, senderLog, dropped);
        }
        finally
        {
            if (sender is not null)
                await sender.DisposeAsync();
            foreach (var r in receivers)
                await r.DisposeAsync();
        }
    }

    private static IContainer BuildReceiver(CastrClusterFixture fixture, string publicKeyId, string group, int port)
    {
        // No `exec`: the shell must survive `castr receive` so it can hash and print the result afterwards.
        var script =
            $"castr trust add {publicKeyId} --trust-store /t/trust.json && " +
            $"castr receive --dest-dir /dst --trust-store /t/trust.json " +
            $"--group {group} --port {port} --interface eth0 --on-unknown-sender deny && " +
            "echo RESULTHASH=$(sha256sum /dst/payload.bin | cut -d' ' -f1)";

        return new ContainerBuilder(fixture.Image)
            .WithNetwork(fixture.Network)
            .WithEntrypoint("/bin/sh")
            .WithCommand("-c", script)
            .WithWaitStrategy(Wait.ForUnixContainer().UntilMessageIsLogged("listening on"))
            .Build();
    }

    private static IContainer BuildSender(
        CastrClusterFixture fixture, byte[] payload, byte[] identityKey, string group, int port, int? lossPercent)
    {
        var builder = new ContainerBuilder(fixture.Image)
            .WithNetwork(fixture.Network)
            .WithResourceMapping(payload, "/data/payload.bin", ReadableFileMode)
            .WithResourceMapping(identityKey, "/data/identity.key", ReadableFileMode)
            .WithWaitStrategy(Wait.ForUnixContainer().UntilMessageIsLogged("serving"));

        if (lossPercent is null)
        {
            return builder
                .WithCommand(
                    "send", "/data/payload.bin", "--identity", "/data/identity.key",
                    "--group", group, "--port", port.ToString(), "--interface", "eth0")
                .Build();
        }

        // Induce real kernel-level loss on the sender's egress, targeting only large (chunk-carrying) datagrams
        // while sparing small control traffic. Since M3, Castr.Core packetizes every chunk whose encrypted
        // envelope exceeds the wire-packet budget into MTU-safe wire packets (default 1200-byte payload =>
        // ~1228-byte IP datagrams), so chunk traffic is NO LONGER IP-fragmented and the old "match the first IP
        // fragment" filter would match nothing. This is true even for the CLI's 8 KB default chunk: at the
        // default 1200-byte budget an 8 KB chunk's ~8208-byte ciphertext does not fit one datagram, so it ships
        // as ~8 ChunkPacket datagrams of <=1200 bytes each (M3 QA verified this over real loopback UDP: the
        // largest datagram observed was exactly 1200 bytes; see the QA report). We match on the IP total-length
        // field (offset 2): 0x0400/0xfc00 selects lengths in [1024, 2047], which covers the MTU-sized chunk
        // packets but leaves the much smaller ANNOUNCE/MANIFEST/KEY_GRANT/CHUNK_REQUEST/PEER_HAVE control
        // datagrams (< 1024 bytes) untouched. Sparing control matters because the manifest is broadcast exactly
        // once with no re-request path — losing it would stall rather than exercise repair.
        // NOTE (M3 QA): this filter change has not been run against Docker in-loop; verify netem still drops
        // chunk datagrams (result.NetemDroppedPackets > 0) on a machine with the E2E tier enabled. If a run ever
        // observes chunk-sized *IP fragments* (total length ~1500) rather than <=1228-byte wire packets, the
        // baked container image is a STALE pre-packetization build — rebuild it (the fixture caches the image
        // and reuses a temp publish dir), as current Castr.Core no longer IP-fragments chunk traffic.
        var script =
            "tc qdisc add dev eth0 root handle 1: prio && " +
            $"tc qdisc add dev eth0 parent 1:3 handle 30: netem loss {lossPercent}% && " +
            "tc filter add dev eth0 parent 1:0 protocol ip u32 match u16 0x0400 0xfc00 at 2 flowid 1:3 && " +
            "exec /opt/castr/castr send /data/payload.bin --identity /data/identity.key " +
            $"--group {group} --port {port} --interface eth0";

        return builder
            .WithEntrypoint("/bin/sh")
            .WithCommand("-c", script)
            .WithCreateParameterModifier(p =>
            {
                p.HostConfig ??= new HostConfig();
                p.HostConfig.CapAdd = new List<string> { "NET_ADMIN" };
            })
            .Build();
    }

    private static async Task<IReadOnlyList<string>> WaitForHashesAsync(
        IReadOnlyList<IContainer> receivers, TimeSpan timeout, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow + timeout;
        var hashes = new string?[receivers.Count];

        while (DateTime.UtcNow < deadline)
        {
            bool allDone = true;
            for (int i = 0; i < receivers.Count; i++)
            {
                if (hashes[i] is not null)
                    continue;

                var (stdout, _) = await receivers[i].GetLogsAsync(timestampsEnabled: false, ct: ct);
                var match = HashLine.Match(stdout);
                if (match.Success)
                    hashes[i] = match.Groups[1].Value.ToLowerInvariant();
                else
                    allDone = false;
            }

            if (allDone)
                return hashes!;

            await Task.Delay(TimeSpan.FromSeconds(1), ct);
        }

        // Timed out: surface which receivers never completed, with their last log lines, for a useful failure.
        var report = new StringBuilder($"Not all receivers completed within {timeout.TotalSeconds:0}s.");
        for (int i = 0; i < receivers.Count; i++)
        {
            if (hashes[i] is not null)
                continue;
            var (stdout, _) = await receivers[i].GetLogsAsync(timestampsEnabled: false, ct: ct);
            report.Append($"\n--- receiver {i} (incomplete) tail ---\n{Tail(stdout, 6)}");
        }
        throw new TimeoutException(report.ToString());
    }

    private static async Task<long> ReadNetemDropsAsync(IContainer sender, CancellationToken ct)
    {
        try
        {
            var result = await sender.ExecAsync(new[] { "tc", "-s", "qdisc", "show", "dev", "eth0" }, ct);
            var match = Regex.Match(result.Stdout, @"netem[\s\S]*?dropped (\d+)");
            return match.Success ? long.Parse(match.Groups[1].Value) : 0;
        }
        catch
        {
            return 0;
        }
    }

    private static string Tail(string text, int lines)
    {
        var all = text.Replace("\r", "").Split('\n', StringSplitOptions.RemoveEmptyEntries);
        return string.Join('\n', all.TakeLast(lines));
    }

    // Testcontainers' byte[] resource-mapping overload takes the POSIX mode as a uint; 0644 == rw-r--r--.
    private const uint ReadableFileMode = 420; // 0o644
}
