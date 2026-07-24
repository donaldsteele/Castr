# Castr.Gui

Shared Avalonia (MVVM) views and view-models for Castr's graphical heads. Scaffolded in **M2** with the
official Avalonia templates (`dotnet new avalonia.mvvm`), now depending on `Castr.Core`.

- **`Castr.Gui`** (this project) — a class library holding `App`, the views, and the view-models. It is
  consumed by the desktop head today and, in M4, by the `Castr.Gui.Android` / `Castr.Gui.iOS` heads.
- **`Castr.Gui.Desktop`** — the Windows/macOS/Linux executable head; a thin `Program.cs` that bootstraps
  `Castr.Gui.App` via `Avalonia.Desktop`.

## What it does

- **Send** — pick a file, build a signed manifest + per-transfer content key + ciphertext Merkle tree
  (`Services/TransferBuilder`), and drive a real `SenderSession` over multicast.
- **Receive** — choose a destination folder and an unknown-sender policy (Deny / Prompt / Queue), then drive
  a real `ReceiverSession` with a periodic repair loop.
- **Trust prompt** — when the policy is "Prompt me", an unknown sender triggers `DialogTrustPrompt`
  (`ITrustPrompt`), a modal TOFU dialog. It never throws: internal errors and cancellation resolve to a
  graceful *deny*, because `ReceiverSession` propagates any exception out of its receive loop.

Session `ProgressChanged` callbacks fire off the UI thread and are marshaled through `Dispatcher.UIThread`
before touching bound state (`TransferProgressViewModel`).

Transport is abstracted behind `ITransportFactory` (`UdpTransportFactory` in the app,
`InMemoryTransportFactory` for design-time/tests) so the identical view-model + session path can run
end-to-end headlessly.

Tests live in `tests/Castr.Gui.Tests` (Avalonia.Headless + xUnit).
