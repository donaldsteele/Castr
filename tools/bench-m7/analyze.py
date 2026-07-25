# BENCH (temporary M7 measurement harness) — turns the BenchMetrics JSON reports for one run into a
# human-readable report: throughput time series, packet/byte accounting, per-stage cost, channel occupancy.
import json, sys, glob, os, datetime

MB = 1024.0 * 1024.0


def load(run_dir):
    harness = json.load(open(os.path.join(run_dir, 'harness.json'), encoding='utf-8-sig'))
    reports = {}
    for f in sorted(glob.glob(os.path.join(run_dir, '*.json'))):
        if os.path.basename(f) == 'harness.json':
            continue
        d = json.load(open(f, encoding='utf-8-sig'))
        reports[d['tag']] = d
    return harness, reports


def iso(s):
    # .NET "O" format has 7 fractional digits; Python 3.10's fromisoformat accepts at most 6.
    import re
    s = re.sub(r'\.(\d{6})\d+', r'.\1', s)
    return datetime.datetime.fromisoformat(s.replace('Z', '+00:00'))


COLS = ['ms', 'verifiedBytes', 'inboxDepth', 'chunkDgramRecv', 'peerHaveDgramRecv',
        'chunkDgramSent', 'duplicates', 'repairChunksRequested', 'cpuMs']


def series_table(rep, bucket_ms=250):
    """Resamples the raw sample list into bucket_ms buckets.

    Returns [(ms, cumMB, MB/s, maxDepth, dupPerSec, cpuPct, repairReqDelta)].
    """
    cols = rep.get('seriesColumns', COLS)
    idx = {c: i for i, c in enumerate(cols)}
    s = rep['series']
    out = []
    prev = None
    acc = []
    for row in s:
        if prev is None:
            prev = row
            continue
        acc.append(row)
        ms = row[idx['ms']]
        dt = ms - prev[idx['ms']]
        if dt >= bucket_ms:
            def d(c):
                i = idx.get(c, -1)
                if i < 0 or i >= len(row) or i >= len(prev):
                    return 0  # older report written before this column existed
                return row[i] - prev[i]
            rate = d('verifiedBytes') / (dt / 1000.0) / MB
            maxdepth = max(a[idx['inboxDepth']] for a in acc)
            dups = d('duplicates') / (dt / 1000.0)
            cpu = 100.0 * d('cpuMs') / dt
            out.append((ms, row[idx['verifiedBytes']] / MB, rate, maxdepth, dups, cpu,
                        d('repairChunksRequested'), d('gcPauseMs')))
            prev = row
            acc = []
    return out


def sparkline(vals, vmax=None):
    chars = ' .:-=+*#%@'
    if not vals:
        return ''
    vmax = vmax or max(vals) or 1
    return ''.join(chars[min(len(chars) - 1, int(v / vmax * (len(chars) - 1)))] for v in vals)


