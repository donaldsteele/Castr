# BENCH (temporary M7 measurement harness) — aggregates repeated cells of Matrix 3 into one row per cell,
# using the in-process marks (manifest-sent -> slowest receive-complete) rather than harness wall clock.
import glob, os, sys, re, statistics
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from analyze import load, iso

MB = 1024.0 * 1024.0
PAYLOAD = 80 * MB


def mark(rep, name, t0):
    off = (iso(rep['startUtc']) - t0).total_seconds() * 1000.0
    for ms, n, v in rep['marks']:
        if n == name:
            return ms + off
    return None


def marks(rep, name, t0):
    off = (iso(rep['startUtc']) - t0).total_seconds() * 1000.0
    return [(ms + off, v) for ms, n, v in rep['marks'] if n == name]


cells = {}
for run_dir in sorted(glob.glob(os.path.join(sys.argv[1], 'M3-*'))):
    if not os.path.exists(os.path.join(run_dir, 'harness.json')):
        continue
    h, reps = load(run_dir)
    m = re.match(r'M3-(.+)-r\d+$', h['tag'])
    if not m:
        continue
    send = reps.get('send')
    recvs = [reps[k] for k in sorted(reps) if k.startswith('recv')]
    if not send or not recvs:
        continue
    t0 = min(iso(r['startUtc']) for r in reps.values())
    start = mark(send, 'manifest-sent', t0)
    ends = [mark(r, 'receive-complete', t0) for r in recvs]
    if start is None or any(e is None for e in ends):
        continue
    dur = (max(ends) - start) / 1000.0
    car = mark(send, 'carousel-complete', t0)
    served = marks(send, 'repair-served', t0)
    sdg = sum(v[0] for k, v in send['sent'].items())
    sby = sum(v[1] for k, v in send['sent'].items())
    rby = sum(sum(v[1] for v in r['sent'].values()) for r in recvs)
    r0 = recvs[0]
    rdg = sum(r0['recv'].get(k, [0, 0])[0] for k in ('ChunkData', 'ChunkPacket', 'ChunkResponse'))
    # How complete was the slowest receiver at the instant the sender's carousel finished?
    atCar = None
    if car is not None:
        slow = recvs[ends.index(max(ends))]
        off = (iso(slow['startUtc']) - t0).total_seconds() * 1000.0
        cols = slow.get('seriesColumns', [])
        vi = cols.index('verifiedBytes') if 'verifiedBytes' in cols else 1
        best = None
        for row in slow['series']:
            if row[0] + off <= car:
                best = row[vi]
        atCar = 100.0 * (best or 0) / PAYLOAD
    cells.setdefault(m.group(1), []).append(dict(
        dur=dur, mbps=PAYLOAD / MB / dur,
        car=((car - start) / 1000.0) if car else None,
        tail=((max(ends) - car) / 1000.0) if car else None,
        servedFirst=(served[0][1] if served else 0),
        servedTotal=sum(v for _, v in served),
        sdg=sdg, wire=(sby + rby) / MB,
        dupPct=100.0 * r0['duplicateChunkPackets'] / max(1, rdg),
        depth=r0['peakInboxDepth'], blocked=r0['channelWriteBlocked'],
        rcpu=100.0 * r0.get('processCpuMs', 0) / max(1, r0['elapsedMs']),
        scpu=100.0 * send.get('processCpuMs', 0) / max(1, send['elapsedMs']),
        n=h["receivers"], atCar=atCar,
    ))

ORDER = ['base', 'noPH', 'PH64', 'noRep', 'noRep-noPH', 'rep2s', 'w2', 'w4', 'dg8000',
         'chunk256k', 'combo', 'combo-noRep', '3recv', '3recv-noPH', '3recv-combo']


def med(rows, k):
    v = [r[k] for r in rows if r[k] is not None]
    return statistics.median(v) if v else float('nan')


hdr = '%-13s %2s %3s | %7s %7s %6s | %8s %8s | %9s %7s %6s %6s | %6s %8s | %5s %5s' % (
    'cell', 'N', 'rep', 'sec', 'MB/s', 'spread', 'carousel', 'tail', 'sentDgram', 'wireMB', 'amp',
    'dup%', 'depth', 'blocked', 'rCPU', 'sCPU')
print(hdr)
print('-' * len(hdr))
for name in ORDER:
    rows = cells.get(name)
    if not rows:
        continue
    durs = [r['dur'] for r in rows]
    print('%-13s %2d %3d | %7.2f %7.2f %5.0f%% | %8.2f %8.2f | %9d %7.1f %6.2f %5.1f%% | %6d %8d | %4.0f%% %4.0f%%' % (
        name, rows[0]['n'], len(rows), med(rows, 'dur'), PAYLOAD / MB / med(rows, 'dur'),
        100.0 * (max(durs) - min(durs)) / med(rows, 'dur'),
        med(rows, 'car'), med(rows, 'tail'), med(rows, 'sdg'), med(rows, 'wire'),
        med(rows, 'wire') * MB / PAYLOAD, med(rows, 'dupPct'),
        med(rows, 'depth'), med(rows, 'blocked'), med(rows, 'rcpu'), med(rows, 'scpu')))
    print('%-13s %2s %3s |   served on first repair request: %d chunks (total %d over the run); '
          'carousel=%.0f%% of wall, tail=%.0f%%; slowest receiver was %.1f%% complete at carousel end' % (
              '', '', '', med(rows, 'servedFirst'), med(rows, 'servedTotal'),
              100.0 * med(rows, 'car') / med(rows, 'dur'), 100.0 * med(rows, 'tail') / med(rows, 'dur'),
              med(rows, 'atCar')))
