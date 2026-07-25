using Castr.Core.Time;
using Castr.Core.Transport;

namespace Castr.Core.Protocol;

public sealed record RepairRequestPlan(Endpoint Target, int FileIndex, int[] ChunkIndices, byte[] RequestNonce);

/// <param name="RequestTimeout">
/// Base time a requested chunk stays "in flight" before it may be re-requested. Grown per chunk by
/// <paramref name="MaxBackoffDoublings"/>, spread by <paramref name="RetryJitterFraction"/>, then clamped by
/// <paramref name="MaxRequestTimeout"/>.
/// </param>
/// <param name="MaxChunksPerRequest">
/// Hard cap on how many chunk indices one <see cref="ChunkRequestMessage"/> may carry, so every repair request
/// fits in a single datagram. See <see cref="MaxChunksPerRequestFor"/> for how the default is derived and why a
/// cap is a correctness matter rather than a tuning knob. This bounds one <i>datagram</i>, not one pass — see
/// <paramref name="MaxRequestsPerPass"/> for the amplification bound.
/// </param>
/// <param name="MaxRequestsPerPass">
/// Hard cap on how many <see cref="ChunkRequestMessage"/>s one repair pass may emit across all files. This — not
/// <paramref name="MaxChunksPerRequest"/> — is the actual amplification bound: without it a cold-start pass emits
/// one single-datagram request per <paramref name="MaxChunksPerRequest"/> chunks missing (~38 of them on an
/// 80 MiB transfer) and the sender still re-serves every chunk in all of them. It doubles as crude pacing, as a
/// bound on a hostile receiver's outbound burst, and as a bound on how long <see cref="PlanRepairs"/> can block
/// packet processing (it runs under the receiver's state gate, and <see cref="IPeerTable.GetPeersWithChunk"/> is
/// O(peers x bitmap bytes) per missing index).
/// </param>
/// <param name="MaxBackoffDoublings">
/// Ceiling on the exponential backoff exponent: the per-chunk timeout grows as
/// <c>RequestTimeout * 2^min(attempts-1, MaxBackoffDoublings)</c>, is then clamped to
/// <paramref name="MaxRequestTimeout"/>, and only then jittered. The default of 2 gives a reachable ladder of
/// 5 s, 10 s, 20 s — see <see cref="RepairOptions.DefaultMaxBackoffDoublings"/> for why a larger value is dead
/// configuration at the shipped clamp.
/// </param>
/// <param name="MaxRequestTimeout">
/// Wall-clock clamp on the <b>backed-off</b> per-chunk timeout. Note it bounds the backoff ladder, not the final
/// value: <see cref="RetryJitterFraction"/> is applied <i>after</i> this clamp (deliberately — clamping last
/// would collapse every receiver onto exactly this value in lockstep), so the true ceiling on an effective
/// timeout is <c>MaxRequestTimeout * (1 + RetryJitterFraction)</c>, i.e. 25 s at the shipped defaults rather than
/// 20 s. The backoff exists to stop hammering
/// a chunk nobody can supply, but a timeout equally often means "my request was dropped" — the very failure P0
/// exists to make cheap — so an unclamped 16x growth would turn a few unlucky drops into minute-scale silence on
/// a chunk a peer could have served immediately. Note the backoff is only safe to grow at all <i>because</i> the
/// sender-side datagram filter (see <see cref="DatagramFilters"/>) ships alongside it: before that filter,
/// CHUNK_REQUEST loss at the sender was measured above 90%, so "timed out" was overwhelmingly "was never heard"
/// rather than "cannot be served", and backing off would have been close to giving up. Null uses
/// <see cref="DefaultMaxRequestTimeout"/>.
/// </param>
/// <param name="RetryJitterFraction">
/// Fraction of a chunk's effective timeout drawn at random (per chunk, redrawn on every confirmed request) and
/// added to it, so retries de-synchronize instead of every receiver re-asking on the same grid. Together with
/// <paramref name="InitialRequestJitter"/> this is the randomized jitter + exponential backoff
/// wiki/concepts/repair-protocol.md specifies. 0 disables it.
/// </param>
/// <param name="InitialRequestJitter">
/// Maximum randomized wall-clock delay between a chunk first becoming eligible for repair and its first request.
/// This is the thundering-herd suppression the wiki names: many receivers notice the same lost chunk at the same
/// moment (when the carousel passes it), and without a spread they all ask at once. Drawn per chunk from the
/// injected <see cref="Random"/> and measured on the injected <see cref="ISystemClock"/>, so it is deterministic
/// under test and — unlike counting repair passes — carries no assumption about the caller's repair period.
/// <see cref="TimeSpan.Zero"/> disables it; null uses <see cref="DefaultInitialRequestJitter"/>.
/// </param>
public sealed record RepairOptions(
    TimeSpan RequestTimeout,
    int MaxChunksPerRequest = RepairOptions.DefaultMaxChunksPerRequest,
    int MaxRequestsPerPass = RepairOptions.DefaultMaxRequestsPerPass,
    int MaxBackoffDoublings = RepairOptions.DefaultMaxBackoffDoublings,
    TimeSpan? MaxRequestTimeout = null,
    double RetryJitterFraction = RepairOptions.DefaultRetryJitterFraction,
    TimeSpan? InitialRequestJitter = null)
{
    /// <summary>
    /// Fixed bytes a <see cref="ChunkRequestMessage"/> encodes around its index array, with an empty
    /// <c>ReturnHost</c>: FormatVersion(1) + MessageType(1) + SessionId(16) + RequesterId(16) + RequestNonce(16)
    /// + FileIndex(4) + index-array count prefix(4) + ReturnHost length prefix(2) + ReturnPort(4) = 64.
    /// Mirrors <see cref="MessageCodec"/>'s ChunkRequest case exactly.
    /// </summary>
    internal const int ChunkRequestEnvelopeOverhead = 1 + 1 + 16 + 16 + 16 + 4 + 4 + 2 + 4;

    /// <summary>
    /// Bytes held back from the index array as headroom. Covers a future non-empty <c>ReturnHost</c> (today the
    /// multicast MVP always sends <c>""</c>, but the field exists and an IPv4 literal is up to 15 bytes, an IPv6
    /// one up to 45) plus slack, so the cap does not sit exactly on the datagram boundary.
    /// </summary>
    internal const int ChunkRequestHeadroom = 64;

    /// <summary>
    /// Largest index count that keeps a <see cref="ChunkRequestMessage"/> inside
    /// <see cref="WirePacketizer.DefaultMaxDatagramPayload"/> — 268 at the shipped 1200-byte budget, against a
    /// measured true single-datagram maximum of 284.
    ///
    /// <para><b>Re-validated at the 256 KiB default and deliberately unchanged.</b> Every term in this derivation
    /// — the datagram budget and <see cref="MessageCodec"/>'s ChunkRequest encoding — is independent of chunk
    /// size, so the bound it computes is exactly as correct at 256 KiB as at 8 KiB. It is a <i>fragmentation</i>
    /// bound, not a tuning knob and not an amplification bound (see <see cref="DefaultMaxRequestsPerPass"/>), so
    /// the fact that 268 indices now covers most of a small file is not a defect: covering the file is what a
    /// receiver that genuinely lost the file should ask for, and the watermark decides whether it may.</para>
    ///
    /// <para><b>One consequence that did change by 32x, recorded because it is not obvious.</b> The cap is
    /// denominated in indices, so the <i>data</i> one request datagram can command grew with the chunk size:
    /// 268 x 256 KiB = <b>67 MB</b> served from a single inbound datagram, where at 8 KiB it was 2.2 MB. Castr's
    /// own receivers are bounded by <see cref="DefaultMaxRequestsPerPass"/>, but a hostile on-segment peer is not
    /// — it can simply emit request datagrams. The principled fix is a byte-denominated serve cap on the sender
    /// rather than an index-denominated one; it is not made here because that changes sender behavior under load
    /// and belongs with the repair-response-deduplication work, not with a default-value change.</para>
    /// </summary>
    public const int DefaultMaxChunksPerRequest =
        (WirePacketizer.DefaultMaxDatagramPayload - ChunkRequestEnvelopeOverhead - ChunkRequestHeadroom) / sizeof(int);

    /// <summary>
    /// Four requests per pass.
    ///
    /// <para>Derivation, so this is not a bare number: repair capacity is
    /// <c>MaxRequestsPerPass x MaxChunksPerRequest x passes/second</c>. At the shipped values and the CLI's 250 ms
    /// repair period that is <c>4 x 268 x 4 = 4,288 chunks/s</c>. <b>At the M8 default chunk size of 256 KiB that
    /// is ~1.1 GB/s of nominal repair traffic</b> against a wire that real two-process measurement clocks at
    /// 12-17 MB/s (see <c>docs/benchmarks/throughput-runs.md</c>) — so the cap is loose by roughly <i>two</i>
    /// orders of magnitude, where at the old 8 KiB default it was loose by one.</para>
    ///
    /// <para><b>⚠️ Be explicit about this: at 256 KiB one of M7's own invariants has regressed.</b> This cap was
    /// added in M7 round 2 precisely so that amplification had a bound that <i>did not depend on the carousel
    /// watermark being right</i> — before it, any false-idle in the valve restored the full repair storm,
    /// self-reinforcingly. That guarantee no longer holds for ordinary files: <c>4 x 268 = 1,072</c> chunks per
    /// pass exceeds the entire chunk count of any file below <b>268 MB</b> at 256 KiB, so for such a file the cap
    /// cannot bind at all and <b>the watermark is once again the only thing standing between the system and a
    /// full-file repair storm.</b> The trade is reduced probability, increased consequence: the measured
    /// watermark margin is 36x with zero false-idle events in 16 instrumented runs (see
    /// <see cref="ReceiverSession.DefaultCarouselIdleThreshold"/>), but the per-pass blast radius went from
    /// ~8.8 MB to a whole file. The real repair is to denominate this and
    /// <see cref="MaxChunksPerRequest"/> in <i>bytes</i> rather than counts, which makes both chunk-size-
    /// independent by construction; that is tracked in wiki/synthesis/roadmap.md and is deliberately not a
    /// smaller count, which would throttle genuine recovery without fixing the denomination.</para>
    ///
    /// <para><b>What it does still bound</b>, and these are now its primary justification:</para>
    /// <list type="number">
    /// <item>work done under the receiver's state gate — <see cref="PlanRepairs"/> considers at most
    /// <c>budget x MaxChunksPerRequest</c> candidate indices per pass, and
    /// <see cref="IPeerTable.GetPeersWithChunk"/> is O(peers x bitmap bytes) <i>per index</i>;</item>
    /// <item>a hostile or malfunctioning receiver's outbound burst, which is 4 datagrams per pass regardless of
    /// chunk size;</item>
    /// <item>amplification for files above ~268 MB, where it binds again.</item>
    /// </list>
    ///
    /// <para>Deliberately <b>not</b> tightened for the larger chunk. A cap tight enough to bind during normal
    /// operation would throttle genuine repair and risk the liveness problems this area has already produced
    /// three times, and the measurement that would justify tightening it — repair traffic actually competing with
    /// the carousel — has not been taken. On a lossless 100 MB transfer at 256 KiB a passive sniffer counted
    /// <b>2 CHUNK_REQUESTs in the whole transfer</b> and 1.05x wire amplification, i.e. this cap is nowhere near
    /// being approached in practice.</para>
    ///
    /// <para>Note the cap is denominated in <i>requests</i>, not chunks or bytes, so its real wire cost scales with
    /// <see cref="MaxChunksPerRequest"/> <i>and</i> with the chunk size: raising either raises this cap's effective
    /// byte throughput without the number changing. That is exactly why the 256 KiB default moved it from "loose"
    /// to "not binding", without a single character of it changing.</para>
    /// </summary>
    public const int DefaultMaxRequestsPerPass = 4;

    /// <summary>
    /// Two doublings, giving a reachable ladder of <b>5 s, 10 s, 20 s</b> at the shipped
    /// <see cref="Default"/> timeout and <see cref="DefaultMaxRequestTimeout"/>.
    ///
    /// <para>Deliberately 2 rather than 4: with a 5 s base and a 20 s backoff clamp, a third doubling (40 s) and
    /// a fourth (80 s) are both unreachable — they would be clamped straight back to 20 s. Shipping 4 meant the
    /// docs described a 16x ladder while the real one was 5, 10, 20, 20, 20, i.e. two dead configuration steps and
    /// a comment that overstated the behavior. The clamp, not this exponent, is the real limiter; raising
    /// <see cref="MaxRequestTimeout"/> is what unlocks further doublings, and a caller that does so should raise
    /// this in step.</para>
    /// </summary>
    public const int DefaultMaxBackoffDoublings = 2;

    public const double DefaultRetryJitterFraction = 0.25;

    public static readonly TimeSpan DefaultMaxRequestTimeout = TimeSpan.FromSeconds(20);

    public static readonly TimeSpan DefaultInitialRequestJitter = TimeSpan.FromMilliseconds(500);

    public static RepairOptions Default => new(TimeSpan.FromSeconds(5));

    /// <summary>Backoff clamp actually in force (falls back to <see cref="DefaultMaxRequestTimeout"/>). Applied before jitter — see <see cref="MaxRequestTimeout"/>.</summary>
    public TimeSpan EffectiveMaxRequestTimeout => MaxRequestTimeout ?? DefaultMaxRequestTimeout;

    /// <summary>First-request jitter actually in force (falls back to <see cref="DefaultInitialRequestJitter"/>).</summary>
    public TimeSpan EffectiveInitialRequestJitter => InitialRequestJitter ?? DefaultInitialRequestJitter;

    /// <summary>
    /// Largest number of chunk indices that fits in one <paramref name="maxDatagramPayload"/>-byte datagram once
    /// <see cref="MessageCodec"/>'s <see cref="ChunkRequestMessage"/> envelope and
    /// <see cref="ChunkRequestHeadroom"/> are accounted for.
    ///
    /// <para>Keeping every request to one datagram is a correctness property, not an optimization: a request
    /// larger than the budget is split by <see cref="WirePacketizer"/> into <see cref="PacketFragmentMessage"/>
    /// datagrams that reassemble <b>all-or-nothing</b>. An uncapped cold-start pass on an 80 MiB transfer
    /// produced a ~39.7 KB request spread over ~35 fragments, and losing any single one of them cost a full
    /// <see cref="RequestTimeout"/> of silence — which is why the observed stall quantum was the timeout rather
    /// than the caller's repair period.</para>
    /// </summary>
    public static int MaxChunksPerRequestFor(int maxDatagramPayload) =>
        Math.Max(1, (maxDatagramPayload - ChunkRequestEnvelopeOverhead - ChunkRequestHeadroom) / sizeof(int));
}

