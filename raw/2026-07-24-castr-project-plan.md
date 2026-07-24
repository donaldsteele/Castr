# Castr — Multi-OS LAN Multicast File Transfer Tool

## Context

We are starting a brand-new project (`c:\code\Castr`, currently empty, no git repo) to build **Castr**: a cross-platform tool that lets a trusted sender broadcast a file once over LAN IP multicast and have any number of receivers pick it up simultaneously, with cryptographic trust, chunk-level integrity, and peer-assisted repair of lost chunks. It must run on Windows, macOS, Linux, iOS, and Android; ship as a CLI with a colorful TUI, and optionally a GUI; and be released publicly on GitHub with a full automated test suite.

This plan was produced after two rounds of clarifying questions and a dedicated architecture-design pass (via a Plan subagent). Key decisions already locked in:

- **Mobile transport**: iOS/Android cannot reliably join true IP multicast (Apple gates the entitlement; Android restricts it). Mobile is a **unicast swarm client** tier — discovers peers via native mDNS/Bonjour (NsdManager / NWBrowser) and pulls chunks over unicast TCP from any peer that has them, using the *same* chunk/hash/trust model as the multicast tier.
- **GUI framework**: **Avalonia UI** (only realistic single-codebase option spanning Win/Mac/Linux desktop *and* Android/iOS — .NET MAUI is disqualified for lacking Linux desktop support).
- **Payload security**: **integrity + authenticity only**, no encryption. Sender signs a manifest (Ed25519); every chunk is hashed. This is a trusted-LAN tool, not a confidential-transport tool.
- **Path safety**: sender-suggested relative paths may never contain `..` (no upward traversal — subdirectories going down are fine); a receiver-configured explicit absolute destination always takes precedence over any sender hint.
- **Unknown-sender policy**: configurable, **default-deny** in headless/non-interactive mode (`--on-unknown-sender=deny|queue|prompt`), interactive prompt available in TUI/GUI.
- **Build phasing**: protocol core first (fully tested in isolation), then CLI/TUI/desktop-GUI in parallel once the core is stable, mobile last.
- **Repo**: `Castr`, public, **Apache-2.0** license (explicit patent grant, given this is a crypto/protocol-bearing tool).
- **Mobile distribution v1**: sideload only (Android APK via GitHub Releases; iOS build artifact / local install, no App Store submission) — avoids paid developer account dependency for v1.
- **graphify + llm-wiki are mandatory, ongoing infrastructure for this project**, not optional tooling — see the dedicated section below. They exist specifically to solve the user's stated concerns about context-window usage and session resumability.

---

## Technical Architecture

### Wire protocol

Two-level model: **chunks** (256 KB–1 MB, the hash/repair granularity) are split into **wire packets** (~1200 bytes, MTU-safe) for actual UDP datagrams. Message types, all over a configurable administratively-scoped multicast group (default `239.192.55.55`, TTL=1 by default):

| Message | Direction | Purpose |
|---|---|---|
| `ANNOUNCE` | sender → multicast (periodic) | session ID, sender pubkey ID, Merkle root, transfer name, issued-at |
| `MANIFEST` | sender → multicast/unicast | full signed manifest: per-file Merkle root, chunk size/count, signature |
| `CHUNK_DATA` | sender → multicast (carousel) | file/chunk index, payload, Merkle inclusion proof |
| `PEER_HAVE` | receiver → multicast (desktop) / mDNS+gossip (mobile) | per-file chunk bitmap + receiver endpoint — doubles as free peer discovery |
| `CHUNK_REQUEST` / `CHUNK_RESPONSE` | receiver ↔ peer or sender | targeted repair; **on desktop, repair responses are multicast** (rate-limited) so one repair fixes every receiver with that gap — this is what makes "hundreds of receivers, one send" hold up even under loss |
| `TRANSFER_COMPLETE` | receiver → sender/multicast | status telemetry |

