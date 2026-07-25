using System.Diagnostics;

namespace Castr.Tui;

/// <summary>
/// Derives a smoothed transfer rate (bytes/second) from a sequence of cumulative-byte samples.
/// <para>
/// The <see cref="Castr.Core.Protocol.TransferProgress"/> snapshot is aggregate-only: it exposes
/// <c>CompletedBytes</c> but no timing and no per-peer breakdown. This sampler recovers throughput by
/// differencing successive <c>CompletedBytes</c> readings over a short sliding window, giving the dashboard
/// a live "MB/s" figure. It is intentionally the aggregate rate across all sources — a true per-peer
/// throughput view is not derivable from the snapshot alone (see the docs on <see cref="TransferDashboard"/>).
/// </para>
/// </summary>
internal sealed class ThroughputSampler
{
    private static readonly Stopwatch Clock = Stopwatch.StartNew();

    private readonly TimeSpan _window;
    private readonly Func<TimeSpan> _now;
    private readonly Queue<(TimeSpan At, long Bytes)> _samples = new();
    private readonly Lock _gate = new();

    public ThroughputSampler(TimeSpan? window = null, Func<TimeSpan>? now = null)
    {
        _window = window ?? TimeSpan.FromSeconds(3);
        _now = now ?? (() => Clock.Elapsed);
    }

    /// <summary>
    /// Records a cumulative-bytes reading and returns the current smoothed rate in bytes/second.
    /// <see cref="TransferDashboard"/> subscribes this (indirectly, via its own handler) to
    /// <c>ProgressChanged</c>, which fires from whichever thread completed a send/receive — with
    /// <c>SenderSession</c>'s chunk carousel now able to run several chunk-sends concurrently, multiple
    /// threads can call this at once. <see cref="Queue{T}"/> is not thread-safe, so every access to
    /// <see cref="_samples"/> is serialized under <see cref="_gate"/>.
    /// </summary>
    public double Record(long cumulativeBytes)
    {
        lock (_gate)
        {
            var now = _now();
            _samples.Enqueue((now, cumulativeBytes));

            // Drop samples older than the window (but always keep at least one anchor point).
            while (_samples.Count > 1 && now - _samples.Peek().At > _window)
                _samples.Dequeue();

            var oldest = _samples.Peek();
            var elapsed = (now - oldest.At).TotalSeconds;
            if (elapsed <= 0)
                return 0;

            var delta = cumulativeBytes - oldest.Bytes;
            return delta <= 0 ? 0 : delta / elapsed;
        }
    }
}
