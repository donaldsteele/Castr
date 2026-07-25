# BENCH (temporary M7 measurement harness) — creates the 80 MB payload, mints a sender identity,
# and writes a trust store that trusts it. Run once before Run-Bench.ps1.
[CmdletBinding()]
param(
    [int]$SizeMB = 80,
    [string]$Root = 'C:\Users\Don\AppData\Local\Temp\claude\c--code-Castr\83aa7531-770b-4c21-864f-5d07fdd1a959\scratchpad\bench'
)
$ErrorActionPreference = 'Stop'
$exe = 'C:\code\Castr\.claude\worktrees\agent-aad9c2ea41236900d\src\Castr.Cli\bin\Release\net10.0\castr.exe'
New-Item -ItemType Directory -Force -Path $Root | Out-Null

$payload = Join-Path $Root 'payload.bin'
if (-not (Test-Path $payload) -or (Get-Item $payload).Length -ne ($SizeMB * 1MB)) {
    Write-Output "generating $SizeMB MB payload..."
    $rng = [System.Security.Cryptography.RandomNumberGenerator]::Create()
    $fs = [System.IO.File]::Create($payload)
    try {
        $buf = New-Object byte[] (1MB)
        for ($i = 0; $i -lt $SizeMB; $i++) { $rng.GetBytes($buf); $fs.Write($buf, 0, $buf.Length) }
    } finally { $fs.Dispose(); $rng.Dispose() }
}
Write-Output "payload: $payload ($((Get-Item $payload).Length) bytes)"

# Mint the identity by running a send against a tiny file with the stop file already in place.
$identity = Join-Path $Root 'identity.key'
$tiny = Join-Path $Root 'tiny.bin'
Set-Content -Path $tiny -Value 'x' -Encoding ascii -NoNewline
$stop = Join-Path $Root 'STOP-init'
Set-Content -Path $stop -Value 'stop' -Encoding ascii
$env:CASTR_BENCH_STOP_FILE = $stop
Remove-Item Env:\CASTR_BENCH -ErrorAction SilentlyContinue
$out = Join-Path $Root 'identity.log'
$p = Start-Process -FilePath $exe -PassThru -WindowStyle Hidden -RedirectStandardOutput $out `
    -ArgumentList @('send', $tiny, '--identity', $identity, '--port', '46999')
$p.WaitForExit(30000) | Out-Null
if (-not $p.HasExited) { $p.Kill() }

$line = (Get-Content $out) | Where-Object { $_ -match 'identity\s+(ed25519:\S+)' } | Select-Object -First 1
if (-not $line) { throw "could not read identity from $out" }
$null = $line -match 'identity\s+(ed25519:\S+)'
$id = $matches[1]
Write-Output "identity: $id"

$trustStore = Join-Path $Root 'trust.json'
Remove-Item $trustStore -ErrorAction SilentlyContinue
& $exe trust add $id --name bench --trust-store $trustStore | Out-Null
& $exe trust list --trust-store $trustStore
Write-Output "trust store: $trustStore"
