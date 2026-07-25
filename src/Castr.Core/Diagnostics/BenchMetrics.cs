using System.Diagnostics;
using System.Globalization;
using System.Text;

namespace Castr.Core.Diagnostics;

/// <summary>
/// TEMPORARY MEASUREMENT-ONLY INSTRUMENTATION (M7 throughput investigation). Not shipped behavior.
///
/// Entirely inert unless the <c>CASTR_BENCH</c> environment variable is set to an output directory: every hook
/// below short-circuits on <see cref="Enabled"/>, which is a <c>static readonly bool</c> so the JIT elides the
/// call bodies in normal runs. Delete this file (and the call sites tagged <c>BENCH</c>) to remove.
///
/// Env knobs:
///   CASTR_BENCH=&lt;dir&gt;              enable, write &lt;dir&gt;/&lt;tag&gt;-&lt;pid&gt;.json on flush
///   CASTR_BENCH_TAG=&lt;name&gt;         label for the output file (default "run")
///   CASTR_BENCH_SAMPLE_MS=&lt;n&gt;      time-series sample interval (default 100)
///   CASTR_BENCH_PEERHAVE_EVERY=&lt;n&gt; broadcast PEER_HAVE only every n-th verified chunk (0 = never; default 1 = shipped behavior)
///   CASTR_BENCH_MAXDGRAM=&lt;n&gt;       override WirePacketizer.DefaultMaxDatagramPayload
/// </summary>
public static class BenchMetrics
{
    public static readonly string? OutputDirectory = Environment.GetEnvironmentVariable("CASTR_BENCH");
    public static readonly bool Enabled = !string.IsNullOrWhiteSpace(OutputDirectory);
    public static readonly string Tag = Environment.GetEnvironmentVariable("CASTR_BENCH_TAG") ?? "run";

    /// <summary>0 = never broadcast PEER_HAVE, 1 = shipped behavior (every verified chunk), n = every n-th.</summary>
    public static readonly int PeerHaveEvery = ReadInt("CASTR_BENCH_PEERHAVE_EVERY", 1);

    /// <summary>Override for the wire datagram payload target; -1 = leave the shipped default alone.</summary>
    public static readonly int MaxDatagramOverride = ReadInt("CASTR_BENCH_MAXDGRAM", -1);

    /// <summary>Override for UdpMulticastTransport.SocketBufferBytes; -1 = leave the shipped 4 MB alone.</summary>
    public static readonly int SocketBufferOverride = ReadInt("CASTR_BENCH_SOCKBUF", -1);

    private static readonly int SampleMs = ReadInt("CASTR_BENCH_SAMPLE_MS", 100);
    private static readonly Stopwatch Clock = Stopwatch.StartNew();
    private static readonly DateTimeOffset StartUtc = DateTimeOffset.UtcNow;

    // ---- wire accounting, indexed by the MessageType byte at offset 1 of every datagram ----
    private static readonly long[] SentDatagrams = new long[256];
    private static readonly long[] SentBytes = new long[256];
    private static readonly long[] RecvDatagrams = new long[256];
    private static readonly long[] RecvBytes = new long[256];

    // ---- receiver-side per-stage cost (Stopwatch ticks, accumulated) ----
    public enum Stage
    {
        GateWait, Reassemble, Decode, ChunkAssemble, MerkleVerify, Decrypt, DiskWrite, PeerHave, ProgressEmit, Bookkeeping,
        RepairPass, HandleOther,
        // sender side
        SenderChunkRead, SenderEncrypt, SenderProof, SenderSocketSend, SenderRepairServe,
    }

    private static readonly long[] StageTicks = new long[24];
    private static readonly long[] StageCount = new long[24];

    // ---- scalar counters ----
    private static long _channelWriteBlocked;   // producer had to await a full bounded channel
    private static long _duplicateChunkPackets; // arrived for a chunk we already hold
    private static long _reassemblyIncomplete;  // fragment offered, message not yet whole
    private static long _peerHaveSuppressed;    // suppressed by PeerHaveEvery
    private static long _repairPasses;
    private static long _repairChunksRequested;
    private static long _repairRequestMessages;
    private static long _chunksVerified;

    private static Func<int>? _inboxDepth;
    private static Func<long>? _verifiedBytes;
    private static int _inboxCapacity;

