# `tools/bench` — the measurement harness

Built for M12a (fan-out scaling, measurement stage). Every number in the M12a section of
`docs/benchmarks/throughput-runs.md` comes from these four pieces, and every one of them can be re-run.

Before M12a the campaign tooling was ad hoc: the M8/M9 "passive external sniffer" was never committed, so a
wire-composition row could be quoted but not reproduced. That is the gap this directory closes.

| Piece | What it is |
|---|---|
| `Castr.Bench.Sniffer` (`castr-sniff`) | Passive read-only multicast counter, broken down by Castr message type. References **nothing** from `src/` — it knows only the two-byte `[FormatVersion][MessageType]` prefix, so it cannot agree with the product code by construction. |
| `Castr.Bench.DatagramCeiling` (`castr-dgram`) | `drain` stands up a real `UdpMulticastTransport` and counts; `blast` offers sequence-stamped datagrams at a target rate from N sockets. Loss comes from the sequence stamps, because `netstat -s -p udp` reads 0 receive errors on Windows while hundreds of thousands of datagrams are dropped. |
| `Measure-DatagramCeiling.ps1` | Sweeps `blast` against `drain`. `-Receivers N` runs N drains, which makes it a same-host fan-out component measurement. |
| `Measure-FanOut.ps1` | The protocol-level fan-out benchmark: N real `castr receive` processes, one real `castr send`, wall clock plus a SHA-256 check per receiver. |

```powershell
dotnet build -c Release                                   # from the repo root

./Measure-FanOut.ps1          -Interface 'Ethernet' -Receivers 1,2,3,5 -Repeats 3
./Measure-FanOut.ps1          -Interface 'Ethernet' -Receivers 1,3     -Repeats 1 -Sniff
./Measure-DatagramCeiling.ps1 -Interface 'Loopback Pseudo-Interface 1' -Rates 100000,150000,200000 -Threads 6
```

## Rules these scripts encode, so you do not have to remember them

- **Always pass `-Interface`, and record it.** Force-killing a `castr` process skips `DisposeAsync`, so
  `DropMembership` never runs and the join leaks. Leaked memberships make a group's interface ambiguous, and
  a leaked join makes a transfer look *faster*, never slower — the error class that survives review.
- **A benchmark that is unexpectedly ~2.6x fast is suspected loopback, not a result.** Loopback on this host
  runs well above the LAN's real multicast ceiling.
- **Assert completion, not process exit.** A harness bug once reported 0.00 s at ~3,000,000 MB/s because one
  arm's receiver died on an argument-parse error and exited success-shaped. `Measure-FanOut.ps1` requires each
  receiver's own "transfer complete." line *and* a byte-identical SHA-256.
- **`Start-Process -ArgumentList` does not quote array elements containing spaces.** That is what split
  `--interface "Loopback Pseudo-Interface 1"` into three arguments and broke exactly one arm of an A/B. Both
  scripts build one pre-quoted argument string.
- **Sniffer arms are for composition only.** Joining the group adds another kernel multicast copy per
  datagram; never quote goodput from a `-Sniff` run.
- **Interleave arms, do not block them.** Host state drifts during a run, and a blocked schedule turns drift
  into a fake trend.
- **On one host, every extra receiver is another inline kernel copy charged to the sender's `sendto`.** A
  switch does not work that way. `Measure-DatagramCeiling.ps1 -Receivers` measures that cost directly so a
  same-host fan-out curve can be separated from the protocol's own behaviour.

`workspace/` is gitignored scratch (payload files, per-process logs, keys, per-arm JSON). `results/` is
tracked: those CSVs are the raw data behind the run-log rows.
