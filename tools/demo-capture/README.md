# Demo capture scripts

PowerShell scripts that produced the real, hands-on-captured media in [`docs/SHOWCASE.md`](../../docs/SHOWCASE.md)
(and linked from the README's "See it in action" section). Kept here so the GIFs/screenshots can be
regenerated later — after a UI change, a new demo scenario, or just to refresh them for a release —
without re-deriving any of this from scratch.

Every capture is a **real** transfer: real loopback IP multicast, real chunking/BLAKE3/Merkle/ChaCha20-Poly1305,
real UI automation driving the actual shipped binaries. Nothing here is mocked or hand-edited after the fact.

## Prerequisites

- **Windows** (these scripts are Windows-only — `wt.exe`, Win32 window APIs, `ddagrab`).
- **ffmpeg** on `PATH`, built with `ddagrab` (DXGI Desktop Duplication) and `libx264` support. Check with
  `ffmpeg -hide_banner -filters | Select-String ddagrab`. The `gyan.dev` full builds have this; a stripped
  build might not — see "Why ddagrab, not gdigrab" below before swapping it out.
- **Windows Terminal** (`wt.exe`) on `PATH` — the default on Windows 11, and what the `castr`/`Castr.Tui`
  screenshots are captured through.
- **Release builds** of the projects you're capturing: `dotnet build -c Release` from the repo root before
  running anything here (the scripts fail fast with a clear error if `castr.exe` isn't found).

## Usage

```powershell
cd tools\demo-capture
.\New-DemoFiles.ps1        # one-time: creates workspace\, demo files, seeds CLI/TUI trust stores
.\capture-cli.ps1          # -> workspace\media\raw\cli-sysadmin.mp4
.\capture-tui.ps1          # -> workspace\media\raw\tui-lab-fanout.mp4
.\capture-gui.ps1          # -> workspace\media\raw\gui-lanparty.mp4 + two PNGs in workspace\media\frames\

.\ConvertTo-Gif.ps1 -InputMp4 workspace\media\raw\cli-sysadmin.mp4 -OutputGif ..\..\docs\media\sysadmin-fleet-push-cli.gif -Start 1.2 -End 23
.\ConvertTo-Gif.ps1 -InputMp4 workspace\media\raw\tui-lab-fanout.mp4 -OutputGif ..\..\docs\media\test-lab-tui-fanout.gif -Start 1.5 -End 53 -Fps 6 -Width 1300
.\ConvertTo-Gif.ps1 -InputMp4 workspace\media\raw\gui-lanparty.mp4 -OutputGif ..\..\docs\media\lan-party-desktop-gui.gif -Start 0.5 -End 49 -Fps 7
```

Copy `workspace\media\frames\gui-trust-dialog.png` and `gui-final-state.png` to `docs/media/` too (as
`trust-on-first-use-dialog.png` / `desktop-gui-transfer-complete.png`) if you're refreshing those.

`workspace\` (demo files, trust stores, raw recordings) is git-ignored — only the scripts and this README
are tracked. Re-running a capture script is idempotent: each one cleans its own prior output/process/window
state at the top before starting.

**Timing is throughput-dependent.** These demos push real bytes over real (loopback) UDP multicast; how far
a given recording duration (`-t` inside each script) gets before the clip ends depends on this machine's
actual throughput that run, which varies. If a capture ends mid-transfer instead of at "Completed", either
bump that script's `-t` value a little and re-run, or just trim `ConvertTo-Gif.ps1`'s `-End` to whatever
felt like a satisfying stopping point — a near-complete progress bar reads fine too.

## Known gotchas (found the hard way — don't rediscover these)

- **`gdigrab` desaturates Windows Terminal.** Windows Terminal is GPU-composited; `ffmpeg`'s legacy
  `gdigrab` (GDI `BitBlt`) captures it as near-monochrome — confirmed by a direct side-by-side test (a
  plain `System.Drawing` `CopyFromScreen` of the same window showed full color; `gdigrab` didn't). Use
  `ddagrab` (DXGI Desktop Duplication) for anything involving a Windows Terminal window. Plain GDI
  `CopyFromScreen` (used for the GUI demo's static PNGs) is fine for Avalonia windows — this is specifically
  a Windows Terminal/`gdigrab` interaction, not a general "GDI can't capture GPU-composited windows" rule.
- **Resizing a console window after `Castr.Tui`'s `LiveDisplay` starts painting corrupts it.**
  Spectre.Console's `LiveDisplay` measures the console once and repaints via relative cursor-up escape
  sequences; resizing the window mid-render shifts what those escapes land on, scrambling/overlapping the
  dashboard's borders and rows. Console geometry must be fixed **at launch** — `wt.exe`'s own `--size
  cols,rows` flag (not an in-session `mode con:`, which is a no-op for the outer window's pixel size — verified
  empirically) — and only ever *repositioned* afterward via `Set-WindowPosition` (`SWP_NOSIZE`), never resized.
  `WinPos.ps1`'s `Start-TitledConsole`/`Set-WindowPosition` encode this; don't reintroduce a post-launch
  `Set-WindowRect` (which resizes) on a `--tui` window.
- **`wt.exe --size` pixel dimensions were calibrated empirically**, not computed from font metrics: on this
  machine's default WT profile, `W(px) ≈ 9·cols + 49`, `H(px) ≈ 19·lines + 65`. If you're on a different
  machine/profile/font size, re-calibrate with two `--size` probes and `GetWindowRect` (see git history for
  the exact probe snippet) before trusting the `-Cols`/`-Lines` values baked into the capture scripts.
- **Windows Firewall's first-run prompt** for `Castr.Gui.Desktop.exe`/`castr.exe` will sit in front of
  everything if this is truly the first time either binary has bound a UDP socket on this machine. Click
  through it once (any network scope is fine for loopback) before running a capture for real — it won't
  reappear once granted.
- **The GUI demo's trust store is deliberately cleared every run** (`capture-gui.ps1` deletes
  `%APPDATA%\Castr\trusted-senders.json` at the top) so the real TOFU dialog reliably reappears on camera.
  The CLI/TUI demos do the opposite — `New-DemoFiles.ps1` pre-seeds their trust stores once so those two
  runs go straight to a clean transfer with no interactive prompt needed.

## Files

| File | What it does |
|---|---|
| `WinPos.ps1` | Shared window-automation helpers: find/position/close windows by title (via `EnumWindows`, not just `Process.MainWindowHandle`, which only reports one window even for multi-window host processes like Windows Terminal), launch a titled console via a generated `.bat` (avoids mangled quoting from threading a command through `wt.exe` → `cmd.exe` → the target's own arg parser). |
| `New-DemoFiles.ps1` | One-time setup: workspace dirs, three sparse demo files, one shared demo sender identity, pre-seeded CLI/TUI trust stores. |
| `capture-cli.ps1` | Sysadmin fleet push — two `castr.exe` consoles side by side. |
| `capture-tui.ps1` | Test lab load — one `castr.exe --tui` sender fanning out to three independent `castr.exe --tui` receivers, 2×2 grid. |
| `capture-gui.ps1` | LAN party — two `Castr.Gui.Desktop.exe` instances driven via UI Automation, including the real TOFU trust dialog. |
| `ConvertTo-Gif.ps1` | Shared palette-based `.mp4` → `.gif` conversion (two-pass `palettegen`/`paletteuse`). |
