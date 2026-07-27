namespace Castr.Tui;

/// <summary>
/// A one-permit wake-up for the dashboard's render loop: any number of <see cref="Signal"/> calls made while
/// the loop is not waiting collapse into a single wake.
///
/// <para><b>Why this is a type rather than a bare semaphore.</b> The loop used a
/// <c>new SemaphoreSlim(0)</c> — no maximum — with a <c>catch (SemaphoreFullException)</c> around
/// <c>Release()</c> that read as coalescing but could never fire: an unbounded semaphore never throws there.
/// Permits therefore accumulated one per progress event, and a receiver raises one per verified chunk, so a
/// burst of a thousand chunks bought a thousand immediate loop iterations and a thousand full re-renders of a
/// terminal that can show maybe ten a second. Naming the behaviour and giving it its own tests is what keeps
/// the guard from silently reverting to decoration.</para>
///
/// <para>Coalescing is exactly right here because the loop renders <i>the newest snapshot</i>, not a queue of
/// them: a wake that is dropped costs nothing, because the frame it would have drawn is superseded by the one
/// the surviving wake draws.</para>
/// </summary>
internal sealed class RenderSignal : IDisposable
{
    private readonly SemaphoreSlim _wake = new(0, 1);

    /// <summary>Wakes the loop, or does nothing if a wake is already pending. Safe to call from any thread.</summary>
    public void Signal()
    {
        try { _wake.Release(); }
        catch (SemaphoreFullException) { /* a wake is already pending — the newer snapshot rides on it */ }
    }

    /// <summary>Waits for a signal, giving up after <paramref name="timeout"/>. Returns true if a signal arrived.</summary>
    public Task<bool> WaitAsync(TimeSpan timeout, CancellationToken cancellationToken) =>
        _wake.WaitAsync(timeout, cancellationToken);

    /// <summary>Pending wakes, 0 or 1. Test-only, and the whole point: it is the value that used to be unbounded.</summary>
    public int PendingWakes => _wake.CurrentCount;

    public void Dispose() => _wake.Dispose();
}