    private static readonly List<Sample> Samples = [];
    private static readonly List<(double Ms, string Name, long Value)> Marks = [];
    private static readonly object Gate = new();
    private static readonly Dictionary<string, string> Meta = [];
    private static Timer? _sampler;
    private static long _peakInboxDepth;

    private readonly record struct Sample(
        double Ms, long VerifiedBytes, int InboxDepth, long ChunkDatagramsRecv, long PeerHaveDatagramsRecv,
        long ChunkDatagramsSent, long Duplicates, long RepairChunksRequested, double CpuMs,
        double GcPauseMs, int Gen2);

    public static double ElapsedMs => Clock.Elapsed.TotalMilliseconds;

    private static int ReadInt(string name, int fallback) =>
        int.TryParse(Environment.GetEnvironmentVariable(name), out var v) ? v : fallback;

    public static void Meta_(string key, string value)
    {
        if (!Enabled) return;
        lock (Gate) Meta[key] = value;
    }

    /// <summary>Marks a named instant (e.g. "carousel-complete") on the shared timeline.</summary>
    public static void Mark(string name, long value = 0)
    {
        if (!Enabled) return;
        lock (Gate) Marks.Add((Clock.Elapsed.TotalMilliseconds, name, value));
    }

    /// <summary>Starts the time-series sampler. Idempotent.</summary>
    public static void StartSampling(Func<long>? verifiedBytes = null)
    {
        if (!Enabled) return;
        lock (Gate)
        {
            if (verifiedBytes is not null) _verifiedBytes = verifiedBytes;
            _sampler ??= new Timer(_ => TakeSample(), null, 0, SampleMs);
        }
    }

    public static void RegisterInbox(Func<int> depth, int capacity)
    {
        if (!Enabled) return;
        _inboxDepth = depth;
        _inboxCapacity = capacity;
    }

    private static void TakeSample()
    {
        int depth = 0;
        try { depth = _inboxDepth?.Invoke() ?? 0; } catch { /* transport disposed mid-sample */ }
        long verified = 0;
        try { verified = _verifiedBytes?.Invoke() ?? 0; } catch { }
        if (depth > Interlocked.Read(ref _peakInboxDepth))
            Interlocked.Exchange(ref _peakInboxDepth, depth);

        long chunkRecv = Volatile.Read(ref RecvDatagrams[(int)MessageTypeTag.ChunkData])
            + Volatile.Read(ref RecvDatagrams[(int)MessageTypeTag.ChunkPacket])
            + Volatile.Read(ref RecvDatagrams[(int)MessageTypeTag.ChunkResponse]);
        // PEER_HAVE at this chunk count exceeds the 1200-byte datagram budget, so it travels as
        // PacketFragment datagrams; on the receiver's own socket that is what its feedback shows up as.
        long peerHaveRecv = Volatile.Read(ref RecvDatagrams[(int)MessageTypeTag.PeerHave])
            + Volatile.Read(ref RecvDatagrams[(int)MessageTypeTag.PacketFragment]);
        long chunkSent = Volatile.Read(ref SentDatagrams[(int)MessageTypeTag.ChunkData])
            + Volatile.Read(ref SentDatagrams[(int)MessageTypeTag.ChunkPacket])
            + Volatile.Read(ref SentDatagrams[(int)MessageTypeTag.ChunkResponse]);
        double cpuMs = 0;
        try { cpuMs = System.Diagnostics.Process.GetCurrentProcess().TotalProcessorTime.TotalMilliseconds; } catch { }

        lock (Gate)
            Samples.Add(new Sample(
                Clock.Elapsed.TotalMilliseconds, verified, depth, chunkRecv, peerHaveRecv, chunkSent,
                Interlocked.Read(ref _duplicateChunkPackets), Interlocked.Read(ref _repairChunksRequested), cpuMs,
                GC.GetTotalPauseDuration().TotalMilliseconds, GC.CollectionCount(2)));
    }

    // ---- wire hooks (called from UdpMulticastTransport) ----

    public static void OnDatagramSent(ReadOnlySpan<byte> datagram)
    {
        if (!Enabled) return;
        int t = datagram.Length >= 2 ? datagram[1] : 255;
        Interlocked.Increment(ref SentDatagrams[t]);
        Interlocked.Add(ref SentBytes[t], datagram.Length);
    }

