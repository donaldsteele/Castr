# Castr.Gui (not yet scaffolded)

This project — the shared Avalonia views/viewmodels consumed by `Castr.Gui.Desktop`,
`Castr.Gui.Android`, and `Castr.Gui.iOS` — is intentionally **not** scaffolded yet.

Why: scaffolding it now would pull in the Avalonia template package and NuGet
dependencies before there's a stable `Castr.Core` contract (observable progress
stream + `IInteractiveTrustPrompt`) for it to bind against, and before the milestone
that owns this work has started. See the project plan, milestone M2 (desktop head)
and M4 (mobile heads, gated separately due to Avalonia mobile's beta-maturity risk
as of 2026).

To scaffold when M2 starts:

```
dotnet new install Avalonia.Templates
dotnet new avalonia.mvvm -n Castr.Gui -o src/Castr.Gui
dotnet new avalonia.app -n Castr.Gui.Desktop -o src/Castr.Gui.Desktop
dotnet sln add src/Castr.Gui/Castr.Gui.csproj src/Castr.Gui.Desktop/Castr.Gui.Desktop.csproj
```

`Castr.Gui.Android` and `Castr.Gui.iOS` additionally require the mobile workloads
(`dotnet workload install android ios`), which are not installed in this
environment as of the M0 scaffolding pass — install and verify as the first task
of M4.