def report(run_dir, bucket=250, full_series=False):
    harness, reports = load(run_dir)
    print('=' * 100)
    print('RUN %s   chunk=%s window=%s peerHaveEvery=%s maxDgram=%s receivers=%s iface=%r' % (
        harness['tag'], harness['chunkSize'], harness['sendWindow'], harness['peerHaveEvery'],
        harness['maxDatagram'], harness['receivers'], harness['interface']))
    print('harness wall: %d ms | verified: %s | UDP delta: %s' % (
        harness['harnessWallMs'],
        [(v['recv'], v['ok']) for v in (harness['verify'] if isinstance(harness['verify'], list) else [harness['verify']])],
        {k: v for k, v in harness['udpDelta'].items()}))
    print('=' * 100)

    # ---- shared timeline anchor ----
    t0 = min(iso(r['startUtc']) for r in reports.values())
    for tag, r in reports.items():
        off = (iso(r['startUtc']) - t0).total_seconds() * 1000.0
        r['_offset'] = off

    for tag in sorted(reports):
        r = reports[tag]
        print('\n--- %s (pid %s, offset %+.0f ms, elapsed %.0f ms) ---' % (tag, r['pid'], r['_offset'], r['elapsedMs']))
        print('  meta: %s' % r.get('meta'))
        marks = [(m[0] + r['_offset'], m[1], m[2]) for m in r['marks']]
        for ms, name, val in marks:
            print('  MARK %-18s t=%8.0f ms (aligned)  value=%s' % (name, ms, val))

        # wire accounting
        sent, recv = r['sent'], r['recv']
        tot_s_d = sum(v[0] for v in sent.values()); tot_s_b = sum(v[1] for v in sent.values())
        tot_r_d = sum(v[0] for v in recv.values()); tot_r_b = sum(v[1] for v in recv.values())
        print('  %-22s %12s %14s %8s | %12s %14s %8s' % ('message type', 'sent dgrams', 'sent bytes', '%B', 'recv dgrams', 'recv bytes', '%B'))
        keys = sorted(set(list(sent) + list(recv)))
        for k in keys:
            sd, sb = sent.get(k, [0, 0]); rd, rb = recv.get(k, [0, 0])
            print('  %-22s %12d %14d %7.2f%% | %12d %14d %7.2f%%' % (
                k, sd, sb, 100.0 * sb / max(1, tot_s_b), rd, rb, 100.0 * rb / max(1, tot_r_b)))
        print('  %-22s %12d %14d          | %12d %14d' % ('TOTAL', tot_s_d, tot_s_b, tot_r_d, tot_r_b))

        print('  chunksVerified=%s  duplicateChunkPackets=%s  reassemblyIncomplete=%s  peerHaveSuppressed=%s' % (
            r['chunksVerified'], r['duplicateChunkPackets'], r['reassemblyIncomplete'], r['peerHaveSuppressed']))
        print('  repairPasses=%s  repairChunksRequested=%s  repairRequestMessages=%s' % (
            r['repairPasses'], r['repairChunksRequested'], r['repairRequestMessages']))
        print('  inbox: capacity=%s peakDepth=%s channelWriteBlocked=%s' % (
            r['inboxCapacity'], r['peakInboxDepth'], r['channelWriteBlocked']))

        # stages
        freq = r['stopwatchFrequency']
        stages = r['stages']
        if stages:
            tot = sum(v[0] for v in stages.values())
            print('  %-16s %12s %12s %14s %8s' % ('stage', 'total ms', 'count', 'us/call', '% staged'))
            for k, (ticks, cnt) in sorted(stages.items(), key=lambda kv: -kv[1][0]):
                ms = ticks / freq * 1000.0
                print('  %-16s %12.1f %12d %14.2f %7.1f%%' % (k, ms, cnt, ms * 1000.0 / max(1, cnt), 100.0 * ticks / max(1, tot)))
            print('  %-16s %12.1f (note: GateWait/RepairPass overlap the others; PeerHave includes its socket sends)'
                  % ('SUM', tot / freq * 1000.0))

        # time series
        st = series_table(r, bucket)
        if st:
            rates = [x[2] for x in st]
            nz = [x for x in rates if x > 0.01]
            print('  throughput series (%d ms buckets): n=%d  mean=%.2f  median=%.2f  p10=%.2f  p90=%.2f  max=%.2f MB/s' % (
                bucket, len(rates), sum(rates) / len(rates), sorted(rates)[len(rates) // 2],
                sorted(rates)[int(len(rates) * .1)], sorted(rates)[int(len(rates) * .9)], max(rates)))
            zero = sum(1 for x in rates if x < 0.05)
            print('  stalled buckets (<0.05 MB/s): %d/%d = %.1f%%   nonzero mean=%.2f MB/s' % (
                zero, len(rates), 100.0 * zero / len(rates), (sum(nz) / len(nz)) if nz else 0))
            print('  rate  |%s| max=%.1f MB/s' % (sparkline(rates), max(rates)))
            print('  depth |%s| max=%d of %d' % (
                sparkline([x[3] for x in st], r['inboxCapacity']), max(x[3] for x in st), r['inboxCapacity']))
            print('  dups  |%s| max=%.0f/s' % (sparkline([x[4] for x in st]), max(x[4] for x in st)))
            print('  cpu%%  |%s| max=%.0f%%' % (sparkline([x[5] for x in st]), max(x[5] for x in st)))
            print('  processCpuMs=%.0f (%.1f%% of one core over %.1f s wall)' % (
                r.get('processCpuMs', 0), 100.0 * r.get('processCpuMs', 0) / max(1, r['elapsedMs']), r['elapsedMs'] / 1000))
            print('  GC: pauseMs=%.0f (%.1f%% of wall) gen0=%s gen1=%s gen2=%s allocatedMB=%.0f' % (
                r.get('gcPauseMs', 0), 100.0 * r.get('gcPauseMs', 0) / max(1, r['elapsedMs']),
                r.get('gcGen0'), r.get('gcGen1'), r.get('gcGen2'), r.get('gcAllocatedMB', 0)))
            # burst/stall period: distance between successive rising edges of the rate signal
            hi = max(rates) * 0.35
            edges = [st[i][0] for i in range(1, len(st)) if rates[i] >= hi and rates[i - 1] < hi]
            if len(edges) >= 3:
                periods = [edges[i + 1] - edges[i] for i in range(len(edges) - 1)]
                periods.sort()
                print('  burst period: n=%d median=%.0f ms mean=%.0f ms (min %.0f, max %.0f)' % (
                    len(periods), periods[len(periods) // 2], sum(periods) / len(periods), periods[0], periods[-1]))
            if full_series:
                print('  %8s %10s %10s %9s %10s %7s %10s %8s' % (
                    'ms', 'cumMB', 'MB/s', 'maxDepth', 'dups/s', 'cpu%', 'repairReq', 'gcPause'))
                for ms, cum, rate, depth, dups, cpu, rq, gc in st:
                    print('  %8.0f %10.2f %10.2f %9d %10.0f %6.0f%% %10d %7.1f%s' % (
                        ms, cum, rate, depth, dups, cpu, rq, gc, '   <== REPAIR BURST' if rq > 0 else ''))


if __name__ == '__main__':
    args = [a for a in sys.argv[1:] if not a.startswith('-')]
    bucket = 250
    full = '--full' in sys.argv
    for a in sys.argv[1:]:
        if a.startswith('--bucket='):
            bucket = int(a.split('=')[1])
    for d in args:
        report(d, bucket, full)
