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

        // Induce real kernel-level loss on the sender's egress, targeting the data-carrying message types while
        // sparing control traffic. Sparing control matters because the manifest is broadcast exactly once with
        // no re-request path — losing it would stall the transfer rather than exercise repair.
        //
        // Selected by Castr MESSAGE TYPE, not by datagram size (M11). The size filter this replaced —
        // `match u16 0x0400 0xfc00 at 2`, i.e. IP total length in [1024, 2047] — was written against a
        // 1200-byte datagram budget and 8 KiB chunks, and both of those defaults have since moved (1472 and
        // 256 KiB). Two things were wrong with it by the time this was revisited:
        //
        //   * It spared every datagram under 1024 bytes, which is control traffic by design but also the SHORT
        //     TAIL PACKET each chunk ends with — 228 payload bytes at the shipped 256 KiB/1472 pair. One packet
        //     in every 184 was therefore undroppable, so the loss the receivers actually saw was neither the
        //     configured rate nor uniform across a chunk.
        //   * It dropped any large control datagram, including the PacketFragment slices a big manifest travels
        //     as — the exact traffic the comment claimed to be protecting. Invisible only because this fixture's
        //     manifests describe one small file.
        //
        // A Castr datagram is [FormatVersion:1][MessageType:1][body], so the type tag sits at a fixed offset:
        // 20 (IP header, no options on a container veth) + 8 (UDP) + 1 = 29. Types 3 (CHUNK_DATA), 6
        // (CHUNK_RESPONSE) and 11 (CHUNK_PACKET) are the sender's payload-bearing messages; CHUNK_RESPONSE is
        // included deliberately so repair traffic is lossy too, which is what makes convergence a real result
        // rather than a first-try success. Everything else — ANNOUNCE, MANIFEST, PEER_HAVE, CHUNK_REQUEST,
        // JOIN_REQUEST, KEY_GRANT, PACKET_FRAGMENT — passes untouched at any size.
        //
        // Verified in-loop against Docker when it landed (the previous filter carried an M3-era note saying it
        // never had been): all three fan-out arms green with NetemDroppedPackets > 0 and every receiver's hash
        // byte-identical.
        int[] dataMessageTypes = [3, 6, 11]; // CHUNK_DATA, CHUNK_RESPONSE, CHUNK_PACKET
        var typeFilters = string.Join(" && ", dataMessageTypes.Select(type =>
            "tc filter add dev eth0 parent 1:0 protocol ip u32 " +
            $"match ip protocol 17 0xff match u8 {type} 0xff at 29 flowid 1:3"));

        var script =
            "tc qdisc add dev eth0 root handle 1: prio && " +
            $"tc qdisc add dev eth0 parent 1:3 handle 30: netem loss {lossPercent}% && " +
            typeFilters + " && " +
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
