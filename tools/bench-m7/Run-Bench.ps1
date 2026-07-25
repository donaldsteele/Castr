# BENCH (temporary M7 measurement harness) — NOT shipped tooling.
# Runs one real two-process (or 1:N) castr transfer over loopback multicast and collects the
# BenchMetrics JSON reports plus OS-level UDP counter deltas.
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$Tag,
    [int]$ChunkSize = 8192,
    [int]$SendWindow = 1,
    [int]$PeerHaveEvery = 1,
    [int]$MaxDatagram = 0,          # 0 => leave WirePacketizer.DefaultMaxDatagramPayload alone
    [int]$Receivers = 1,
    [int]$Port = 0,                 # 0 => derived from a per-run counter
    [string]$Interface = '',        # '' => let castr auto-select (what a user gets by default)
    [int]$SocketBuf = 0,            # 0 => leave UdpMulticastTransport.SocketBufferBytes (4 MB) alone
    [int]$RepairStartMs = 0,        # 0 => shipped behavior (repair loop starts immediately)
    [int]$RepairPeriodMs = 250,     # 250 => shipped behavior
    [int]$TimeoutSec = 240,
    [string]$Root = 'C:\Users\Don\AppData\Local\Temp\claude\c--code-Castr\83aa7531-770b-4c21-864f-5d07fdd1a959\scratchpad\bench'
)

$ErrorActionPreference = 'Stop'
$exe = 'C:\code\Castr\.claude\worktrees\agent-aad9c2ea41236900d\src\Castr.Cli\bin\Release\net10.0\castr.exe'
$payload = Join-Path $Root 'payload.bin'
$identity = Join-Path $Root 'identity.key'
$trustStore = Join-Path $Root 'trust.json'

if (-not (Test-Path $payload)) { throw "payload missing: $payload (run Init-Bench.ps1 first)" }
if (-not (Test-Path $trustStore)) { throw "trust store missing: $trustStore (run Init-Bench.ps1 first)" }

if ($Port -eq 0) {
    $counterFile = Join-Path $Root 'portcounter.txt'
    $n = 0
    if (Test-Path $counterFile) { $n = [int](Get-Content $counterFile -Raw).Trim() }
    $n = $n + 1
    Set-Content -Path $counterFile -Value $n -Encoding ascii
    # Deliberately clear of 46101/46102, which UdpMulticastTransportTests binds with a fixed port — a benchmark
    # run holding those made that test fail with SocketException on Bind.
    $Port = 47000 + ($n % 900)
}

$runDir = Join-Path $Root "runs\$Tag"
if (Test-Path $runDir) { Remove-Item -Recurse -Force $runDir }
New-Item -ItemType Directory -Force -Path $runDir | Out-Null
$stopFile = Join-Path $runDir 'STOP'

function Get-UdpStats {
    $raw = netstat -s -p udp
    $o = @{}
    foreach ($line in $raw) {
        if ($line -match '^\s*(Datagrams Received|No Ports|Receive Errors|Datagrams Sent)\s*=\s*(\d+)') {
            $o[$matches[1]] = [int64]$matches[2]
        }
    }
    return $o
}

# Start-Process joins -ArgumentList with plain spaces and does NOT quote, so quote every argument here
# (the interface name contains spaces).
function Q([string]$s) { '"' + $s + '"' }
$ifaceArgs = @()
if ($Interface -ne '') { $ifaceArgs = @('--interface', (Q $Interface)) }

$before = Get-UdpStats
$env:CASTR_BENCH = $runDir
$env:CASTR_BENCH_PEERHAVE_EVERY = "$PeerHaveEvery"
$env:CASTR_BENCH_STOP_FILE = $stopFile
$env:CASTR_BENCH_REPAIR_START_MS = "$RepairStartMs"
$env:CASTR_BENCH_REPAIR_PERIOD_MS = "$RepairPeriodMs"
if ($MaxDatagram -gt 0) { $env:CASTR_BENCH_MAXDGRAM = "$MaxDatagram" } else { Remove-Item Env:\CASTR_BENCH_MAXDGRAM -ErrorAction SilentlyContinue }
if ($SocketBuf -gt 0) { $env:CASTR_BENCH_SOCKBUF = "$SocketBuf" } else { Remove-Item Env:\CASTR_BENCH_SOCKBUF -ErrorAction SilentlyContinue }

