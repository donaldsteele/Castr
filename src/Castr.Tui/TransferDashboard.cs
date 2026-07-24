using Castr.Core.Protocol;
using Spectre.Console;

namespace Castr.Tui;

/// <summary>
/// A live, colorful terminal dashboard for an in-progress Castr transfer, built on Spectre.Console's
/// <c>LiveDisplay</c>. Subscribe it to a running <see cref="SenderSession"/> or <see cref="ReceiverSession"/>
/// and it renders the current phase, a progress bar, chunk/byte counts, peer count, aggregate throughput, and
/// a chunk-map heatmap, refreshing on every <c>ProgressChanged</c> event until the transfer completes or the
/// caller cancels.
/// <para>
/// Typical CLI usage (a future <c>--tui</c> flag):
/// <code>
/// await new TransferDashboard().RunAsync(receiverSession, cancellationToken);
/// // ...run concurrently with receiverSession.RunAsync(cancellationToken)
/// </code>
/// </para>
/// <para>
/// <b>Aggregate-only data.</b> <see cref="TransferProgress"/> exposes counts, not the raw per-chunk bitmap or
/// a per-peer byte breakdown. Consequently the "heatmap" shows completion density across the chunk space
/// rather than exact missing-chunk positions (see <see cref="ChunkHeatmap"/>), and the throughput figure is
/// the aggregate rate across all sources plus a peer count, not a per-peer table (see
/// <see cref="ThroughputSampler"/>). Both are honest views of the data the observability contract provides;
/// richer fidelity would require a new read-only accessor on the sessions, which is out of scope.
/// </para>
/// </summary>
public sealed class TransferDashboard
{
    private readonly IAnsiConsole _console;
    private readonly TimeSpan _refreshInterval;

    /// <param name="console">Target console; defaults to <see cref="AnsiConsole.Console"/>. Pass a
    /// <c>Spectre.Console.Testing.TestConsole</c> to capture output in tests.</param>
    /// <param name="refreshInterval">How often the loop wakes to refresh throughput even without a new
    /// progress event. Defaults to 250&#160;ms.</param>
    public TransferDashboard(IAnsiConsole? console = null, TimeSpan? refreshInterval = null)
    {
        _console = console ?? AnsiConsole.Console;
        _refreshInterval = refreshInterval ?? TimeSpan.FromMilliseconds(250);
    }

    /// <summary>Renders live progress for a sender until <paramref name="cancellationToken"/> is cancelled.
    /// A sender has no terminal "complete" state (it keeps serving repairs), so this returns only on
    /// cancellation.</summary>
    public Task RunAsync(SenderSession session, CancellationToken cancellationToken = default) =>
        RunLoopAsync(
            handler => session.ProgressChanged += handler,
            handler => session.ProgressChanged -= handler,
            isComplete: () => false,
            cancellationToken);

    /// <summary>Renders live progress for a receiver until it reports <see cref="ReceiverSession.IsComplete"/>
    /// or <paramref name="cancellationToken"/> is cancelled.</summary>
    public Task RunAsync(ReceiverSession session, CancellationToken cancellationToken = default) =>
        RunLoopAsync(
            handler => session.ProgressChanged += handler,
            handler => session.ProgressChanged -= handler,
            isComplete: () => session.IsComplete,
            cancellationToken);

    /// <summary>
    /// Core render loop, decoupled from the concrete session type via subscribe/unsubscribe/isComplete
    /// delegates so it can be exercised directly with synthetic <see cref="TransferProgress"/> events.
    /// </summary>
    internal async Task RunLoopAsync(
        Action<Action<TransferProgress>> subscribe,
        Action<Action<TransferProgress>> unsubscribe,
        Func<bool> isComplete,
        CancellationToken cancellationToken)
    {
        var sampler = new ThroughputSampler();
        TransferProgress? latest = null;
        double rate = 0;
        var wake = new SemaphoreSlim(0);

        void Handler(TransferProgress progress)
        {
            // Progress events may arrive on the session's threads; capture the newest snapshot and wake the loop.
            Volatile.Write(ref latest, progress);
            rate = sampler.Record(progress.CompletedBytes);
            try { wake.Release(); } catch (SemaphoreFullException) { /* already signalled */ }
        }

        subscribe(Handler);
        try
        {
            await _console.Live(TransferDashboardRenderer.Render(EmptySnapshot()))
                .StartAsync(async ctx =>
                {
                    while (true)
                    {
                        var snapshot = Volatile.Read(ref latest);
                        if (snapshot is not null)
                        {
                            ctx.UpdateTarget(TransferDashboardRenderer.Render(snapshot, rate));
                            ctx.Refresh();
                        }

                        bool done = cancellationToken.IsCancellationRequested
                            || isComplete()
                            || (snapshot?.IsComplete ?? false)
                            || snapshot?.Phase == TransferPhase.TrustDenied;
                        if (done)
                        {
                            // One last frame so the final state (100% / denied) is what stays on screen.
                            var final = Volatile.Read(ref latest);
                            if (final is not null)
                            {
                                ctx.UpdateTarget(TransferDashboardRenderer.Render(final, rate));
                                ctx.Refresh();
                            }
                            return;
                        }

                        try
                        {
                            await wake.WaitAsync(_refreshInterval, cancellationToken).ConfigureAwait(false);
                        }
                        catch (OperationCanceledException)
                        {
                            // fall through: the loop re-checks `done` and renders the final frame.
                        }
                    }
                }).ConfigureAwait(false);
        }
        finally
        {
            unsubscribe(Handler);
            wake.Dispose();
        }
    }

    private static TransferProgress EmptySnapshot() =>
        new(TransferRole.Receiver, TransferPhase.Starting, string.Empty, 0, 0, 0, 0, 0, 0, 0);
}
