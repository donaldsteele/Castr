---
type: synthesis
title: "M2 — CLI, TUI, Desktop GUI: implementation summary"
tags: [milestone, protocol, platform-quirk]
sources: [castr-project-plan]
created: 2026-07-24
updated: 2026-07-24
---

# M2 — CLI, TUI, Desktop GUI: implementation summary

M2 is complete: three independent UI surfaces now sit on top of the QA'd `Castr.Core` from [[m1.5-encryption-summary]] — `Castr.Cli`, `Castr.Tui`, and `Castr.Gui`/`Castr.Gui.Desktop` — plus a small new `Castr.Core` contract built specifically to unblock them. Full solution: **244 tests passing** (Core 191, Cli 25, Tui 16, Gui 7, IntegrationTests 4, E2E 1), 0 build warnings. QA verdict: **PASS-WITH-CONCERNS** — no blocking defects, one pre-existing `Castr.Core` transport gap surfaced and mitigated (see below).

## The Core contract that made parallel UI work possible

`src/Castr.Gui/PLACEHOLDER.md` had explicitly called out that GUI scaffolding was blocked on "a stable Castr.Core contract (observable progress stream + IInteractiveTrustPrompt) for it to bind against" — that gap is what M2 closed first, before any UI code:

- **`src/Castr.Core/Protocol/TransferProgress.cs`**: `TransferRole` (Sender/Receiver), `TransferPhase` (Starting/AwaitingKey/Transferring/Serving/Completed/TrustDenied), and a `TransferProgress` record (Role, Phase, TransferName, TotalFiles, TotalChunks, CompletedChunks, PendingChunks, TotalBytes, CompletedBytes, PeerCount, plus computed `FractionComplete`/`IsComplete`). Both `SenderSession` and `ReceiverSession` expose `public event Action<TransferProgress>? ProgressChanged;`, firing on chunk verify/send and lifecycle transitions.
- **`src/Castr.Core/Trust/ITrustPrompt.cs`**: `TrustPromptContext` (SenderId, TransferName, FileCount, TotalBytes) and `ITrustPrompt.RequestTrustAsync(...)`, wired into `ReceiverSession` as an optional trailing constructor parameter. This closes a real gap: `TrustOutcome.PromptRequired` existed in `TrustDecisionEngine` since M1 but nothing could ever act on it — it silently denied. Now, when a caller supplies an `ITrustPrompt` and the outcome is `PromptRequired` (only reachable with `UnknownSenderPolicy.Prompt` + `IsInteractive: true`), the session calls it; `true` upserts a `Trusted` entry and proceeds, `false` (or no prompt supplied) denies exactly as before.
- **Critical contract for implementers**: if `RequestTrustAsync` throws or is cancelled mid-prompt, `ReceiverSession.RunAsync` propagates the exception rather than emitting a graceful `TrustDenied` snapshot — every UI-side implementation must catch everything internally and resolve to `false`, never let an exception escape. QA confirmed both `Castr.Cli`'s `ConsoleTrustPrompt` and `Castr.Gui`'s `DialogTrustPrompt` honor this correctly; `Castr.Tui` doesn't implement `ITrustPrompt` at all, and a `--tui` session with `--on-unknown-sender prompt` correctly degrades to deny (via `IsInteractive: false`) rather than silently accepting or crashing.
- QA'd separately, before the three UI builds started: concurrency/locking in `SenderSession`'s progress counters (no torn reads), all four trust fail-safe paths (`Blocked`/`DeniedUnknown` untouched by the new code, throw-while-prompting fails closed, cancel-while-prompting fails closed, correct entry upserted on accept). No defects found.

## Castr.Tui

A Spectre.Console library (not its own executable — a dependency of `Castr.Cli`, matching the pre-existing `ProjectReference`). `TransferDashboard` drives a `LiveDisplay` from either a `SenderSession` or `ReceiverSession`'s `ProgressChanged` event; `TransferDashboardRenderer`/`ChunkHeatmap`/`ThroughputSampler` render it. Because `TransferProgress` only exposes aggregate counts (not the raw `ChunkBitmap` or per-peer byte counts), the heatmap shows completion **density** (green/yellow/grey blocks) rather than exact chunk positions, and throughput is aggregate MB/s rather than true per-peer — both are documented, deliberate approximations rather than a fuller Core API addition. 16 tests in `tests/Castr.Tui.Tests/`, including a real transfer driven end-to-end through `TransferDashboard.RunAsync` over `InMemoryNetwork`.

## Castr.Gui / Castr.Gui.Desktop