/// <summary>
/// Pure repair-planning logic: given the current peer table and which chunks are still missing, decides
/// who to ask for what. Never touches a transport directly — the caller (ReceiverSession) is responsible
/// for actually sending the CHUNK_REQUEST messages this returns, for calling <see cref="MarkRequested"/>
/// once a request is confirmed on the wire, and for calling <see cref="MarkFulfilled"/> when a response
/// arrives. See wiki/concepts/repair-protocol.md for the ranking rationale.
/// </summary>
public sealed class RepairCoordinator(IPeerTable peerTable, ISystemClock clock, RepairOptions? options = null, Random? random = null)
{
    private readonly RepairOptions _options = options ?? RepairOptions.Default;
    private readonly Random _random = random ?? Random.Shared;
    private readonly Dictionary<(int File, int Chunk), DateTimeOffset> _pending = [];

    // Per-chunk request attempt counts, the input to the exponential backoff in EffectiveTimeout. Kept
    // separately from _pending because _pending entries expire (that is what makes a retry possible) while the
    // attempt history must survive expiry — otherwise the backoff resets every round and never grows.
    private readonly Dictionary<(int File, int Chunk), int> _attempts = [];

    // Per-chunk retry-jitter multiplier in [1, 1 + RetryJitterFraction), redrawn on every confirmed request so
    // two receivers that lost the same chunk at the same instant do not retry in lockstep forever.
    private readonly Dictionary<(int File, int Chunk), double> _retryJitter = [];

