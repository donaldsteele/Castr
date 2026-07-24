namespace Castr.Core.Protocol;

/// <summary>Which side of a transfer a <see cref="TransferProgress"/> snapshot describes.</summary>
public enum TransferRole
{
    Sender,
    Receiver,
}

/// <summary>
/// Coarse lifecycle stage of a transfer, shared by both sides. Not every phase applies to both roles:
/// <see cref="AwaitingKey"/>/<see cref="TrustDenied"/>/<see cref="Completed"/> are receiver-only,
/// <see cref="Serving"/> is sender-only. A UI can render whichever phases it observes.
/// </summary>
public enum TransferPhase
{
    /// <summary>Session has started but no manifest/announce has been processed yet.</summary>
    Starting,

    /// <summary>Receiver: manifest accepted and trusted; waiting for the KEY_GRANT that unlocks chunk plaintext.</summary>
    AwaitingKey,

    /// <summary>Chunks are actively flowing (sender carousel in progress, or receiver verifying/decrypting).</summary>
    Transferring,

    /// <summary>Sender: the one-shot carousel has finished; the session stays up only to answer repair requests.</summary>
    Serving,

    /// <summary>Receiver: every chunk has been verified, decrypted, and written.</summary>
    Completed,

    /// <summary>Receiver: the sender was not trusted (and no interactive prompt accepted them); the transfer was aborted.</summary>
    TrustDenied,
}

/// <summary>
/// An immutable snapshot of a transfer's live progress, pushed to subscribers of
/// <see cref="SenderSession.ProgressChanged"/> / <see cref="ReceiverSession.ProgressChanged"/> at each
/// meaningful state transition. Every field is derived from state the session already tracks — this type
/// is a pure read-model over the state machine and never influences wire, trust, or encryption behavior.
/// </summary>
/// <param name="Role">Which side produced this snapshot.</param>
/// <param name="Phase">Current lifecycle stage.</param>
/// <param name="TransferName">Human-readable transfer name from the manifest (empty before the manifest is known).</param>
/// <param name="TotalFiles">Number of files in the transfer (0 before the manifest is known).</param>
/// <param name="TotalChunks">Total chunks across all files.</param>
/// <param name="CompletedChunks">Chunks sent (sender carousel) or verified+decrypted (receiver) so far.</param>
/// <param name="PendingChunks">Chunks still outstanding (<see cref="TotalChunks"/> - <see cref="CompletedChunks"/>).</param>
/// <param name="TotalBytes">Total plaintext bytes across all files.</param>
/// <param name="CompletedBytes">Plaintext bytes sent (sender) or verified (receiver) so far.</param>
/// <param name="PeerCount">Receiver: known repair-source peers seen via PEER_HAVE gossip. Sender: distinct receivers granted the content key.</param>
public sealed record TransferProgress(
    TransferRole Role,
    TransferPhase Phase,
    string TransferName,
    int TotalFiles,
    int TotalChunks,
    int CompletedChunks,
    int PendingChunks,
    long TotalBytes,
    long CompletedBytes,
    int PeerCount)
{
    /// <summary>Fraction of chunks completed in [0, 1]; 1.0 for an empty transfer.</summary>
    public double FractionComplete => TotalChunks == 0 ? 1.0 : (double)CompletedChunks / TotalChunks;

    /// <summary>True once the receiver has fully completed the transfer.</summary>
    public bool IsComplete => Phase == TransferPhase.Completed;
}
