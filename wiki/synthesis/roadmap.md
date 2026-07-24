---
type: synthesis
title: "Castr roadmap and milestone status"
tags: [milestone, decision]
sources: [castr-project-plan]
created: 2026-07-24
updated: 2026-07-24
---

# Castr roadmap and milestone status

**Read this page first when resuming work on Castr after a session restart.** This is the durable, cross-session task list — in-session `TodoWrite` state does not persist, this page does. Update it at the close of every milestone (and whenever milestone status changes) before ending a session or compacting context, per [[castr-project]]'s stated resumability requirement.

## Milestone status

| Milestone | Scope | Status |
|---|---|---|
| M0 — Scaffolding & spikes | Repo init, solution/project skeleton, LICENSE/.gitignore, `/wiki:init`, Ed25519 spike, mDNS/Info.plist spike | **Nearly done.** Repo initialized (git, Apache-2.0 LICENSE, .gitignore). Solution scaffolded: `Castr.Core`, `Castr.Core.Discovery`, `Castr.Cli`, `Castr.Tui` + matching test projects (`Castr.Core.Tests`, `Castr.Core.IntegrationTests`, `Castr.Core.E2ETests`, `Castr.Cli.Tests`) all build and the default scaffolded tests pass. Basic `ci.yml` (build+test matrix across ubuntu/windows/macos) added. `Castr.Gui*` intentionally left unscaffolded — see `src/Castr.Gui/PLACEHOLDER.md` — until M2/M4. `wiki/` initialized and this plan ingested as the first source. **Both M0 spikes complete**: see [[adr-0001-ed25519-library]] (NSec.Cryptography chosen; solution retargeted net8.0 → **net10.0 LTS** as a consequence, .NET 10 SDK installed) and [[adr-0002-mobile-discovery]] (native NsdManager/NWBrowser confirmed usable directly from C#, iOS Info.plist requirements documented). **M0 complete.** Initial commit pushed to `github.com:donaldsteele/Castr` on `main` (SSH auth configured via a dedicated ed25519 key for this machine, added to the user's GitHub account). QA subagent review of the full M0 scaffold found no defects (build/tests, project reference graph, git hygiene, LICENSE, wiki consistency, ADR fact-checks, CI workflow, GUI placeholder honesty — all verified independently, not just read).
| M1 — Castr.Core | Protocol state machines, chunker+Merkle/BLAKE3, manifest sign/verify, trust store+policy, path-safety, transport abstraction (real UDP + in-memory), `IPeerTable`/`RepairCoordinator`, full unit + loopback-multicast integration suite | **Complete, including QA fix.** See [[m1-core-summary]] for full detail. 167 unit tests (`Castr.Core.Tests`) + 4 real-socket integration tests (`Castr.Core.IntegrationTests`), all passing, verified stable across repeated runs. End-to-end `SenderSession`/`ReceiverSession` proven correct: happy path, 5-receiver fan-out from one send, untrusted-sender rejection, tampered-chunk detection + repair recovery, peer-served repair, and real-UDP-socket repair under 15% induced loss. QA subagent review attempted to break path safety / Merkle verification / signature checks (no bypass found) and did find + we fixed one real defect: a `UInt16` length-prefix overflow in `ChunkRequestMessage` encoding for repair batches over 65,535 chunks (realistic for large cold-start bulk repairs), widened to `UInt32`. Documented scope trims: single-pass carousel (no repeating rounds), repair request/response are multicast-broadcast rather than unicast-targeted with no NACK suppression. **Open risk carried to M3**: CI multicast behavior on GitHub-hosted runners is unverified — only confirmed working on this local Windows dev machine so far. |
| M1.5 — Payload encryption retrofit | X25519 keypairs, `JOIN_REQUEST`/`KEY_GRANT` handshake, ChaCha20-Poly1305 chunk encryption, Merkle-over-ciphertext, updated `SenderSession`/`ReceiverSession`, new tests | **Complete, including QA review.** See [[m1.5-encryption-summary]] for full detail. 186 unit tests (`Castr.Core.Tests`, up from 167) + 4 real-socket integration tests (`Castr.Core.IntegrationTests`), all passing. QA subagent independently rebuilt/re-ran the suite and actively verified tamper rejection, cross-context AAD rejection, per-file nonce uniqueness, MITM-proof manifest binding, and that the new handshake cannot bypass TOFU — no defects found. Two deliberate, QA-accepted deviations from the original wording in [[wire-protocol]]: `JOIN_REQUEST`/`KEY_GRANT` travel over the shared multicast channel (not a separate unicast socket), and the content key is generated outside `SenderSession`'s constructor and injected. `Castr.Core` now sends ciphertext-only chunk payloads. |
| M2 — CLI, TUI, Desktop GUI (parallel) | `Castr.Cli`, `Castr.Tui`, `Castr.Gui.Desktop`, each with its own test suite | **Complete, including QA review (PASS-WITH-CONCERNS).** See [[m2-ui-summary]] for full detail. Added a new `Castr.Core` observability contract first (`TransferProgress`/`ProgressChanged` events, `ITrustPrompt` interactive callback closing the previously-inert `TrustOutcome.PromptRequired` path) — QA'd separately before the three UI surfaces were built against it, no defects found. Then built in parallel: `Castr.Tui` (Spectre.Console `TransferDashboard`, library consumed by Cli), `Castr.Gui`/`Castr.Gui.Desktop` (newly scaffolded Avalonia MVVM app, verified via `Avalonia.Headless`), and `Castr.Cli` (System.CommandLine `send`/`receive`/`trust` commands, real loopback transfer personally verified). Full solution: 244 tests passing (Core 191, Cli 25, Tui 16, Gui 7, IntegrationTests 4, E2E 1), 0 warnings. QA's one non-blocking finding — a pre-existing M1 transport gap where large chunk sizes crash the sender and silently hang the receiver over real UDP (the documented chunk/wire-packet split was never implemented) — was mitigated with a `Castr.Cli` fail-fast `--chunk-size` guard rather than blocking M2 on a full Core fix; the underlying fix is tracked below and in [[wire-protocol]]. |
| M3 — Test/CI hardening | Full GitHub Actions matrix, Testcontainers E2E fan-out, security test pass (traversal/tamper/replay), `/wiki:lint` | Not started. **New scope item from M2**: implement the documented two-level chunk/wire-packet split in `Castr.Core`'s UDP transport (see [[wire-protocol]] and [[m2-ui-summary]]) so chunk sizes above ~65 KB work correctly — currently only safe because `Castr.Cli` defaults to 8 KB and fails fast on larger values, not because the transport actually handles them. |
| M4 — Mobile | `Castr.Core.Discovery` native mDNS impls, unicast swarm client, `Castr.Gui.Android`/`Castr.Gui.iOS`, sideload packaging | Not started. Requires `dotnet workload install android ios` (not installed as of M0). |
| M5 — Release automation & docs | Tag-triggered `release.yml`, self-contained per-RID publishes, checksums/signatures, GitHub Release notes, docs finalized | Not started. |

## Definition of done (every milestone)

Per [[castr-project-plan]]: tests passing, a `/wiki:ingest` summary committed, `graphify --update` run if code changed, and a QA subagent pass (an independent `Agent` spawned to exercise/review the milestone's tests and behavior) before moving to the next milestone.

## Open items carried forward

- **True Native AOT publish validation** (deferred from [[adr-0001-ed25519-library]]) — the M0 spike only validated trimming, not a full `PublishAot=true` build, because this dev machine lacks the native C++ linker toolchain. Install VS Build Tools (or validate on a machine that has them) before M4's iOS NativeAOT build.
- **Hands-on mobile discovery validation** (deferred from [[adr-0002-mobile-discovery]]) — real Android↔iOS `NsdManager`/`NWBrowser` interop over actual mDNS, plus exact Android 13+ runtime permission requirements, not yet confirmed hands-on. Do this at M4, once mobile workloads are installed.
- **Mobile workloads not installed** in the current dev environment — install and verify (`dotnet workload install android ios`) at the start of M4, not before.
- **CI multicast behavior unverified on GitHub-hosted runners** (new, from M1 — see [[m1-core-summary]]) — check this the first time `ci.yml` runs against M1's code, don't wait for the full M3 CI-hardening pass to discover it's broken.
- **Repeating carousel rounds and NACK-suppressed unicast-targeted repair** (new, from M1 — documented scope trims in [[m1-core-summary]]) — both are natural low-risk extensions to `SenderSession`/`ReceiverSession`, not required for correctness, worth revisiting opportunistically during M2/M3 rather than blocking on them now.
- **Manifest metadata (file names/sizes) remains unencrypted** now that M1.5 has landed — [[adr-0003-payload-encryption]] only encrypts chunk payloads by design. Revisit if a deployment needs filename/metadata confidentiality too; documented as a known, deliberate scope boundary, not an oversight.
- **Sender-side receiver allowlist** is a plausible future hardening noted in [[adr-0003-payload-encryption]] but not required now — currently any receiver that completes `JOIN_REQUEST` (i.e. already trusts the sender's signature) gets the content key, matching today's receiver-side-only authorization model.
- **Session-ID uniqueness is a caller contract, not yet enforced in `Castr.Core`** (new, from M1.5 QA — see [[m1.5-encryption-summary]]) — `ContentKeyWrap`'s HKDF salt is the transfer's session ID, so a caller reusing one across transfers between the same sender/receiver pair risks wrap-key reuse. `Castr.Cli` generates a fresh random session ID per invocation, so this is currently safe in practice, but `Castr.Core` still doesn't enforce it itself.
- **Two-level chunk/wire-packet split not implemented** (new, from M2 QA — see [[wire-protocol]] and [[m2-ui-summary]]) — chunks travel as single UDP datagrams instead of being split into MTU-safe wire packets. Chunk sizes whose ciphertext exceeds ~65507 bytes crash the sender and silently hang the receiver. Not blocking today only because every M2 UI surface defaults to 8 KB chunks and `Castr.Cli`'s `send` fails fast (exit 5) above a 64,984-byte chunk size (`receive` has no `--chunk-size` option to guard); implement the real fix in M3.
- **`Castr.Tui.Tests`' `TransferDashboardEndToEndTests` shows occasional transient failures under parallel test execution** (new, from M2 — see [[m2-ui-summary]]) — a real-UDP-timing test; passes standalone and on clean re-runs, `Castr.Tui` itself wasn't implicated. Worth a look during M3's CI-hardening pass.
- **`Castr.Tui`'s heatmap/throughput views are approximations** (new, from M2 — see [[m2-ui-summary]]) — `TransferProgress` only exposes aggregate counts, not the raw `ChunkBitmap` or per-peer byte counts, so the live dashboard shows completion density and aggregate MB/s rather than exact per-chunk/per-peer detail. Revisit if a richer Core progress API is ever justified.
- **`Castr.Gui.Tests` is the only xUnit v3 project in the repo** (new, from M2 — see [[m2-ui-summary]]) — required by `Avalonia.Headless.XUnit`; QA confirmed it coexists cleanly with the rest of the solution's xUnit v2 projects (no double-counting, no version conflicts) but it's worth knowing about before adding more test infra.

## Where this fits

- [[castr-project]]
- [[wire-protocol]]
- [[repair-protocol]]
- [[security-model]]
- [[tech-stack]]
- [[adr-0001-ed25519-library]]
- [[adr-0002-mobile-discovery]]
- [[adr-0003-payload-encryption]]
- [[m1-core-summary]]
- [[m1.5-encryption-summary]]
- [[m2-ui-summary]]