**Manifest = signed Merkle root over BLAKE3 chunk hashes**, not a flat signed hash list. This keeps the re-broadcast manifest at a fixed ~32 bytes regardless of file size, and — critically — lets a receiver verify a chunk that arrived from an **untrusted peer** during repair: only the sender's signature over the root needs to be trusted; any peer can hand over a chunk + inclusion proof and it self-verifies.

Replay protection: session ID (16 random bytes) + freshness window on `issued-at`; trust is keyed to the sender's Ed25519 public key fingerprint, not the session, so replay of a legitimate announce is low-severity (worst case: redundant hash-checked rewrite).

### Repair protocol

1. Receiver tracks a per-file bitmap of verified chunks.
2. On stall/gap, ranks candidate peers (not-original-sender first, most-complete-file, jitter tiebreak) from an `IPeerTable`.
3. Splits missing indices across multiple peers in parallel; short timeout, retry against a different peer on failure.
4. **Desktop**: repair responses are multicast (NORM/FLUTE-style), so one fulfilled request self-heals every straggler with the same gap. Requests can also be multicast with randomized-delay suppression (don't ask if you just heard someone else ask).
5. **Mobile**: no multicast available — peer discovery via native mDNS, repair strictly unicast.
6. Falls back to the original sender only if no peer answers. Failure modes handled: sender-offline-with-no-peers (surface stalled status, keep `.part` file, auto-resume if anyone reappears), peer-goes-offline-mid-repair (timeout + retry + TTL-expiring peer table), thundering herd (jitter + backoff + multicast-repair fan-out).

`IPeerTable` is populated differently per tier (multicast gossip vs. mDNS) but consumed identically by one `RepairCoordinator` — this abstraction **must** be designed during the core milestone even though mobile is built last, or mobile work forces a repair-coordinator rework later.

### Security posture

- Ed25519 signatures over the manifest root; trust store is TOFU (trusted / blocked / unknown) keyed by public key fingerprint.
- Pre-deployment: a seed trust file (`trusted-senders.seed.json`) ships alongside the receiver package/config and is merged into the local trust store on first run; the local store is authoritative afterward.
- Path safety enforced at the receiver: reject any sender-suggested path containing `..`; explicit receiver-configured absolute destination always wins.
- No payload encryption (confirmed decision) — chunk integrity via BLAKE3 leaves in the Merkle tree; a cheap CRC32C pre-filter may be used only as a non-security-relevant first-pass garbage filter before paying for BLAKE3+proof verification.

### Tech stack

| Concern | Choice | Note |
|---|---|---|
| Signing | Ed25519 — **spike required** (NSec/libsodium vs. built-in `System.Security.Cryptography` Ed25519 vs. pure-managed fallback) | Open risk: verify iOS NativeAOT/trimming compatibility **and** license compatibility with Apache-2.0 distribution before committing. Resolve in M0. |
| Hashing | BLAKE3 (managed, SIMD, no native dep) | Chosen over SHA-256 (slower) and CRC32C-only (not collision-resistant, unsafe once peers relay chunks) |
| Transport | Raw `System.Net.Sockets.Socket`, not `UdpClient` | Needed for explicit `AddMembership`/interface index, `MulticastLoopback` for tests, `ReuseAddress` |
| Mobile discovery | Platform-native only: Android `NsdManager`, iOS/macOS `NWBrowser`/Bonjour, behind `IServiceDiscovery` | Pure-managed mDNS libs are themselves multicast-socket-based and hit the same iOS restriction — native APIs are the only real option |
| CLI | System.CommandLine 2.0 (GA) | |
| TUI | Spectre.Console (`LiveDisplay`/`Progress`, custom chunk-bitmap heatmap renderable) | |
| GUI | Avalonia (desktop head solid; **Android/iOS heads are beta-quality as of 2026** — flagged risk, informs sequencing) | |

### Platform quirks to design around (not fix in code review later)

Windows requires binding `Any` then joining with explicit interface (can't bind the multicast address directly like Linux); Linux CI can use multiple loopback aliases (`127.0.0.2`, `.3`, …) for true multi-instance tests, Windows effectively can't; macOS interface auto-selection is unreliable with VPN/virtual adapters present — always enumerate `NetworkInterface.GetAllNetworkInterfaces()` and let the user override; Windows Defender commonly blocks inbound UDP on first run — plan signed installer + firewall guidance as a doc/UX task, not just code; iOS requires `NSLocalNetworkUsageDescription` + `NSBonjourServices` in Info.plist for mDNS — easy to miss in dev since Xcode debug builds can bypass the entitlement check, only surfacing at distribution time.

---

## Repo / Solution Layout

```
Castr.sln
src/
  Castr.Core/                 protocol state machines, chunker, Merkle/manifest, trust store, transport abstractions + real UDP impl
  Castr.Core.Discovery/       IServiceDiscovery + platform mDNS impls
  Castr.Cli/                  System.CommandLine entrypoint, headless mode, policy/config
  Castr.Tui/                  Spectre.Console live dashboard over Core's observable session model
  Castr.Gui/                  Avalonia shared views/viewmodels
  Castr.Gui.Desktop/          Avalonia desktop head (Win/Mac/Linux)
  Castr.Gui.Android/          Avalonia Android head
  Castr.Gui.iOS/              Avalonia iOS head
tests/
  Castr.Core.Tests/           pure unit tests (no sockets) — protocol state machines, chunker, trust policy, path-safety, manifest signing
  Castr.Core.IntegrationTests/ real UDP loopback multicast + ChaosTransport (seeded loss/reorder/duplicate/delay)
  Castr.Core.E2ETests/        Testcontainers/docker-compose multi-node fan-out (5-9 Linux containers, netem-induced kernel-level loss)
  Castr.Cli.Tests/            CLI parsing/behavior tests
.github/workflows/            ci.yml (build+unit+integration matrix), e2e.yml (opt-in slower stage), release.yml (tag-triggered)
wiki/  raw/                   llm-wiki knowledge base (see below)
graphify-out/                 generated codebase graph (see below)
trusted-senders.seed.json     example/template pre-deployment trust seed
README.md  LICENSE (Apache-2.0)  CONTRIBUTING.md
```

Dependency direction: `Cli`, `Tui`, `Gui.*` depend only on `Castr.Core` (+ `Castr.Core.Discovery` for mobile heads). `Tui` and `Gui` have no dependency on each other — both are independent consumers of Core's stable contracts (observable progress stream + `IInteractiveTrustPrompt` callback), which is what makes building them in parallel valid, provided those contracts are finalized before UI work starts.

Core testability hinges on one design move: protocol logic (`SenderSession`, `ReceiverSession`, `RepairCoordinator`, `TrustDecisionEngine`) is modeled as pure `(State, Event) -> (State, Effect[])` state machines driven by an injected `ISystemClock`, never touching `Socket` or `Task.Delay` directly — this is what makes repair/trust/replay edge cases assertable in microseconds instead of flaky real-time tests.

---

## graphify + llm-wiki: mandatory project infrastructure

These aren't bolted on — they're how we satisfy the user's explicit requirement that "each step is well documented and resumable" and that context usage is managed by "writing the context needed into the plan and tasks" rather than re-deriving it every session.

- **M0**: run `/wiki:init` at repo root (creates `wiki/` + `raw/`). Every architectural decision in this plan (protocol design, library choices, the NSec spike outcome, etc.) gets ingested as a source via `/wiki:ingest` — this plan file itself is the first ingest.
- **After every milestone**: `/wiki:ingest` a session summary (decisions made, open issues, next steps) and update a durable `wiki/synthesis/roadmap.md` tracking milestone status — this is the cross-session task list, since in-session `TodoWrite` state doesn't persist across a restart.
- **After M1** (once there's real code) and periodically thereafter: run `/graphify` to build/update `graphify-out/graph.json` + `GRAPH_REPORT.md`. Future sessions resuming this project should run `graphify query "<question>"` against the existing graph instead of re-reading the whole codebase — this is the concrete mechanism for keeping context usage down.
- Before a session ends or context is getting large: ingest a wrap-up into the wiki, then `/compact`. The wiki + graph are the durable memory; the conversation is disposable.
- `/wiki:lint` runs as part of the M3 hardening pass to catch orphaned/stale documentation before the v1 release.

---

## Milestones

Each milestone's definition of done includes: tests passing, a `/wiki:ingest` summary committed, `graphify --update` run if code changed, and a QA subagent pass (spawn an `Agent` to independently review and exercise the milestone's tests/behavior before moving on — this is the explicit "spin up a QA agent" requirement, applied at every milestone, not just once).

- **M0 — Scaffolding & spikes.** Repo init, solution/project skeleton (empty projects per the layout above), `.gitignore`, LICENSE (Apache-2.0), `/wiki:init`. Resolve the two flagged risks concretely: (1) Ed25519 library — validate NSec (or alternative) under iOS NativeAOT/trimming *and* confirm its license permits Apache-2.0 redistribution, decide and document via ADR in the wiki; (2) confirm native mDNS behavior and required iOS Info.plist entries. Ingest both spike outcomes into the wiki.
- **M1 — Castr.Core.** Protocol state machines, chunker + Merkle/BLAKE3, manifest sign/verify, trust store + policy engine, path-safety enforcement, transport abstraction with real UDP multicast + in-memory transport, `IPeerTable`/`RepairCoordinator` (designed for both tiers now, multicast population implemented, mDNS population stubbed for M-mobile). Full unit suite + loopback integration suite with `ChaosTransport`. This is the largest, highest-risk milestone — nothing else starts until it's solid.
- **M2 — CLI, TUI, Desktop GUI (parallel).** Three independent consumers of Core's stable contracts: `Castr.Cli` (System.CommandLine, headless policy flags), `Castr.Tui` (Spectre.Console live status/heatmap), `Castr.Gui.Desktop` (Avalonia, Win/Mac/Linux). Each ships with its own test suite (CLI parsing/behavior, TUI rendering/behavior where feasible, GUI smoke tests).
- **M3 — Test/CI hardening.** GitHub Actions matrix (ubuntu/windows/macos-latest) for unit+integration; Testcontainers-based E2E fan-out job (opt-in/slower stage); security-focused test pass (path-traversal attack cases, signature-tamper cases, replay cases, trust-store tamper cases); `/wiki:lint`.
- **M4 — Mobile.** `Castr.Core.Discovery` native mDNS implementations, unicast swarm client logic, `Castr.Gui.Android`/`Castr.Gui.iOS` heads (treated separately from M2's desktop GUI given Avalonia mobile's beta-maturity risk), sideload packaging (APK via Releases; iOS local/artifact build).
- **M5 — Release automation & docs.** Tag-triggered `release.yml`: self-contained `dotnet publish` per RID (win-x64, osx-x64/arm64, linux-x64), checksums + detached signatures of release artifacts themselves, GitHub Release notes, finalized README/CONTRIBUTING, wiki finalized as living docs, graphify graph committed/refreshed for v1.

---

## Verification approach

- **Unit**: `dotnet test` on `Castr.Core.Tests` — protocol state machines, Merkle/manifest, trust policy (including the default-deny/queue/prompt matrix), path-safety (explicit `..`-traversal attack fixtures), all deterministic and socket-free.
- **Integration**: `Castr.Core.IntegrationTests` against real loopback UDP multicast with `ChaosTransport`-injected loss/reorder/duplication, asserting repair-protocol convergence.
- **E2E**: Testcontainers-driven multi-container fan-out running the actual shipped `Castr.Cli` binary, asserting byte-identical output files across all receivers, including kernel-level (`netem`) induced loss.
- **Security**: dedicated test pass for path traversal, signature tampering, replay, and trust-store tamper scenarios as part of M3.
- **QA agent gate**: at the close of every milestone, spawn a QA-focused subagent to independently exercise and review that milestone's code and tests before the next milestone begins.
- **Manual/cross-platform sanity**: run the CLI on real Windows/macOS/Linux hosts on a shared physical/virtual LAN segment at least once before M5 release, since Docker bridge-network multicast fidelity is a proxy for, not a replacement for, real hardware/Wi-Fi behavior (especially IGMP snooping switches).
