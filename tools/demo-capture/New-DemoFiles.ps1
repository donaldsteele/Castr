# One-time setup for the demo-capture scripts: creates the workspace directory tree, three
# realistically-sized (sparse-allocated, so this is instant) demo files matching each showcase
# scenario, and pre-seeds the CLI/TUI receiver trust stores with the shared demo sender identity so
# those two demos run cleanly with no interactive trust prompt (the GUI demo deliberately leaves its
# trust store empty every run, via capture-gui.ps1, so the real TOFU dialog appears on camera).
#
# Usage: .\New-DemoFiles.ps1
# Then:  .\capture-cli.ps1 ; .\capture-tui.ps1 ; .\capture-gui.ps1
# Set $env:CASTR_DEMO_ROOT to override the workspace location (default: .\workspace next to this script).
$ErrorActionPreference = 'Stop'

$Root = if ($env:CASTR_DEMO_ROOT) { $env:CASTR_DEMO_ROOT } else { Join-Path $PSScriptRoot "workspace" }
$RepoRoot = Resolve-Path (Join-Path $PSScriptRoot "..\..")
$Castr = Join-Path $RepoRoot "src\Castr.Cli\bin\Release\net10.0\castr.exe"

if (-not (Test-Path $Castr)) {
    throw "castr.exe not found at $Castr - build the solution first: dotnet build -c Release (from the repo root)"
}

foreach ($dir in @("files", "receive1", "receive2", "receive3", "receive-gui", "state", "media\raw", "media\frames")) {
    New-Item -ItemType Directory -Force -Path (Join-Path $Root $dir) | Out-Null
}

# Sparse-allocated (instant, no real I/O) - the protocol doesn't care about content, only size/hashes,
# so these are as real a workout for chunking/hashing/encryption/transfer as genuine file content.
$files = @{
    "SuperRaid_ModPack_v3.zip"        = 104857600  # 100 MB - LAN party (desktop GUI)
    "fleet_config_bundle_v2.tar.gz"   = 62914560   #  60 MB - sysadmin fleet push (CLI)
    "lab_dataset_batch7.bin"          = 83886080   #  80 MB - test lab load (TUI fan-out)
}
foreach ($name in $files.Keys) {
    $path = Join-Path $Root "files\$name"
    if (-not (Test-Path $path)) {
        fsutil file createnew $path $files[$name] | Out-Null
    }
}

# One shared demo sender identity across all three scripts - simplest, and viewers don't see/care about
# the underlying fingerprint. Created by running `send` once, just long enough to print its own identity.
$identityKey = Join-Path $Root "state\sender-identity.key"
if (-not (Test-Path $identityKey)) {
    $p = Start-Process -FilePath $Castr -ArgumentList @(
        "send", (Join-Path $Root "files\fleet_config_bundle_v2.tar.gz"),
        "--identity", $identityKey, "--chunk-size", "60000"
    ) -PassThru -WindowStyle Hidden
    Start-Sleep -Seconds 2
    Stop-Process -Id $p.Id -Force -ErrorAction SilentlyContinue
}
$rawKey = [Convert]::ToBase64String([IO.File]::ReadAllBytes($identityKey))
# The identity file IS the raw Ed25519 private key bytes; castr's own fingerprint format needs the
# public key derived from it, which castr itself only ever prints on `send` startup - simplest robust
# way to get it back out is to run `send` again briefly and capture the printed "[send] identity ..." line.
$out = & $Castr send (Join-Path $Root "files\fleet_config_bundle_v2.tar.gz") --identity $identityKey --chunk-size 60000 2>&1 |
    Select-Object -First 1
Start-Sleep -Milliseconds 300
Get-Process -Name "castr" -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
if ($out -match '(ed25519:\S+)') {
    $fingerprint = $Matches[1]
    Write-Output "Demo sender fingerprint: $fingerprint"
    foreach ($store in @("trust-sysadmin.json", "trust-lab1.json", "trust-lab2.json", "trust-lab3.json")) {
        & $Castr trust add $fingerprint --name "Demo Sender" --trust-store (Join-Path $Root "state\$store") | Out-Null
    }
    Write-Output "Seeded trust stores for the CLI and TUI demos."
} else {
    Write-Warning "Could not parse the sender's identity from castr's startup output - trust stores not seeded. Re-run capture-cli.ps1/capture-tui.ps1's receivers with --on-unknown-sender Prompt and accept manually once, or fix this script."
}

Write-Output "Demo workspace ready at: $Root"
