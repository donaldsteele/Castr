# Castr

A cross-platform LAN multicast file transfer tool. A trusted sender broadcasts a
file once over IP multicast; any number of receivers on the network pick it up
simultaneously, verify it via a signed, chunk-hashed manifest, decrypt it (chunk
payloads are encrypted end-to-end with ChaCha20-Poly1305, not just signed — see
below), and repair any lost chunks by requesting them from peer receivers rather
than re-asking the sender.

Runs on Windows, macOS, and Linux with full IP multicast; iOS and Android join the
same swarm as unicast clients over LAN-discovered peers, since neither OS allows
apps to reliably join true multicast groups.

## Status

M0 (scaffolding), M1 (core protocol), M1.5 (payload encryption retrofit),
M2 (CLI, TUI, desktop GUI), and M3 (test/CI hardening) are complete; M4
(mobile) is next. Chunk payloads are ChaCha20-Poly1305-encrypted end-to-end
and travel over MTU-safe wire packets (chunks split/reassembled below the
crypto layer) — see `wiki/synthesis/m1.5-encryption-summary.md` and
`wiki/synthesis/m3-test-ci-hardening-summary.md`.
`castr send`/`castr receive`/`castr trust`, a live Spectre.Console dashboard
(`--tui`), and an Avalonia desktop GUI are all working — see
`wiki/synthesis/m2-ui-summary.md`. CI runs a real 3-OS build+test matrix plus
a Docker-gated multi-container E2E fan-out job with real induced packet loss —
see `wiki/synthesis/m3-test-ci-hardening-summary.md`. See `wiki/synthesis/roadmap.md` for milestone status and
`wiki/` generally for the accumulated design decisions (ADRs, spike results). The
full architecture — wire protocol, repair algorithm, security model, and milestone
plan — lives in the project plan; a synthesis of it is ingested into `wiki/` as the
project's first source so it survives session restarts.

## Repo layout

- `src/Castr.Core` — protocol state machines, chunker, Merkle/manifest, trust
  store, transport abstractions (no UI, no platform-specific code)
- `src/Castr.Core.Discovery` — peer discovery abstraction + platform mDNS impls
  (used by the mobile unicast tier)
- `src/Castr.Cli` — command-line entrypoint (System.CommandLine): `send`,
  `receive`, `trust list|add|block|remove`
- `src/Castr.Tui` — colorful live transfer dashboard (Spectre.Console),
  consumed by `Castr.Cli --tui`
- `src/Castr.Gui`, `src/Castr.Gui.Desktop` — Avalonia GUI: shared
  views/viewmodels and the Windows/macOS/Linux desktop head; mobile heads
  (`Castr.Gui.Android`/`Castr.Gui.iOS`) are M4
- `tests/` — unit, loopback-multicast integration, CLI, TUI, and GUI test
  projects, one per corresponding `src/` project's concerns, plus
  `Castr.Core.E2ETests`: a Testcontainers-driven multi-container fan-out suite
  (real Docker bridge multicast + kernel-level `tc netem` loss), opt-in via
  `CASTR_E2E=1` and gated on Docker being reachable
- `wiki/`, `raw/` — the project's persistent knowledge base (llm-wiki), the
  durable memory across sessions
- `graphify-out/` — generated codebase knowledge graph (query this instead of
  re-reading the whole tree when resuming work)

## Building

```
dotnet build
dotnet test
```

Requires the .NET 10 SDK (LTS, pinned via `global.json`).

Every push to `main` also builds self-contained, per-platform zips of `castr` (CLI) and the desktop GUI for
win-x64, win-arm64, osx-x64, osx-arm64, and linux-x64, uploaded as downloadable artifacts on that CI run's
Actions page (see the `package` job in `.github/workflows/ci.yml`). These are unsigned CI convenience builds,
not versioned/checksummed releases — that's tracked for M5.

## License

Apache-2.0 — see [LICENSE](LICENSE).
