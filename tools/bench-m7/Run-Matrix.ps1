# BENCH (temporary M7 measurement harness) — runs the whole A/B matrix back to back.
[CmdletBinding()]
param([string[]]$Only = @())
$ErrorActionPreference = 'Stop'
$run = Join-Path $PSScriptRoot 'Run-Bench.ps1'

$matrix = @(
    @{ Tag = 'A1-baseline';        Args = @{} }
    @{ Tag = 'A2-baseline-rep';    Args = @{} }                                            # repeat, run-to-run noise
    @{ Tag = 'B-window2';          Args = @{ SendWindow = 2 } }
    @{ Tag = 'C-window4';          Args = @{ SendWindow = 4 } }
    @{ Tag = 'D-chunk60k';         Args = @{ ChunkSize = 60000 } }
    @{ Tag = 'E-chunk256k';        Args = @{ ChunkSize = 262144 } }
    @{ Tag = 'F-dgram1400';        Args = @{ MaxDatagram = 1400 } }
    @{ Tag = 'G-dgram8000';        Args = @{ MaxDatagram = 8000 } }
    @{ Tag = 'H-dgram60000';       Args = @{ MaxDatagram = 60000 } }
    @{ Tag = 'I-noPeerHave';       Args = @{ PeerHaveEvery = 0 } }
    @{ Tag = 'J-peerHave64';       Args = @{ PeerHaveEvery = 64 } }
    @{ Tag = 'K-noRepair';         Args = @{ RepairStartMs = 999999 } }
    @{ Tag = 'L-noRepair-noPH';    Args = @{ RepairStartMs = 999999; PeerHaveEvery = 0 } }
    @{ Tag = 'M-3recv';            Args = @{ Receivers = 3 } }
    @{ Tag = 'N-3recv-noPH';       Args = @{ Receivers = 3; PeerHaveEvery = 0 } }
    @{ Tag = 'O-loopbackIface';    Args = @{ Interface = 'Loopback Pseudo-Interface 1' } }
    @{ Tag = 'P-combo';            Args = @{ ChunkSize = 262144; MaxDatagram = 60000; RepairStartMs = 999999 } }
    @{ Tag = 'Q-combo-repair';     Args = @{ ChunkSize = 262144; MaxDatagram = 60000 } }
    @{ Tag = 'R-dgram60k-noRepair';Args = @{ MaxDatagram = 60000; RepairStartMs = 999999 } }
    @{ Tag = 'S-combo-w2';         Args = @{ ChunkSize = 262144; MaxDatagram = 60000; SendWindow = 2 } }
)

foreach ($m in $matrix) {
    if ($Only.Count -gt 0 -and $Only -notcontains $m.Tag) { continue }
    $splat = $m.Args.Clone()
    $splat['Tag'] = $m.Tag
    try { & $run @splat }
    catch { Write-Output "== $($m.Tag) : FAILED $($_.Exception.Message)" }
    Start-Sleep -Seconds 2
}