# ---- receivers ----
$recvProcs = @()
for ($i = 0; $i -lt $Receivers; $i++) {
    $dest = Join-Path $runDir "recv$i"
    New-Item -ItemType Directory -Force -Path $dest | Out-Null
    $env:CASTR_BENCH_TAG = "recv$i"
    $recvProcs += Start-Process -FilePath $exe -PassThru -WindowStyle Hidden `
        -RedirectStandardOutput (Join-Path $runDir "recv$i.log") `
        -RedirectStandardError  (Join-Path $runDir "recv$i.err") `
        -ArgumentList (@('receive', '--dest-dir', (Q $dest), '--trust-store', (Q $trustStore),
            '--group', '239.192.55.55', '--port', "$Port") + $ifaceArgs)
}
Start-Sleep -Milliseconds 1500

# ---- sender ----
$env:CASTR_BENCH_TAG = 'send'
$sw = [System.Diagnostics.Stopwatch]::StartNew()
$sendProc = Start-Process -FilePath $exe -PassThru -WindowStyle Hidden `
    -RedirectStandardOutput (Join-Path $runDir 'send.log') `
    -RedirectStandardError  (Join-Path $runDir 'send.err') `
    -ArgumentList (@('send', (Q $payload), '--identity', (Q $identity), '--chunk-size', "$ChunkSize",
        '--send-window-size', "$SendWindow", '--group', '239.192.55.55', '--port', "$Port") + $ifaceArgs)

# ---- wait for all receivers ----
$deadline = (Get-Date).AddSeconds($TimeoutSec)
$timedOut = $false
foreach ($p in $recvProcs) {
    $remaining = [int]([Math]::Max(1, ($deadline - (Get-Date)).TotalMilliseconds))
    if (-not $p.WaitForExit($remaining)) { $timedOut = $true }
}
$sw.Stop()

# ---- stop the sender cleanly so its metrics flush ----
Set-Content -Path $stopFile -Value 'stop' -Encoding ascii
if (-not $sendProc.WaitForExit(20000)) { $sendProc.Kill(); Start-Sleep -Milliseconds 300 }
foreach ($p in $recvProcs) { if (-not $p.HasExited) { $p.Kill() } }
Start-Sleep -Milliseconds 400

$after = Get-UdpStats
$delta = @{}
foreach ($k in $before.Keys) { $delta[$k] = $after[$k] - $before[$k] }

# ---- verify every receiver got byte-identical output ----
$srcHash = (Get-FileHash $payload -Algorithm SHA256).Hash
$verify = @()
for ($i = 0; $i -lt $Receivers; $i++) {
    $f = Join-Path (Join-Path $runDir "recv$i") 'payload.bin'
    if (Test-Path $f) {
        $h = (Get-FileHash $f -Algorithm SHA256).Hash
        $verify += [pscustomobject]@{ recv = $i; ok = ($h -eq $srcHash); bytes = (Get-Item $f).Length }
    } else {
        $verify += [pscustomobject]@{ recv = $i; ok = $false; bytes = 0 }
    }
}

$summary = [ordered]@{
    tag = $Tag; chunkSize = $ChunkSize; sendWindow = $SendWindow; peerHaveEvery = $PeerHaveEvery
    maxDatagram = $MaxDatagram; receivers = $Receivers; port = $Port; interface = $Interface
    repairStartMs = $RepairStartMs; repairPeriodMs = $RepairPeriodMs; socketBuf = $SocketBuf
    harnessWallMs = [int]$sw.Elapsed.TotalMilliseconds; timedOut = $timedOut
    udpDelta = $delta; verify = $verify
}
$summary | ConvertTo-Json -Depth 5 | Set-Content -Path (Join-Path $runDir 'harness.json') -Encoding utf8

$okCount = @($verify | Where-Object { $_.ok }).Count
Write-Output "== $Tag : wall=$([int]$sw.Elapsed.TotalMilliseconds)ms timedOut=$timedOut verified=$okCount/$Receivers"
Write-Output ("   udp delta: recv={0} sent={1} recvErrors={2} noPorts={3}" -f $delta['Datagrams Received'], $delta['Datagrams Sent'], $delta['Receive Errors'], $delta['No Ports'])