    public static void OnDatagramReceived(ReadOnlySpan<byte> datagram)
    {
        if (!Enabled) return;
        int t = datagram.Length >= 2 ? datagram[1] : 255;
        Interlocked.Increment(ref RecvDatagrams[t]);
        Interlocked.Add(ref RecvBytes[t], datagram.Length);
    }

    public static void OnChannelWriteBlocked() { if (Enabled) Interlocked.Increment(ref _channelWriteBlocked); }
    public static void OnDuplicateChunkPacket() { if (Enabled) Interlocked.Increment(ref _duplicateChunkPackets); }
    public static void OnReassemblyIncomplete() { if (Enabled) Interlocked.Increment(ref _reassemblyIncomplete); }
    public static void OnPeerHaveSuppressed() { if (Enabled) Interlocked.Increment(ref _peerHaveSuppressed); }
    public static void OnChunkVerified() { if (Enabled) Interlocked.Increment(ref _chunksVerified); }

    public static void OnRepairPass(long chunksRequested, long requestMessages)
    {
        if (!Enabled) return;
        Interlocked.Increment(ref _repairPasses);
        Interlocked.Add(ref _repairChunksRequested, chunksRequested);
        Interlocked.Add(ref _repairRequestMessages, requestMessages);
    }

    /// <summary>Accumulates elapsed ticks against a stage. Cheap enough (two interlocked adds) for a per-packet hook.</summary>
    public static void AddStage(Stage stage, long startTicks)
    {
        if (!Enabled) return;
        Interlocked.Add(ref StageTicks[(int)stage], Stopwatch.GetTimestamp() - startTicks);
        Interlocked.Increment(ref StageCount[(int)stage]);
    }

    public static long Now() => Enabled ? Stopwatch.GetTimestamp() : 0;

    /// <summary>Writes the collected report as JSON. Safe to call more than once.</summary>
    public static void Flush()
    {
        if (!Enabled) return;
        try
        {
            _sampler?.Dispose();
            TakeSample();
            Directory.CreateDirectory(OutputDirectory!);
            var path = Path.Combine(OutputDirectory!, $"{Tag}-{Environment.ProcessId}.json");
            File.WriteAllText(path, BuildJson(), new UTF8Encoding(false));
        }
        catch { /* measurement must never break the run */ }
    }

