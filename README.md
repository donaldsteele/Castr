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

M0 (scaffolding), M1 (core protocol), M1.5 (payload encryption retrofit), and
M2 (CLI, TUI, desktop GUI) are complete; M3 (test/CI hardening) is next. Chunk
payloads are ChaCha20-Poly1305-encrypted end-to-end — see
`wiki/synthesis/m1.5-encryption-summary.md` and `wiki/synthesis/adr-0003-payload-encryption.md`.
`castr send`/`castr receive`/`castr trust`, a live Spectre.Console dashboard
(`--tui`), and an Avalonia desktop GUI are all working — see
`wiki/synthesis/m2-ui-summary.md`. See `wiki/synthesis/roadmap.md` for milestone status and
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
- `tests/` — unit, loopback-multicast integration, multi-container E2E, and CLI
  test projects, one per corresponding `src/` project's concerns
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

## License

Apache-2.0 — see [LICENSE](LICENSE).