    // Per-chunk earliest-first-request time: when a chunk is first seen as eligible it is deferred by a random
    // slice of InitialRequestJitter rather than asked for immediately. This is the thundering-herd spread the
    // wiki specifies. Unlike counting repair passes it is real wall-clock, it applies at the moment the herd
    // actually converges (when the carousel passes a lost chunk, mid-transfer, long after any startup window),
    // and it assumes nothing about how often the caller runs a pass.
    private readonly Dictionary<(int File, int Chunk), DateTimeOffset> _firstRequestNotBefore = [];

    /// <summary>Cap on indices per <see cref="ChunkRequestMessage"/> — see <see cref="RepairOptions.MaxChunksPerRequestFor"/>.</summary>
    public int MaxChunksPerRequest => _options.MaxChunksPerRequest;

    /// <summary>Cap on requests emitted per repair pass — see <see cref="RepairOptions.MaxRequestsPerPass"/>.</summary>
    public int MaxRequestsPerPass => _options.MaxRequestsPerPass;

    /// <summary>
    /// Plans requests for <paramref name="missingChunkIndices"/> that are neither already in flight nor still
    /// inside their randomized first-request delay. Chunks with a peer candidate go to the most-complete-file
    /// peer (jitter breaks ties among equally-complete peers); chunks with no peer candidate fall back to
    /// <paramref name="originalSender"/>. Each target's chunks are split into batches of at most
    /// <see cref="MaxChunksPerRequest"/>, each with its own nonce, so every resulting
    /// <see cref="ChunkRequestMessage"/> fits in a single datagram; at most <paramref name="maxRequests"/>
    /// batches are returned.
    ///
    /// <para>Planning does <b>not</b> mark anything in flight. The caller must call
    /// <see cref="MarkRequested"/> after the corresponding request has actually been sent: a request that
    /// faulted on the way to the socket, or was never sent at all, must not suppress a retry for a full
    /// <see cref="RepairOptions.RequestTimeout"/>.</para>
    /// </summary>
    /// <param name="maxRequests">
    /// Upper bound on returned plans. Defaults to <see cref="RepairOptions.MaxRequestsPerPass"/>; a caller
    /// spreading one pass's budget across several files passes its remaining budget.
    /// </param>
    public IReadOnlyList<RepairRequestPlan> PlanRepairs(
        int fileIndex, IReadOnlyCollection<int> missingChunkIndices, Endpoint originalSender, Func<byte[]> nonceFactory,
        int? maxRequests = null)
    {
        int budget = maxRequests ?? _options.MaxRequestsPerPass;
        if (budget <= 0)
            return [];

        var now = clock.UtcNow;
        ExpireStalePending(now);

        // Stop collecting once this pass has as many candidates as it could possibly emit. Without this the budget
        // was applied only at plan-emission time, i.e. AFTER a full pass over every missing index — and each index
        // costs an IPeerTable.GetPeersWithChunk (O(peers x bitmap bytes) popcount) plus a jitter draw and a
        // dictionary insert, all while the caller holds its state gate and packet processing is stalled. At 10,240
        // missing and 3 peers that was tens of MB of popcount several times a second.
        //
        // Deliberately a cap on *candidates collected*, not a blind prefix of the missing set: a prefix would
        // stall as soon as the lowest indices were all in flight, since it would keep re-examining the same
        // already-pending ones and never reach the rest. Skipping pending/deferred entries while collecting slides
        // the window forward each pass instead. MissingIndices() is ascending, so lowest-first is already the
        // right priority — those are the chunks the carousel passed longest ago and that a peer most likely holds.
        // (The cheap _pending dictionary probe still runs across the whole missing set; the bound is on the
        // expensive per-index peer-table work and the jitter draws, which is where the cost actually was.)
        // Widened deliberately: budget can legitimately be int.MaxValue (a caller asking for "no per-pass bound"),
        // and budget * MaxChunksPerRequest overflows int at that value — which silently made the bound zero and
        // suppressed all repair.
        long considerAtMostWide = (long)budget * _options.MaxChunksPerRequest;
        int considerAtMost = considerAtMostWide >= int.MaxValue ? int.MaxValue : (int)considerAtMostWide;

        var stillNeeded = new List<int>();
        foreach (var chunkIndex in missingChunkIndices)
        {
            if (stillNeeded.Count >= considerAtMost)
                break;

            var key = (fileIndex, chunkIndex);
            if (_pending.ContainsKey(key))
                continue;

            // First time this chunk is seen as eligible: defer it by a random slice of InitialRequestJitter, so
            // many receivers that noticed the same gap in the same instant do not all ask in the same instant.
            if (!_firstRequestNotBefore.TryGetValue(key, out var notBefore))
            {
                var jitter = _options.EffectiveInitialRequestJitter;
                notBefore = jitter > TimeSpan.Zero
                    ? now + TimeSpan.FromTicks((long)(_random.NextDouble() * jitter.Ticks))
                    : now;
                _firstRequestNotBefore[key] = notBefore;
            }
            if (now < notBefore)
                continue;

            stillNeeded.Add(chunkIndex);
        }
        if (stillNeeded.Count == 0)
            return [];

        peerTable.RemoveExpired(now);

        var byTarget = new Dictionary<Endpoint, List<int>>();
        foreach (var chunkIndex in stillNeeded)
        {
            var candidates = peerTable.GetPeersWithChunk(fileIndex, chunkIndex);
            var target = candidates.Count > 0 ? PickAmongTopRanked(candidates) : originalSender;

            if (!byTarget.TryGetValue(target, out var list))
                byTarget[target] = list = [];
            list.Add(chunkIndex);
        }

        var plans = new List<RepairRequestPlan>();
        foreach (var (target, indices) in byTarget)
        {
            for (int offset = 0; offset < indices.Count && plans.Count < budget; offset += _options.MaxChunksPerRequest)
            {
                int length = Math.Min(_options.MaxChunksPerRequest, indices.Count - offset);
                plans.Add(new RepairRequestPlan(
                    target, fileIndex, [.. indices.GetRange(offset, length)], nonceFactory()));
            }
            if (plans.Count >= budget)
                break;
        }

        return plans;
    }