    private static string BuildJson()
    {
        var sb = new StringBuilder();
        sb.Append("{\n");
        sb.Append($"  \"tag\": \"{Tag}\",\n  \"pid\": {Environment.ProcessId},\n");
        sb.Append($"  \"startUtc\": \"{StartUtc:O}\",\n  \"elapsedMs\": {F(Clock.Elapsed.TotalMilliseconds)},\n");
        sb.Append($"  \"stopwatchFrequency\": {Stopwatch.Frequency},\n");
        sb.Append($"  \"peerHaveEvery\": {PeerHaveEvery},\n  \"maxDatagramOverride\": {MaxDatagramOverride},\n");
        sb.Append($"  \"inboxCapacity\": {_inboxCapacity},\n  \"peakInboxDepth\": {Interlocked.Read(ref _peakInboxDepth)},\n");
        sb.Append($"  \"channelWriteBlocked\": {_channelWriteBlocked},\n");
        sb.Append($"  \"duplicateChunkPackets\": {_duplicateChunkPackets},\n");
        sb.Append($"  \"reassemblyIncomplete\": {_reassemblyIncomplete},\n");
        sb.Append($"  \"peerHaveSuppressed\": {_peerHaveSuppressed},\n");
        sb.Append($"  \"chunksVerified\": {_chunksVerified},\n");
        sb.Append($"  \"repairPasses\": {_repairPasses},\n");
        sb.Append($"  \"repairChunksRequested\": {_repairChunksRequested},\n");
        sb.Append($"  \"repairRequestMessages\": {_repairRequestMessages},\n");
        double totalCpuMs = 0, userCpuMs = 0;
        try
        {
            using var p = Process.GetCurrentProcess();
            totalCpuMs = p.TotalProcessorTime.TotalMilliseconds;
            userCpuMs = p.UserProcessorTime.TotalMilliseconds;
        }
        catch { }
        sb.Append($"  \"processCpuMs\": {F(totalCpuMs)},\n  \"processUserCpuMs\": {F(userCpuMs)},\n");
        sb.Append($"  \"gcPauseMs\": {F(GC.GetTotalPauseDuration().TotalMilliseconds)},\n");
        sb.Append($"  \"gcGen0\": {GC.CollectionCount(0)},\n  \"gcGen1\": {GC.CollectionCount(1)},\n  \"gcGen2\": {GC.CollectionCount(2)},\n");
        sb.Append($"  \"gcAllocatedMB\": {F(GC.GetTotalAllocatedBytes() / 1048576.0)},\n");
        sb.Append($"  \"socketBufferOverride\": {SocketBufferOverride},\n");

        lock (Gate)
        {
            sb.Append("  \"meta\": {");
            sb.Append(string.Join(", ", Meta.Select(kv => $"\"{kv.Key}\": \"{kv.Value}\"")));
            sb.Append("},\n");

            sb.Append("  \"marks\": [");
            sb.Append(string.Join(", ", Marks.Select(m => $"[{F(m.Ms)}, \"{m.Name}\", {m.Value}]")));
            sb.Append("],\n");

            sb.Append("  \"sent\": {");
            sb.Append(string.Join(", ", TypeIndices(SentDatagrams).Select(i =>
                $"\"{NameOf(i)}\": [{SentDatagrams[i]}, {SentBytes[i]}]")));
            sb.Append("},\n");

            sb.Append("  \"recv\": {");
            sb.Append(string.Join(", ", TypeIndices(RecvDatagrams).Select(i =>
                $"\"{NameOf(i)}\": [{RecvDatagrams[i]}, {RecvBytes[i]}]")));
            sb.Append("},\n");

            sb.Append("  \"stages\": {");
            sb.Append(string.Join(", ", Enumerable.Range(0, StageTicks.Length)
                .Where(i => StageCount[i] > 0)
                .Select(i => $"\"{(Stage)i}\": [{StageTicks[i]}, {StageCount[i]}]")));
            sb.Append("},\n");

            sb.Append("  \"seriesColumns\": [\"ms\", \"verifiedBytes\", \"inboxDepth\", \"chunkDgramRecv\", \"peerHaveDgramRecv\", \"chunkDgramSent\", \"duplicates\", \"repairChunksRequested\", \"cpuMs\", \"gcPauseMs\", \"gen2\"],\n");
            sb.Append("  \"series\": [\n");
            sb.Append(string.Join(",\n", Samples.Select(s =>
                $"    [{F(s.Ms)}, {s.VerifiedBytes}, {s.InboxDepth}, {s.ChunkDatagramsRecv}, {s.PeerHaveDatagramsRecv}, {s.ChunkDatagramsSent}, {s.Duplicates}, {s.RepairChunksRequested}, {F(s.CpuMs)}, {F(s.GcPauseMs)}, {s.Gen2}]")));
            sb.Append("\n  ]\n");
        }

        sb.Append("}\n");
        return sb.ToString();
    }

    private static IEnumerable<int> TypeIndices(long[] counts) =>
        Enumerable.Range(0, counts.Length).Where(i => counts[i] > 0);

    private static string F(double v) => v.ToString("0.###", CultureInfo.InvariantCulture);

    private static string NameOf(int tag) =>
        Enum.IsDefined(typeof(MessageTypeTag), (byte)tag) ? ((MessageTypeTag)tag).ToString() : $"tag{tag}";

    /// <summary>
    /// Mirror of <c>Castr.Core.Protocol.MessageType</c>'s byte tags, duplicated here only so this diagnostics
    /// file can classify raw datagrams without a dependency cycle back into the protocol layer.
    /// </summary>
    private enum MessageTypeTag : byte
    {
        Announce = 1, Manifest = 2, ChunkData = 3, PeerHave = 4, ChunkRequest = 5, ChunkResponse = 6,
        TransferComplete = 7, JoinRequest = 8, KeyGrant = 9, PacketFragment = 10, ChunkPacket = 11,
        ManifestRequest = 12, ChunkPullRequest = 13, ChunkPullResponse = 14, KeyUnavailable = 15,
    }
}
