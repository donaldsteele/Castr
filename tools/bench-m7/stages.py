# BENCH (temporary M7 measurement harness) — side-by-side per-stage receiver (and sender) cost across runs.
import glob, os, sys
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from analyze import load

wanted = sys.argv[2:]
rows = {}
order = []
for run_dir in sorted(glob.glob(os.path.join(sys.argv[1], '*'))):
    if not os.path.isdir(run_dir) or not os.path.exists(os.path.join(run_dir, 'harness.json')):
        continue
    h, reps = load(run_dir)
    if wanted and h['tag'] not in wanted:
        continue
    for role in sorted(reps):
        r = reps[role]
        if not r['stages']:
            continue
        key = '%s/%s' % (h['tag'], role)
        order.append(key)
        freq = r['stopwatchFrequency']
        rows[key] = {k: (v[0] / freq * 1000.0, v[1]) for k, v in r['stages'].items()}
        rows[key]['__wall'] = (r['elapsedMs'], 1)
        rows[key]['__cpu'] = (r.get('processCpuMs', 0), 1)

stages = []
for key in order:
    for s in rows[key]:
        if s not in stages:
            stages.append(s)
stages = [s for s in stages if not s.startswith('__')] + ['__cpu', '__wall']

w = max(len(k) for k in order) + 2
print('%-18s' % 'stage' + ''.join('%*s' % (w, k) for k in order))
print('-' * (18 + w * len(order)))
for s in stages:
    line = '%-18s' % s
    for key in order:
        v = rows[key].get(s)
        line += '%*s' % (w, ('%.0f ms' % v[0]) if v else '-')
    print(line)
print()
print('%-18s' % 'us/call' + ''.join('%*s' % (w, k) for k in order))
for s in stages:
    if s.startswith('__'):
        continue
    line = '%-18s' % s
    for key in order:
        v = rows[key].get(s)
        line += '%*s' % (w, ('%.1f (n=%d)' % (v[0] * 1000.0 / max(1, v[1]), v[1])) if v else '-')
    print(line)
