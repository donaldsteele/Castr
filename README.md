# Castr

A cross-platform LAN multicast file transfer tool. A trusted sender broadcasts a
file once over IP multicast; any number of receivers on the network pick it up
simultaneously, verify it via a signed, chunk-hashed manifest, and repair any lost
chunks by requesting them from peer receivers rather than re-asking the sender.

Runs on Windows, macOS, and Linux with full IP multicast; iOS and Android join the
same swarm as unicast clients over LAN-discovered peers, since neither OS allows
apps to reliably join true multicast groups.

## Status

M0 (scaffolding) and M1 (core protocol) complete; M2 (CLI/TUI/desktop GUI) not yet started. See `wiki/synthesis/roadmap.md` for milestone status and
`wiki/` generally for the accumulated design decisions (ADRs, spike results). The
full architecture — wire protocol, repair algorithm, security model, and milestone
plan — lives in the project plan; a synthesis of it is ingested into `wiki/` as the
project's first source so it survives session restarts.

## Repo layout

- `src/Castr.Core` — protocol state machines, chunker, Merkle/manifest, trust
  store, transport abstractions (no UI, no platform-specific code)
- `src/Castr.Core.Discovery` — peer discovery abstraction + platform mDNS impls
  (used by the mobile unicast tier)
- `src/Castr.Cli` — command-line entrypoint (System.CommandLine)
- `src/Castr.Tui` — colorful live transfer dashboard (Spectre.Console)
- `src/Castr.Gui*` — Avalonia GUI (desktop + mobile heads); not yet scaffolded,
  see `src/Castr.Gui/PLACEHOLDER.md`
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
