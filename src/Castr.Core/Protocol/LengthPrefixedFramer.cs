using System.Buffers.Binary;
using Castr.Core.Transport;

namespace Castr.Core.Protocol;

/// <summary>
/// Turns the raw byte stream of an <see cref="IStreamConnection"/> into a sequence of whole messages, each
/// framed as a 4-byte big-endian length prefix followed by that many payload bytes. TCP already gives a
/// reliable ordered stream, so — unlike the datagram path's <see cref="WirePacketizer"/>/<see cref="PacketReassembler"/>
/// — no per-message MTU fragmentation or reassembly is needed; the length prefix alone re-establishes message
/// boundaries a stream erases.
/// </summary>
/// <remarks>
/// Security: <see cref="ReadFrameAsync"/> validates the declared length against <see cref="MaxFrameLength"/>
/// <b>before allocating the receive buffer</b>. This is the exact defect class M3 fixed in
/// <see cref="PacketReassembler"/> — never size an allocation directly from an attacker-controlled length
/// prefix without an upper bound, or a single crafted 4-byte header claims gigabytes. An over-limit prefix
/// throws <see cref="InvalidDataException"/> so the caller drops the connection rather than trusting the peer.
/// The default ceiling matches the codebase's established 16 MiB memory-safety bound.
/// Not thread-safe: one framer wraps one connection, driven from a single flow.
/// </remarks>
public sealed class LengthPrefixedFramer(IStreamConnection connection, int maxFrameLength = LengthPrefixedFramer.DefaultMaxFrameLength)
{
    /// <summary>Kept consistent with <see cref="PacketReassembler.DefaultMaxMessageLength"/> (16 MiB).</summary>
    public const int DefaultMaxFrameLength = 16 * 1024 * 1024;

    private const int LengthPrefixSize = 4;

    public int MaxFrameLength { get; } = maxFrameLength > 0
        ? maxFrameLength
        : throw new ArgumentOutOfRangeException(nameof(maxFrameLength));

    /// <summary>Writes one length-prefixed frame. Throws if the payload exceeds <see cref="MaxFrameLength"/> so a local bug can't emit a frame no compliant peer would accept.</summary>
    public async ValueTask WriteFrameAsync(ReadOnlyMemory<byte> payload, CancellationToken cancellationToken = default)
    {
        if (payload.Length > MaxFrameLength)
            throw new InvalidDataException($"Frame length {payload.Length} exceeds the {MaxFrameLength}-byte limit.");

        var prefixed = new byte[LengthPrefixSize + payload.Length];
        BinaryPrimitives.WriteUInt32BigEndian(prefixed.AsSpan(0, LengthPrefixSize), (uint)payload.Length);
        payload.CopyTo(prefixed.AsMemory(LengthPrefixSize));
        await connection.SendAsync(prefixed, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Reads the next whole frame's payload, or <c>null</c> if the peer closed the stream cleanly at a frame
    /// boundary. Throws <see cref="InvalidDataException"/> on an over-limit length prefix or a truncated frame
    /// (peer vanished mid-message) — in both cases the caller must abandon the connection.
    /// </summary>
    public async ValueTask<byte[]?> ReadFrameAsync(CancellationToken cancellationToken = default)
    {
        var prefix = new byte[LengthPrefixSize];
        if (!await ReadExactlyAsync(prefix, allowCleanEofAtStart: true, cancellationToken).ConfigureAwait(false))
            return null; // clean close between frames

        uint length = BinaryPrimitives.ReadUInt32BigEndian(prefix);

        // Reject before allocating anything sized from the wire (see class remarks / PacketReassembler M3 fix).
        if (length > (uint)MaxFrameLength)
            throw new InvalidDataException($"Declared frame length {length} exceeds the {MaxFrameLength}-byte limit.");

        if (length == 0)
            return [];

        var payload = new byte[length];
        if (!await ReadExactlyAsync(payload, allowCleanEofAtStart: false, cancellationToken).ConfigureAwait(false))
            throw new InvalidDataException("Stream ended mid-frame; the peer disconnected before the full payload arrived.");

        return payload;
    }

    private async ValueTask<bool> ReadExactlyAsync(Memory<byte> buffer, bool allowCleanEofAtStart, CancellationToken cancellationToken)
    {
        int read = 0;
        while (read < buffer.Length)
        {
            int n = await connection.ReceiveAsync(buffer[read..], cancellationToken).ConfigureAwait(false);
            if (n == 0)
            {
                if (read == 0 && allowCleanEofAtStart)
                    return false; // stream closed exactly at a frame boundary — a normal end
                throw new InvalidDataException("Stream ended mid-frame.");
            }
            read += n;
        }
        return true;
    }
}
