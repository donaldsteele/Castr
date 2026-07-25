# BENCH (temporary M7 measurement harness) — third matrix: the controlled A/B, run warm (payload already in
# the OS page cache) with repeats, after the interface variable was shown to be a regime artifact.
[CmdletBinding()]
param([int]$Reps = 3)
$ErrorActionPreference = 'Stop'
$run = Join-Path $PSScriptRoot 'Run-Bench.ps1'

$cells = @(
    @{ N = 'base';        Args = @{} }
    @{ N = 'noPH';        Args = @{ PeerHaveEvery = 0 } }
    @{ N = 'PH64';        Args = @{ PeerHaveEvery = 64 } }
    @{ N = 'noRep';       Args = @{ RepairStartMs = 999999 } }
    @{ N = 'noRep-noPH';  Args = @{ RepairStartMs = 999999; PeerHaveEvery = 0 } }
    @{ N = 'rep2s';       Args = @{ RepairPeriodMs = 2000 } }
    @{ N = 'w2';          Args = @{ SendWindow = 2 } }
    @{ N = 'w4';          Args = @{ SendWindow = 4 } }
    @{ N = 'dg8000';      Args = @{ MaxDatagram = 8000 } }
    @{ N = 'chunk256k';   Args = @{ ChunkSize = 262144 } }
    @{ N = 'combo';       Args = @{ ChunkSize = 262144; MaxDatagram = 60000 } }
    @{ N = 'combo-noRep'; Args = @{ ChunkSize = 262144; MaxDatagram = 60000; RepairStartMs = 999999 } }
    @{ N = '3recv';       Args = @{ Receivers = 3 } }
    @{ N = '3recv-noPH';  Args = @{ Receivers = 3; PeerHaveEvery = 0 } }
    @{ N = '3recv-combo'; Args = @{ Receivers = 3; ChunkSize = 262144; MaxDatagram = 60000 } }
)

# Warm the page cache so every cell starts from the same state.
Get-FileHash 'C:\Users\Don\AppData\Local\Temp\claude\c--code-Castr\83aa7531-770b-4c21-864f-5d07fdd1a959\scratchpad\bench\payload.bin' -Algorithm MD5 | Out-Null

for ($r = 1; $r -le $Reps; $r++) {
    foreach ($c in $cells) {
        $splat = $c.Args.Clone()
        $splat['Tag'] = "M3-$($c.N)-r$r"
        try { & $run @splat } catch { Write-Output "== M3-$($c.N)-r$r : FAILED $($_.Exception.Message)" }
        Start-Sleep -Milliseconds 1200
    }
}