    /// <summary>
    /// Records that a CHUNK_REQUEST covering <paramref name="chunkIndices"/> has actually been sent: they become
    /// in-flight (and so are skipped by <see cref="PlanRepairs"/>) until their per-chunk effective timeout
    /// elapses, and each one's attempt count grows, lengthening that timeout on every subsequent retry.
    /// </summary>
    public void MarkRequested(int fileIndex, IEnumerable<int> chunkIndices, DateTimeOffset now)
    {
        foreach (var chunkIndex in chunkIndices)
        {
            var key = (fileIndex, chunkIndex);
            _pending[key] = now;
            _attempts[key] = _attempts.GetValueOrDefault(key) + 1;
            // Redrawn per attempt, so receivers that started in lockstep drift apart instead of sharing one fixed
            // offset forever.
            _retryJitter[key] = _options.RetryJitterFraction > 0
                ? 1.0 + (_random.NextDouble() * _options.RetryJitterFraction)
                : 1.0;
        }
    }

    /// <summary>Call when a CHUNK_RESPONSE (or an in-carousel CHUNK_DATA) satisfies a chunk, so it stops being treated as in-flight and can be re-requested later if it's somehow still missing.</summary>
    public void MarkFulfilled(int fileIndex, int chunkIndex)
    {
        var key = (fileIndex, chunkIndex);
        _pending.Remove(key);
        // The chunk arrived, so its retry history is spent: a later re-request (e.g. it turned out corrupt and
        // went missing again) starts from the base timeout and draws a fresh first-request delay.
        _attempts.Remove(key);
        _retryJitter.Remove(key);
        _firstRequestNotBefore.Remove(key);
    }

