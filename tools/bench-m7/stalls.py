# BENCH (temporary M7 measurement harness) — quantifies dead air on the receiver: how long the receiver goes
# with zero bytes verified, and how far apart successive repair-request bursts are.
import glob, os, sys
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from analyze import load

MB = 1024.0 * 1024.0

hdr = '%-20s %-6s | %6s %8s %8s %8s | %8s %9s %9s' % (
    'run', 'role', 'stalls', 'totalMs', 'medianMs', 'maxMs', 'nBursts', 'gapMedian', 'gapMean')
print(hdr)
print('-' * len(hdr))
for run_dir in sorted(glob.glob(os.path.join(sys.argv[1], '*'))):
    if not os.path.isdir(run_dir) or not os.path.exists(os.path.join(run_dir, 'harness.json')):
        continue
    h, reps = load(run_dir)
    if len(sys.argv) > 2 and not any(p in h['tag'] for p in sys.argv[2:]):
        continue
    for role in sorted(reps):
        if not role.startswith('recv'):
            continue
        r = reps[role]
        cols = r.get('seriesColumns', [])
        if 'repairChunksRequested' not in cols:
            continue
        mi, vi, ri = cols.index('ms'), cols.index('verifiedBytes'), cols.index('repairChunksRequested')
        s = r['series']
        # dead air: consecutive samples with no byte progress, only counted after the first byte lands
        started = False
        runs, cur = [], 0.0
        for i in range(1, len(s)):
            if s[i][vi] > 0:
                started = True
            if not started or s[i][vi] >= 0.995 * s[-1][vi]:
                continue
            dt = s[i][mi] - s[i - 1][mi]
            if s[i][vi] == s[i - 1][vi]:
                cur += dt
            else:
                if cur > 200:
                    runs.append(cur)
                cur = 0.0
        if cur > 200:
            runs.append(cur)
        runs.sort()
        # repair bursts: samples where repairChunksRequested jumped
        bursts = [s[i][mi] for i in range(1, len(s)) if s[i][ri] > s[i - 1][ri]]
        gaps = [bursts[i + 1] - bursts[i] for i in range(len(bursts) - 1)]
        gaps.sort()
        print('%-20s %-6s | %6d %8.0f %8.0f %8.0f | %8d %9s %9s' % (
            h['tag'], role, len(runs), sum(runs), runs[len(runs) // 2] if runs else 0, runs[-1] if runs else 0,
            len(bursts),
            ('%.0f' % gaps[len(gaps) // 2]) if gaps else '-',
            ('%.0f' % (sum(gaps) / len(gaps))) if gaps else '-'))
