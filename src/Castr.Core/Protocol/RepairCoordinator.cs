using Castr.Core.Time;
using Castr.Core.Transport;

namespace Castr.Core.Protocol;

public sealed record RepairRequestPlan(Endpoint Target, int FileIndex, int[] ChunkIndices, byte[] RequestNonce);

public sealed record RepairOptions(TimeSpan RequestTimeout)
{
    public static RepairOptions Default => new(TimeSpan.FromSeconds(5));
}

/// <summary>
/// Pure repair-planning logic: given the current peer table and which chunks are still missing, decides
/// who to ask for what. Never touches a transport directly — the caller (ReceiverSession) is responsible
/// for actually sending the CHUNK_REQUEST messages this returns and for calling <see cref="MarkFulfilled"/>
/// when a response arrives. See wiki/concepts/repair-protocol.md for the ranking rationale.
/// </summary>
public sealed class RepairCoordinator(IPeerTable peerTable, ISystemClock clock, RepairOptions? options = null, Random? random = null)
{
    private readonly RepairOptions _options = options ?? RepairOptions.Default;
    private readonly Random _random = random ?? Random.Shared;
    private readonly Dictionary<(int File, int Chunk), DateTimeOffset> _pending = [];

    /// <summary>
    /// Plans requests for <paramref name="missingChunkIndices"/> not already in flight. Chunks with a
    /// peer candidate go to the most-complete-file peer (jitter breaks ties among equally-complete peers);
    /// chunks with no peer candidate fall back to <paramref name="originalSender"/>.
    /// </summary>
    public IReadOnlyList<RepairRequestPlan> PlanRepairs(
        int fileIndex, IReadOnlyCollection<int> missingChunkIndices, Endpoint originalSender, Func<byte[]> nonceFactory)
    {
        var now = clock.UtcNow;
        ExpireStalePending(now);

        var stillNeeded = missingChunkIndices.Where(i => !_pending.ContainsKey((fileIndex, i))).ToList();
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
            var nonce = nonceFactory();
            plans.Add(new RepairRequestPlan(target, fileIndex, [.. indices], nonce));
            foreach (var chunkIndex in indices)
                _pending[(fileIndex, chunkIndex)] = now;
        }

        return plans;
    }

    /// <summary>Call when a CHUNK_RESPONSE (or an in-carousel CHUNK_DATA) satisfies a chunk, so it stops being treated as in-flight and can be re-requested later if it's somehow still missing.</summary>
    public void MarkFulfilled(int fileIndex, int chunkIndex) => _pending.Remove((fileIndex, chunkIndex));

    public bool IsPending(int fileIndex, int chunkIndex) => _pending.ContainsKey((fileIndex, chunkIndex));

    private void ExpireStalePending(DateTimeOffset now)
    {
        var expired = _pending.Where(kv => now - kv.Value > _options.RequestTimeout).Select(kv => kv.Key).ToList();
        foreach (var key in expired)
            _pending.Remove(key);
    }

    private Endpoint PickAmongTopRanked(IReadOnlyList<PeerInfo> rankedCandidates)
    {
        int topPopCount = rankedCandidates[0].ChunkPopCount;
        var topTier = rankedCandidates.Where(p => p.ChunkPopCount == topPopCount).ToList();
        return topTier[_random.Next(topTier.Count)].Endpoint;
    }
}
