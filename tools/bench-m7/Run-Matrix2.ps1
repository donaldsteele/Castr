# BENCH (temporary M7 measurement harness) — second matrix: socket-buffer sweep (to test whether the
# burst/stall period is set by SO_SNDBUF), loopback-interface variants, and reproducibility repeats.
[CmdletBinding()]
param([string[]]$Only = @())
$ErrorActionPreference = 'Stop'
$run = Join-Path $PSScriptRoot 'Run-Bench.ps1'
$LB = 'Loopback Pseudo-Interface 1'

$matrix = @(
    # --- socket buffer sweep: if the ~600 ms sawtooth is an SO_SNDBUF fill/drain cycle, the period scales ---
    @{ Tag = 'SB-256k';    Args = @{ SocketBuf =   262144 } }
    @{ Tag = 'SB-1m';      Args = @{ SocketBuf =  1048576 } }
    @{ Tag = 'SB-4m';      Args = @{ SocketBuf =  4194304 } }   # = shipped default, explicit
    @{ Tag = 'SB-16m';     Args = @{ SocketBuf = 16777216 } }
    @{ Tag = 'SB-64m';     Args = @{ SocketBuf = 67108864 } }
    # --- loopback-interface variants (removes the physical-NIC egress path from the picture) ---
    @{ Tag = 'T-LB-base';       Args = @{ Interface = $LB } }
    @{ Tag = 'U-LB-noRepair';   Args = @{ Interface = $LB; RepairStartMs = 999999 } }
    @{ Tag = 'V-LB-noPH';       Args = @{ Interface = $LB; PeerHaveEvery = 0 } }
    @{ Tag = 'W-LB-noRep-noPH'; Args = @{ Interface = $LB; RepairStartMs = 999999; PeerHaveEvery = 0 } }
    @{ Tag = 'X-LB-combo';      Args = @{ Interface = $LB; ChunkSize = 262144; MaxDatagram = 60000 } }
    @{ Tag = 'Y-LB-3recv';      Args = @{ Interface = $LB; Receivers = 3 } }
    # --- isolate IP fragmentation from syscall count: 1472 never fragments on a 1500-MTU path ---
    @{ Tag = 'AD-chunk256k-dg1472'; Args = @{ ChunkSize = 262144; MaxDatagram = 1472 } }
    @{ Tag = 'AE-chunk8k-dg1472';   Args = @{ ChunkSize = 8192;   MaxDatagram = 1472 } }
    # --- reproducibility + the practical best-case combos ---
    @{ Tag = 'Z-combo-rep';    Args = @{ ChunkSize = 262144; MaxDatagram = 60000 } }
    @{ Tag = 'AA-combo-3recv'; Args = @{ ChunkSize = 262144; MaxDatagram = 60000; Receivers = 3 } }
    @{ Tag = 'AB-chunk60k-noRepair'; Args = @{ ChunkSize = 60000; RepairStartMs = 999999 } }
    @{ Tag = 'AC-noRepair-rep';      Args = @{ RepairStartMs = 999999 } }
)

foreach ($m in $matrix) {
    if ($Only.Count -gt 0 -and $Only -notcontains $m.Tag) { continue }
    $splat = $m.Args.Clone()
    $splat['Tag'] = $m.Tag
    try { & $run @splat }
    catch { Write-Output "== $($m.Tag) : FAILED $($_.Exception.Message)" }
    Start-Sleep -Seconds 2
}