    public bool IsPending(int fileIndex, int chunkIndex) => _pending.ContainsKey((fileIndex, chunkIndex));

    /// <summary>How many times a CHUNK_REQUEST covering this chunk has been confirmed sent. Drives <see cref="EffectiveTimeout"/>.</summary>
    public int AttemptCount(int fileIndex, int chunkIndex) => _attempts.GetValueOrDefault((fileIndex, chunkIndex));

    /// <summary>
    /// <see cref="RepairOptions.RequestTimeout"/> grown by <c>2^min(attempts-1, MaxBackoffDoublings)</c>, spread
    /// by this chunk's <see cref="RepairOptions.RetryJitterFraction"/> draw, then clamped to
    /// <see cref="RepairOptions.EffectiveMaxRequestTimeout"/> and <b>then</b> spread by this chunk's
    /// <see cref="RepairOptions.RetryJitterFraction"/> draw: the exponential backoff plus randomized jitter
    /// wiki/concepts/repair-protocol.md specifies, so a chunk nobody on the segment can supply is retried with
    /// geometrically decreasing frequency instead of forever at the base period — without ever going quiet for
    /// longer than <c>MaxRequestTimeout * (1 + RetryJitterFraction)</c>, and without receivers collapsing into
    /// lockstep once the clamp is reached.
    /// </summary>
    public TimeSpan EffectiveTimeout(int fileIndex, int chunkIndex)
    {
        var key = (fileIndex, chunkIndex);
        int attempts = _attempts.GetValueOrDefault(key);
        int doublings = Math.Clamp(attempts - 1, 0, _options.MaxBackoffDoublings);

        // Clamp the backoff FIRST, then apply this chunk's jitter. Clamping last would erase the jitter exactly
        // where de-synchronization matters most: once the ladder exceeds the clamp, every receiver's timeout would
        // collapse onto precisely MaxRequestTimeout and they would all retry in lockstep forever — which is the
        // steady state for a chunk nobody can supply, i.e. the one case the jitter exists for. Clamping first
        // bounds the backoff while leaving the spread intact, so the true ceiling is
        // MaxRequestTimeout * (1 + RetryJitterFraction) and receivers stay spread across it.
        var grown = _options.RequestTimeout * (1L << doublings);
        if (grown > _options.EffectiveMaxRequestTimeout)
            grown = _options.EffectiveMaxRequestTimeout;

        return grown * _retryJitter.GetValueOrDefault(key, 1.0);
    }

    private void ExpireStalePending(DateTimeOffset now)
    {
        var expired = _pending
            .Where(kv => now - kv.Value > EffectiveTimeout(kv.Key.File, kv.Key.Chunk))
            .Select(kv => kv.Key)
            .ToList();
        foreach (var key in expired)
            _pending.Remove(key); // _attempts is deliberately kept, so the backoff keeps growing across retries
    }

    private Endpoint PickAmongTopRanked(IReadOnlyList<PeerInfo> rankedCandidates)
    {
        int topPopCount = rankedCandidates[0].ChunkPopCount;
        var topTier = rankedCandidates.Where(p => p.ChunkPopCount == topPopCount).ToList();
        return topTier[_random.Next(topTier.Count)].Endpoint;
    }
}
