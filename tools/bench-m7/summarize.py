# BENCH (temporary M7 measurement harness) — one row per run across the whole matrix.
import json, glob, os, sys, re, datetime

MB = 1024.0 * 1024.0
PAYLOAD = 80 * MB


def iso(s):
    s = re.sub(r'\.(\d{6})\d+', r'.\1', s)
    return datetime.datetime.fromisoformat(s.replace('Z', '+00:00'))


def load(run_dir):
    h = json.load(open(os.path.join(run_dir, 'harness.json'), encoding='utf-8-sig'))
    reps = {}
    for f in sorted(glob.glob(os.path.join(run_dir, '*.json'))):
        if os.path.basename(f) == 'harness.json':
            continue
        d = json.load(open(f, encoding='utf-8-sig'))
        reps[d['tag']] = d
    return h, reps


def chunkdg(rep, direction):
    d = rep[direction]
    return (sum(d.get(k, [0, 0])[0] for k in ('ChunkData', 'ChunkPacket', 'ChunkResponse')),
            sum(d.get(k, [0, 0])[1] for k in ('ChunkData', 'ChunkPacket', 'ChunkResponse')))


rows = []
for run_dir in sorted(glob.glob(os.path.join(sys.argv[1], '*'))):
    if not os.path.isdir(run_dir) or not os.path.exists(os.path.join(run_dir, 'harness.json')):
        continue
    h, reps = load(run_dir)
    send = reps.get('send')
    recvs = [reps[k] for k in reps if k.startswith('recv')]
    if not send or not recvs:
        continue
    # completion time: from the sender's own manifest-sent mark to the slowest receiver's receive-complete
    t0 = min(iso(r['startUtc']) for r in reps.values())
    def abs_mark(rep, name):
        off = (iso(rep['startUtc']) - t0).total_seconds() * 1000.0
        for ms, n, v in rep['marks']:
            if n == name:
                return ms + off
        return None
    start = abs_mark(send, 'manifest-sent')
    ends = [abs_mark(r, 'receive-complete') for r in recvs]
    ok = all(e is not None for e in ends) and start is not None
    dur = (max(ends) - start) / 1000.0 if ok else None
    carousel = abs_mark(send, 'carousel-complete')
    carousel_s = (carousel - start) / 1000.0 if (carousel and start) else None

    s_dg, s_by = chunkdg(send, 'sent')
    ph_by = sum(r['sent'].get(k, [0, 0])[1] for r in recvs for k in ('PeerHave', 'PacketFragment'))
    ph_dg = sum(r['sent'].get(k, [0, 0])[0] for r in recvs for k in ('PeerHave', 'PacketFragment'))
    total_by = s_by + ph_by
    r0 = recvs[0]
    dup = r0['duplicateChunkPackets']
    rdg, _ = chunkdg(r0, 'recv')
    verified = all(v['ok'] for v in (h['verify'] if isinstance(h['verify'], list) else [h['verify']]))

    rows.append(dict(
        tag=h['tag'], chunk=h['chunkSize'], win=h['sendWindow'], ph=h['peerHaveEvery'],
        dg=h['maxDatagram'], n=h['receivers'], iface=('LB' if h['interface'] else 'auto'),
        rstart=h.get('repairStartMs', 0),
        sec=dur, mbps=(PAYLOAD / MB / dur) if dur else None,
        carousel=carousel_s,
        sentDg=s_dg, wireMB=total_by / MB, amp=total_by / PAYLOAD,
        phPct=100.0 * ph_by / max(1, total_by), phDg=ph_dg,
        dupPct=100.0 * dup / max(1, rdg),
        repReq=r0['repairChunksRequested'], repMsg=r0['repairRequestMessages'],
        peakDepth=r0['peakInboxDepth'], sPeak=send['peakInboxDepth'],
        rcpu=100.0 * r0.get('processCpuMs', 0) / max(1, r0['elapsedMs']),
        scpu=100.0 * send.get('processCpuMs', 0) / max(1, send['elapsedMs']),
        udpErr=h['udpDelta'].get('Receive Errors', 0), ok=verified,
    ))

hdr = ('%-22s %7s %4s %4s %7s %2s %5s | %7s %7s %8s | %9s %6s %6s %6s %6s | %6s %6s | %5s %5s | %s' % (
    'run', 'chunk', 'win', 'PH', 'dgram', 'N', 'iface', 'sec', 'MB/s', 'carousel',
    'sentDgram', 'wireMB', 'amp', 'PH%B', 'dup%', 'repReq', 'repMsg', 'rCPU', 'sCPU', 'ok'))
print(hdr)
print('-' * len(hdr))
for r in sorted(rows, key=lambda x: x['tag']):
    print('%-22s %7d %4d %4d %7d %2d %5s | %7s %7s %8s | %9d %6.1f %6.2f %6.1f %6.1f | %6d %6d | %4.0f%% %4.0f%% | %s' % (
        r['tag'], r['chunk'], r['win'], r['ph'], r['dg'], r['n'], r['iface'],
        ('%.2f' % r['sec']) if r['sec'] else 'n/a', ('%.2f' % r['mbps']) if r['mbps'] else 'n/a',
        ('%.2f' % r['carousel']) if r['carousel'] else 'n/a',
        r['sentDg'], r['wireMB'], r['amp'], r['phPct'], r['dupPct'],
        r['repReq'], r['repMsg'], r['rcpu'], r['scpu'], 'OK' if r['ok'] else 'FAIL'))
