# Castr.Core.E2ETests — container fan-out end-to-end tier

Real multi-container, end-to-end tests that drive the **shipped `castr` CLI binary** across separate
network namespaces (Docker containers) on a shared multicast-capable bridge network, using
[Testcontainers for .NET](https://dotnet.testcontainers.org/). This is the "Testcontainers E2E fan-out
job" called for by the project plan / M3 roadmap.

Unlike `Castr.Core.IntegrationTests` (many real sockets, but one host network stack, and *simulated* loss
via `ChaosTransport`), these tests exercise:

- true IP **multicast fan-out** from one sender container to N receiver containers over a Docker bridge;
- the actual `castr send` / `castr trust add` / `castr receive` command surface;
- **real kernel-level packet loss** (`tc qdisc ... netem`) and recovery via Castr's peer/sender repair;
- **byte-identical** delivery, asserted by `sha256sum` inside each receiver vs. the source hash.

## This is an opt-in, slow, Docker-dependent tier

Every test is gated by `[E2EFact]`, which **skips** unless **both**:

1. the environment variable `CASTR_E2E` is set (to any non-empty value), **and**
2. a Docker daemon is reachable.

So a plain `dotnet test` (locally or in a CI stage that has not opted in) simply skips these — it does not
hang, does not require Docker, and does not slow the normal suite. Every test also carries
`[Trait("Category", "E2E")]` so a dedicated CI job can target exactly this tier.

## Running it

```bash
# PowerShell
$env:CASTR_E2E = "1"; dotnet test tests/Castr.Core.E2ETests --filter Category=E2E

# bash
CASTR_E2E=1 dotnet test tests/Castr.Core.E2ETests --filter Category=E2E
```

Requirements: Docker (Linux containers) with `--cap-add=NET_ADMIN` available for `tc netem`. The fixture
publishes `Castr.Cli` as a self-contained linux-x64 binary and bakes it into a small
`mcr.microsoft.com/dotnet/runtime-deps:10.0` image (plus `iproute2` and `coreutils`); the first run is
slower while that image builds.

## Scenarios

| Test | Receivers | Loss | Asserts |
|---|---|---|---|
| `SevenReceivers_NoLoss_AllReceiveByteIdenticalFile` | 7 | none | all 7 byte-identical |
| `FiveReceivers_UnderRealNetemLoss_RecoverViaRepair` | 5 | 20% real netem | all 5 byte-identical **and** netem actually dropped packets |
| `NineReceivers_UnderModerateLoss_AllRecoverByteIdentical` | 9 | 10% real netem | all 9 byte-identical, netem dropped packets |

## How loss is injected without breaking the protocol

`castr` broadcasts its signed MANIFEST exactly once and has no manifest re-request path, so a receiver that
misses it never initializes. Two design choices keep the loss tests deterministic:

- **Receivers start before the sender** (they pre-trust the sender via `castr trust add`, using an identity
  the test generates up front), so they are already listening when the one-shot manifest goes out.
- **Loss is applied only to MTU-sized chunk-carrying datagrams**, matched by IP total-length (`tc filter ...
  u32 match u16 0x0400 0xfc00 at 2`, i.e. IP datagrams of 1024–2047 bytes). Since M3, `Castr.Core` packetizes
  every chunk whose encrypted envelope exceeds the ~1200-byte wire-packet budget into MTU-safe wire packets
  (default 1200-byte payload => ~1228-byte IP datagrams), so chunk traffic is **no longer IP-fragmented** —
  the old "match the first IP fragment" filter would now match nothing. The proof-carrying packet 0 of each
  chunk lands in this 1024–2047 range; dropping it (or any packet) leaves the chunk incomplete, which the
  chunk-level `CHUNK_REQUEST`/`CHUNK_RESPONSE` repair re-requests in full. The small, sub-1024-byte control
  datagrams (manifest, key grant, chunk requests, peer-have) are left untouched, so the control plane gets
  through and repair does the rest. See the detailed rationale (and the M3-QA verification note) in
  `Infrastructure/CastrFanOut.cs`.
