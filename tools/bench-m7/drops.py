# BENCH (temporary M7 measurement harness) — every participant joins the same group with multicast loopback on,
# so each socket should be offered every datagram anybody sent. The shortfall between "offered" and "seen by
# UdpMulticastTransport.ReceiveLoopAsync" is OS-level drop (SO_RCVBUF overflow), which Windows' netstat
# "Receive Errors" counter does not report.
import glob, os, sys
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from analyze import load

hdr = '%-20s %-6s | %10s %10s %10s %7s | %8s %8s | %10s' % (
    'run', 'role', 'offered', 'seen', 'dropped', 'drop%', 'inboxMax', 'blocked', 'netstatErr')
print(hdr)
print('-' * len(hdr))
for run_dir in sorted(glob.glob(os.path.join(sys.argv[1], '*'))):
    if not os.path.isdir(run_dir) or not os.path.exists(os.path.join(run_dir, 'harness.json')):
        continue
    h, reps = load(run_dir)
    if len(sys.argv) > 2 and not any(p in h['tag'] for p in sys.argv[2:]):
        continue
    offered = sum(sum(v[0] for v in r['sent'].values()) for r in reps.values())
    for role in sorted(reps):
        r = reps[role]
        seen = sum(v[0] for v in r['recv'].values())
        drop = offered - seen
        print('%-20s %-6s | %10d %10d %10d %6.1f%% | %8d %8d | %10s' % (
            h['tag'], role, offered, seen, drop, 100.0 * drop / max(1, offered),
            r['peakInboxDepth'], r['channelWriteBlocked'], h['udpDelta'].get('Receive Errors', '?')))
