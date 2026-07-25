# BENCH (temporary M7 measurement harness) — one line per (run, role): burst/stall period, stall fraction,
# CPU, and GC, so a whole sweep can be compared at a glance.
import json, glob, os, sys
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from analyze import load, series_table

hdr = '%-22s %-6s | %8s %8s %8s %8s | %7s %7s %7s | %6s %6s %7s | %7s %6s' % (
    'run', 'role', 'periodMs', 'nEdges', 'stall%', 'peakMB/s', 'meanMB', 'sockBuf', 'depth',
    'cpu%', 'gcPau%', 'gen2', 'allocMB', 'dgrams')
print(hdr)
print('-' * len(hdr))
for run_dir in sorted(glob.glob(os.path.join(sys.argv[1], '*'))):
    if not os.path.isdir(run_dir) or not os.path.exists(os.path.join(run_dir, 'harness.json')):
        continue
    h, reps = load(run_dir)
    if len(sys.argv) > 2 and h['tag'] not in sys.argv[2:]:
        continue
    for role in sorted(reps):
        r = reps[role]
        st = series_table(r, 1)
        if not st:
            continue
        rates = [x[2] for x in st]
        peak = max(rates)
        hi = peak * 0.35
        edges = [st[i][0] for i in range(1, len(st)) if rates[i] >= hi and rates[i - 1] < hi]
        periods = sorted(edges[i + 1] - edges[i] for i in range(len(edges) - 1))
        med = periods[len(periods) // 2] if periods else 0
        stall = 100.0 * sum(1 for x in rates if x < 0.05) / len(rates)
        dg = sum(v[0] for v in r['sent'].values())
        print('%-22s %-6s | %8.0f %8d %7.1f%% %8.2f | %7.2f %7s %7d | %5.0f%% %5.1f%% %7d | %7.0f %6d' % (
            h['tag'], role, med, len(edges), stall, peak,
            sum(rates) / len(rates), r['meta'].get('effectiveReceiveBufferBytes', '?'), max(x[3] for x in st),
            100.0 * r.get('processCpuMs', 0) / max(1, r['elapsedMs']),
            100.0 * r.get('gcPauseMs', 0) / max(1, r['elapsedMs']), r.get('gcGen2', -1),
            r.get('gcAllocatedMB', 0), dg))
