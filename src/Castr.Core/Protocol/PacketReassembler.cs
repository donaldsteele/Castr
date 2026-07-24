namespace Castr.Core.Protocol;

/// <summary>
/// Receive-side counterpart to <see cref="WirePacketizer.Fragment"/>. Feed every incoming datagram to
/// <see cref="Offer"/>: a non-fragment datagram is returned verbatim (pass-through), and a
/// <see cref="PacketFragmentMessage"/> is buffered by its group id until every fragment of that group has
/// arrived, at which point the fully reassembled original message bytes are returned exactly once.
///
/// UDP gives no ordering, no dedup, and no delivery guarantee, so the buffer:
/// <list type="bullet">
/// <item>stores fragments by index, making arrival order irrelevant;</item>
/// <item>ignores a fragment whose slot is already filled (duplicate/retransmit);</item>
/// <item>only completes when all <c>PacketCount</c> distinct indices are present — a partially-arrived chunk
/// is never surfaced as data, so it stays "not yet received" and the existing chunk-level repair re-requests
/// the whole chunk;</item>
/// <item>bounds memory with an LRU cap on concurrent in-flight groups, evicting the oldest partial group when
/// the cap is reached (its chunk, still incomplete, is later re-requested by repair).</item>
/// </list>
/// Not thread-safe: each session owns one instance and drives it from its single receive loop.
/// </summary>
public sealed class PacketReassembler
{
    /// <summary>
    /// Upper bound on a reassembled message's total length. This path runs <b>pre-authentication</b> — every raw
    /// datagram is offered here before any manifest/trust/session check — with no manifest context to derive a
    /// tighter bound from, so it is a fixed ceiling: generous enough for a legitimate large control message (e.g.
    /// a Manifest describing many files, or a whole chunk sent un-packetized under a large datagram budget), yet
    /// far below what would let a single crafted packet claim gigabytes. Kept consistent with the codebase's
    /// established 16 MiB memory-safety ceiling (Castr.Cli.CastrPaths.MaxChunkSize).
    /// </summary>
    public const int DefaultMaxMessageLength = 16 * 1024 * 1024;

    private readonly int _maxConcurrentGroups;
    private readonly int _maxMessageLength;
    private readonly Dictionary<long, Partial> _partials = [];
    private long _sequence;

    public PacketReassembler(int maxConcurrentGroups = 1024, int maxMessageLength = DefaultMaxMessageLength)
    {
        if (maxConcurrentGroups <= 0)
            throw new ArgumentOutOfRangeException(nameof(maxConcurrentGroups));
        if (maxMessageLength < 0)
            throw new ArgumentOutOfRangeException(nameof(maxMessageLength));
        _maxConcurrentGroups = maxConcurrentGroups;
        _maxMessageLength = maxMessageLength;
    }

    /// <summary>Number of partially-reassembled groups currently buffered. Exposed for testing/diagnostics.</summary>
    public int PendingGroupCount => _partials.Count;

    /// <summary>
    /// Returns the datagram unchanged if it is not a fragment; the fully reassembled message bytes if this
    /// fragment completed its group; or <c>null</c> if it was a fragment that did not (yet) complete a group,
    /// or a malformed/duplicate fragment with nothing new to surface.
    /// </summary>
    public byte[]? Offer(byte[] datagram)
    {
        ArgumentNullException.ThrowIfNull(datagram);

        // Pass non-fragment datagrams straight through. byte[0] is FormatVersion, byte[1] is the MessageType
        // tag; only PacketFragmentMessage carries that tag, so this is an unambiguous discriminator.
        if (datagram.Length < 2 || datagram[1] != (byte)MessageType.PacketFragment)
            return datagram;

        PacketFragmentMessage fragment;
        try { fragment = (PacketFragmentMessage)MessageCodec.Decode(datagram); }
        catch { return null; } // malformed fragment — drop, repair will re-send the chunk

        if (fragment.PacketCount <= 0
            || fragment.PacketIndex < 0
            || fragment.PacketIndex >= fragment.PacketCount
            || fragment.TotalLength < 0
            || fragment.Fragment.Length > fragment.TotalLength)
            return null;

        // Reject before allocating anything sized from the wire. This is reachable pre-auth, so an attacker must
        // not be able to make `new byte[TotalLength]` or `new byte[PacketCount][]` claim gigabytes: bound the
        // total length against the fixed ceiling, and the packet count against the total length (a legitimate
        // fragmentation never yields more fragments than bytes). Oversized packets are dropped, not thrown on.
        if (fragment.TotalLength > _maxMessageLength
            || fragment.PacketCount > Math.Max(1, fragment.TotalLength))
            return null;

        if (fragment.PacketCount == 1)
        {
            _partials.Remove(fragment.GroupId);
            return fragment.Fragment.Length == fragment.TotalLength ? fragment.Fragment : null;
        }

        if (!_partials.TryGetValue(fragment.GroupId, out var partial))
        {
            EvictOldestIfFull();
            partial = new Partial(fragment.PacketCount, fragment.TotalLength, ++_sequence);
            _partials[fragment.GroupId] = partial;
        }
        else if (partial.PacketCount != fragment.PacketCount || partial.TotalLength != fragment.TotalLength)
        {
            // Same group id with inconsistent metadata: a (vanishingly unlikely) group-id collision or a
            // corrupted fragment. Ignore it rather than corrupt an in-progress reassembly.
            return null;
        }

        if (!partial.Add(fragment.PacketIndex, fragment.Fragment))
            return null; // duplicate fragment — nothing new

        if (!partial.IsComplete)
            return null;

        _partials.Remove(fragment.GroupId);
        return partial.TryAssemble(); // null only if lengths don't add up (corruption)
    }

    private void EvictOldestIfFull()
    {
        if (_partials.Count < _maxConcurrentGroups)
            return;

        long oldestKey = 0;
        long oldestSequence = long.MaxValue;
        foreach (var (key, value) in _partials)
        {
            if (value.Sequence < oldestSequence)
            {
                oldestSequence = value.Sequence;
                oldestKey = key;
            }
        }
        _partials.Remove(oldestKey);
    }

    private sealed class Partial
    {
        private readonly byte[]?[] _fragments;
        private int _received;

        public Partial(int packetCount, int totalLength, long sequence)
        {
            PacketCount = packetCount;
            TotalLength = totalLength;
            Sequence = sequence;
            _fragments = new byte[packetCount][];
        }

        public int PacketCount { get; }
        public int TotalLength { get; }
        public long Sequence { get; }
        public bool IsComplete => _received == PacketCount;

        public bool Add(int index, byte[] fragment)
        {
            if (index < 0 || index >= PacketCount || _fragments[index] is not null)
                return false;
            _fragments[index] = fragment;
            _received++;
            return true;
        }

        public byte[]? TryAssemble()
        {
            int total = 0;
            foreach (var fragment in _fragments)
            {
                if (fragment is null)
                    return null;
                total += fragment.Length;
            }
            if (total != TotalLength)
                return null;

            var result = new byte[total];
            int offset = 0;
            foreach (var fragment in _fragments)
            {
                Array.Copy(fragment!, 0, result, offset, fragment!.Length);
                offset += fragment.Length;
            }
            return result;
        }
    }
}