Scaffolded via the official Avalonia templates (`dotnet new install Avalonia.Templates`, `avalonia.mvvm` → `Castr.Gui`, `avalonia.app` → `Castr.Gui.Desktop`), then reshaped so `Castr.Gui` is a class library (shared views/viewmodels, depends only on `Castr.Core`) and `Castr.Gui.Desktop` is the thin executable head (depends only on `Castr.Gui`) — matching the dependency direction the original plan specified. MVVM via CommunityToolkit.Mvvm. Send tab builds a real signed manifest + content key + ciphertext Merkle tree and drives a real `SenderSession`; Receive tab drives a real `ReceiverSession` with a `FileSystemFileSink` and a repair loop; `DialogTrustPrompt` is a modal TOFU dialog shown only when policy is Prompt + interactive. Transport is abstracted behind `ITransportFactory` (`UdpTransportFactory` for the real app, `InMemoryTransportFactory` for tests). 7 tests in `tests/Castr.Gui.Tests/` using `Avalonia.Headless.XUnit` (the only xUnit v3 project in the repo — confirmed by QA to coexist cleanly with the rest of the solution's xUnit v2 projects, no double-counting or version conflicts) — genuine headless rendering/interaction checks, not stubs: window launches and renders, a pushed `TransferProgress` updates a real bound `ProgressBar`, the trust dialog responds to accept/reject, and a full in-memory transfer completes byte-identical end-to-end. The real UDP path and native file/folder pickers were verified only by code inspection (both need a live socket/TopLevel unavailable headlessly). `src/Castr.Gui/PLACEHOLDER.md` was replaced with an accurate README now that the project is real.

## Castr.Cli

System.CommandLine 2.0, assembly `castr`: `send <file>` (`--group/-g`, `--port/-p`, `--interface/-i`, `--chunk-size`, `--identity`, `--tui`), `receive` (`--dest-dir/-d`, `--on-unknown-sender deny|queue|prompt`, `--trust-store`, `--trust-seed`, `--group/--port/--interface`, `--tui`), `trust list|add|block|remove`. Exit codes: 0 success, 1 usage/parse error, 2 runtime error, 3 trust denied, 4 incomplete/cancelled, 5 invalid input. Headless mode prints throttled progress lines; `--tui` runs `Castr.Tui`'s `TransferDashboard` concurrently with the session via `Task.WhenAll`. `ConsoleTrustPrompt` wraps `AnsiConsole.Confirm`, catching everything to `false` per the contract above. 25 tests in `tests/Castr.Cli.Tests/`, including two real-UDP-multicast end-to-end tests (byte-identical transfer; untrusted-sender denial returning exit code 3). A real loopback send/receive of a 512 KB file was personally run by the implementer and independently re-verified during QA.

## The chunk-size / UDP transport gap (found during M2, pre-existing since M1)

Building a CLI against real sockets (rather than the in-memory test transport `Castr.Core.Tests` uses) surfaced a real, previously-latent defect: [[wire-protocol]]'s documented "two-level chunking" — chunks (256 KB–1 MB) split into ~1200-byte MTU-safe wire packets for actual UDP datagrams — was **never actually implemented** in M1. `SenderSession`/the UDP transports put an entire encrypted chunk into one datagram. QA reproduced this directly: on Windows, any datagram over 65507 bytes throws `SocketException` (`MessageSize`, error 10040). Concretely, at the documented 256 KB default: the **sender** fails loudly (a clear error, non-zero exit) on the first chunk send, but a **receiver** that already started listening hangs forever with a 0-byte `.part` file and no error — a genuine silent stall.

**Assessment (QA's, endorsed)**: not blocking for M2. All three surfaces default to 8 KB chunks, and a full transfer at that default was verified working end-to-end by both the Cli implementer and QA independently. The failure only occurs if a user explicitly overrides `--chunk-size` upward. **Mitigation shipped in M2**: `Castr.Cli`'s `send` command validates `--chunk-size` upfront (before file-exists, identity load, or any session/network activity) and fails fast with a clear message + exit code 5 if the resulting ciphertext would exceed a conservative safe-UDP-payload ceiling of 65,000 bytes (`MaxSafeUdpPayloadBytes` in `src/Castr.Cli/CastrPaths.cs`) — i.e. a max chunk size of 64,984 bytes (65,000 minus the ChaCha20-Poly1305 AEAD tag's fixed 16 bytes; confirmed by reading `ContentKey.EncryptChunk` that ciphertext size is always exactly plaintext+16, nonce is derived rather than carried). The 65,000 ceiling (vs. the theoretical 65,507-byte IPv4/UDP max) leaves headroom for the `ChunkDataMessage` wire envelope (session id, indices, length prefix, Merkle proof) around the ciphertext, without coupling the Cli-side check to Core's wire-format internals. Note `receive` has no `--chunk-size` option at all in this codebase — there's nothing to validate there. **Tracked for M3**: actually implement the documented two-level chunk/wire-packet split in `Castr.Core`'s transport layer so large chunk sizes work correctly across all consumers, not just ones that remember to validate upfront.

One more thing surfaced while adding this guard: a full solution re-run showed one **transient failure** in `Castr.Tui.Tests`' `TransferDashboardEndToEndTests` (a real-UDP-timing test) under parallel execution; it passed both standalone and on a clean full re-run, and `Castr.Tui` wasn't touched by this change — logged as pre-existing test flakiness, not a new defect, worth a look during M3's CI-hardening pass rather than right now.

## Where this fits

- [[roadmap]]
- [[wire-protocol]]
- [[tech-stack]]
- [[security-model]]
- [[m1-core-summary]]
- [[m1.5-encryption-summary]]
