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
    /// <summary>How long the render loop will keep polling for the terminal <c>ProgressChanged</c> snapshot
    /// after <c>isComplete()</c> first reports true, before giving up and rendering whatever it has. See the
    /// comment at its use site in <see cref="RunLoopAsync"/> for why this gap can exist at all.</summary>
    private static readonly TimeSpan CompletionConfirmationGrace = TimeSpan.FromSeconds(2);

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
        using var wake = new RenderSignal();
        DateTime? sessionCompleteObservedAt = null;

        void Handler(TransferProgress progress)
        {
            // Progress events may arrive on the session's threads; capture the newest snapshot and wake the loop.
            Volatile.Write(ref latest, progress);
            rate = sampler.Record(progress.CompletedBytes);
            wake.Signal();
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

                        bool snapshotTerminal = (snapshot?.IsComplete ?? false) || snapshot?.Phase == TransferPhase.TrustDenied;
                        if (snapshotTerminal || cancellationToken.IsCancellationRequested)
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

                        if (isComplete())
                        {
                            // The session already reports completion, but the terminal ProgressChanged event
                            // that carries Phase.Completed (and thus IsComplete on the *snapshot*) is raised a
                            // few statements after the underlying state actually flips (e.g. a receiver's
                            // HandleChunkAsync still has a repair broadcast to send before it calls
                            // EmitProgress). That gap is normally sub-microsecond and invisible, but if this
                            // thread is preempted mid-gap — realistic under heavy scheduler contention, e.g.
                            // many test projects' processes competing for CPU — another thread's isComplete()
                            // read can observe "done" before the corresponding terminal snapshot lands. Give
                            // that guaranteed-imminent event a brief, bounded chance to arrive rather than
                            // freezing the display on a stale, pre-completion frame.
                            sessionCompleteObservedAt ??= DateTime.UtcNow;
                            if (DateTime.UtcNow - sessionCompleteObservedAt.Value < CompletionConfirmationGrace)
                            {
                                try
                                {
                                    await wake.WaitAsync(TimeSpan.FromMilliseconds(5), cancellationToken).ConfigureAwait(false);
                                }
                                catch (OperationCanceledException) { /* re-checked at loop top */ }
                                continue;
                            }

                            // Grace period elapsed with no terminal snapshot ever arriving — this shouldn't
                            // happen (every path that can make IsComplete true also raises a terminal
                            // ProgressChanged shortly after), but render the best-known state and stop rather
                            // than waiting forever, as a safety net for a genuine hang.
                            var lastKnown = Volatile.Read(ref latest);
                            if (lastKnown is not null)
                            {
                                ctx.UpdateTarget(TransferDashboardRenderer.Render(lastKnown, rate));
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
