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

### ⚠️ The image is cached, so a run can silently validate stale code

`CastrClusterFixture` builds the image as `castr-e2e-tests:latest` with `WithCleanUp(false)`, and
Testcontainers **skips the build entirely when an image of that name already exists**. That is deliberate —
the publish step is the slow part — but it means a run after a code change can exercise a *previous*
build's binary and pass. This actually happened during M8: a 3.5-hour-old image would have "validated" the
old chunk-size default.

**Before any E2E run whose result depends on a code change, delete the image first:**

```bash
docker rmi -f castr-e2e-tests:latest
```

### Docker Desktop endpoint on Windows (handled automatically)

When the active Docker context is `desktop-linux`, Testcontainers probes the legacy `docker_engine` named
pipe and **hangs indefinitely rather than failing**. `CastrClusterFixture.ConfigureDockerEndpoint` sets
`DOCKER_HOST` in-process to `npipe://./pipe/dockerDesktopLinuxEngine` when that pipe exists and nothing has
already chosen an endpoint, so no machine-level configuration is needed.

Note the slash counts differ by consumer and this is not a typo: the docker **CLI** accepts only
`npipe:////./pipe/...`, while **Docker.DotNet** accepts only `npipe://./pipe/...`. Since the skip-gate in
`E2EFactAttribute` shells out to the CLI, exporting a single `DOCKER_HOST` value for both breaks one of
them — which is why this is set in-process, after that probe, rather than in your shell.

### A second hang that looks identical, and is not Docker's fault (fixed)

`PublishCli()` used to call `StandardOutput.ReadToEnd()` before `WaitForExit()`. MSBuild defaults to
`nodeReuse:true`, so the long-lived MSBuild node processes left behind by any earlier `dotnet build` or
`dotnet test` **inherit the redirected pipe handles** of the `dotnet publish` child. The pipe therefore never
reaches EOF and `ReadToEnd()` blocks forever — even though the publish has already exited and written its
output to disk.

The symptom is tens of minutes of a hung run with **zero Docker activity**, which is almost exactly the
symptom of the npipe hang above, so it is easy to misdiagnose. If you ever see that shape again, get a managed
stack (`dotnet-stack report -p <testhost-pid>`) before assuming it is Testcontainers — that is what identified
this one.

The fixture now drains both streams with `BeginOutputReadLine`/`BeginErrorReadLine` and waits on process exit
with a timeout, so an inherited handle holding the pipe open no longer matters. `MSBUILDDISABLENODEREUSE=1`
was the workaround before the fix and is no longer needed.

## Scenarios

| Test | Receivers | Payload | Chunks | Loss | Asserts |
|---|---|---|---|---|---|
| `SevenReceivers_NoLoss_AllReceiveByteIdenticalFile` | 7 | 4 MB | 16 | none | all 7 byte-identical |
| `FiveReceivers_UnderRealNetemLoss_RecoverViaRepair` | 5 | **64 MB** | **256** | 20% real netem | all 5 byte-identical **and** netem actually dropped packets |
| `NineReceivers_UnderModerateLoss_AllRecoverByteIdentical` | 9 | 16 MB | 64 | 10% real netem | all 9 byte-identical, netem dropped packets |

**Payloads are sized in chunks, not bytes, and that matters.** These were one shared 4 MB constant, which was
512 chunks at the old 8 KiB default and silently became **16** when M8 raised the default to 256 KiB. At 16
chunks most of the repair machinery this tier defends is unreachable: `MaxChunksPerRequest` = 268 fits a
whole file in one request so multi-batch splitting never runs, `MaxRequestsPerPass` = 4 can never bind, and
the carousel watermark has 16 positions. The 5-receiver case is now the many-chunk case. **If the default
chunk size changes again, re-derive these payloads from the chunk count you want, not the byte count.**

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

### ⚠️ How much this filter drops depends on the file's chunk *count*, not just the loss percentage

Non-obvious, discovered while raising the payloads in M8, and worth knowing before reading a drop count as a
loss rate. `ChunkPacketizer.Split` sizes every fragment as
`maxDatagramPayload - FixedEnvelopeOverhead - ProofEncodedSize(proof)`, and the Merkle proof grows with the
file's chunk count — so **more chunks means smaller fragments**, which can fall out of the filter's
1024–2047 byte window:

| Chunks | Proof steps | Non-packet-0 IP datagram | In filter window? |
|---|---|---|---|
| 16 (4 MB) | 4 | ~1086 bytes | **yes — every packet is dropped** |
| 256 (64 MB) | 8 | ~954 bytes | **no — only packet 0 is dropped** |

Both are valid repair exercises, because the proof rides only on packet 0 and a chunk cannot be assembled
without it, so losing packet 0 strands the whole chunk. But they are *different* exercises, and the raw
`netem-dropped` figure is not comparable across payload sizes: 20% loss on 256 chunks drops ~51 packets
(≈20% of *chunks*), where 20% on 16 chunks dropped thousands (≈20% of *packets*). Assert on
`NetemDroppedPackets > 0` and on byte-identity, as these tests do — do not read the count as a loss rate.
