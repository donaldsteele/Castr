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
| M0 — Scaffolding & spikes | Repo init, solution/project skeleton, LICENSE/.gitignore, `/wiki:init`, Ed25519 spike, mDNS/Info.plist spike | **Nearly done.** Repo initialized (git, Apache-2.0 LICENSE, .gitignore). Solution scaffolded: `Castr.Core`, `Castr.Core.Discovery`, `Castr.Cli`, `Castr.Tui` + matching test projects (`Castr.Core.Tests`, `Castr.Core.IntegrationTests`, `Castr.Core.E2ETests`, `Castr.Cli.Tests`) all build and the default scaffolded tests pass. Basic `ci.yml` (build+test matrix across ubuntu/windows/macos) added. `Castr.Gui*` intentionally left unscaffolded — see `src/Castr.Gui/PLACEHOLDER.md` — until M2/M4. `wiki/` initialized and this plan ingested as the first source. **Both M0 spikes complete**: see [[adr-0001-ed25519-library]] (NSec.Cryptography chosen; solution retargeted net8.0 → **net10.0 LTS** as a consequence, .NET 10 SDK installed) and [[adr-0002-mobile-discovery]] (native NsdManager/NWBrowser confirmed usable directly from C#, iOS Info.plist requirements documented). **Remaining before M0 closes**: initial git commit, decide when to push a public GitHub remote.
| M1 — Castr.Core | Protocol state machines, chunker+Merkle/BLAKE3, manifest sign/verify, trust store+policy, path-safety, transport abstraction (real UDP + in-memory), `IPeerTable`/`RepairCoordinator`, full unit + loopback-multicast integration suite | Not started. Largest, highest-risk milestone — nothing else starts until it's solid. |
| M2 — CLI, TUI, Desktop GUI (parallel) | `Castr.Cli`, `Castr.Tui`, `Castr.Gui.Desktop`, each with its own test suite | Not started. Depends on M1's stable Core contracts (observable progress stream + `IInteractiveTrustPrompt`). |
| M3 — Test/CI hardening | Full GitHub Actions matrix, Testcontainers E2E fan-out, security test pass (traversal/tamper/replay), `/wiki:lint` | Not started. |
| M4 — Mobile | `Castr.Core.Discovery` native mDNS impls, unicast swarm client, `Castr.Gui.Android`/`Castr.Gui.iOS`, sideload packaging | Not started. Requires `dotnet workload install android ios` (not installed as of M0). |
| M5 — Release automation & docs | Tag-triggered `release.yml`, self-contained per-RID publishes, checksums/signatures, GitHub Release notes, docs finalized | Not started. |

## Definition of done (every milestone)

Per [[castr-project-plan]]: tests passing, a `/wiki:ingest` summary committed, `graphify --update` run if code changed, and a QA subagent pass (an independent `Agent` spawned to exercise/review the milestone's tests and behavior) before moving to the next milestone.

## Open items carried forward

- **True Native AOT publish validation** (deferred from [[adr-0001-ed25519-library]]) — the M0 spike only validated trimming, not a full `PublishAot=true` build, because this dev machine lacks the native C++ linker toolchain. Install VS Build Tools (or validate on a machine that has them) before M1's manifest-signing code is considered fully de-risked, and definitely before M4's iOS NativeAOT build.
- **Hands-on mobile discovery validation** (deferred from [[adr-0002-mobile-discovery]]) — real Android↔iOS `NsdManager`/`NWBrowser` interop over actual mDNS, plus exact Android 13+ runtime permission requirements, not yet confirmed hands-on. Do this at M4, once mobile workloads are installed.
- **Mobile workloads not installed** in the current dev environment — install and verify (`dotnet workload install android ios`) at the start of M4, not before.
- **No GitHub remote configured yet** — repo is a local git init only as of M0; pushing to a public `Castr` GitHub repo (Apache-2.0, per [[castr-project]]) is a task for later in M0 or the start of M1, whenever the user is ready to make it public.

## Where this fits

- [[castr-project]]
- [[wire-protocol]]
- [[repair-protocol]]
- [[security-model]]
- [[tech-stack]]
- [[adr-0001-ed25519-library]]
- [[adr-0002-mobile-discovery]]
