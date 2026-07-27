<#
.SYNOPSIS
  M12a: measure real multi-receiver fan-out -- goodput and wire amplification against receiver count.

.DESCRIPTION
  Runs the shipped `castr` CLI: N real receiver processes, then one real sender, over real UDP multicast.
  Wall clock is sender launch to the last receiver's exit, the same definition M8/M9 used, so a row here is
  comparable with the single-receiver rows in docs/benchmarks/throughput-runs.md.

  Every arm asserts completion AND a byte-identical SHA-256 per receiver. A transfer that exits successfully
  without producing the file is the failure mode that once made a broken benchmark arm look like a 3,000,000
  MB/s result, so "the process exited 0" is never treated as the result on its own.

  Arms are interleaved (all receiver counts, then repeat) rather than blocked, because host state drifts
  during a run and a blocked schedule turns that drift into a fake trend.

  -Sniff adds a passive read-only sniffer to each arm to break the wire down by message type. Sniffer arms
  are for COMPOSITION ONLY: joining the group adds another kernel multicast copy per datagram, which on
  loopback is the dominant per-datagram cost. Never quote goodput from a sniffer arm.

.NOTES
  Always pass -Interface and record it. Two documented confounders have invalidated whole run sets here: the
  OS page cache (~1.94x) and group/interface ambiguity from leaked memberships (~1.8x). A leaked join always
  flatters the result.

  On a single host every receiver is another INLINE KERNEL COPY charged to the sender's sendto. That is not
  how a switch behaves, and it is measured directly by Measure-DatagramCeiling.ps1 -Receivers. Read a
  fan-out curve from this script as a same-host curve unless the receivers are on other machines.

.EXAMPLE
  ./Measure-FanOut.ps1 -Interface 'Loopback Pseudo-Interface 1' -Receivers 1,2,3,5,8 -Repeats 3
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$Interface,
    [string]$Group = '239.192.58.11',
    [int]$Port = 45059,
    [int[]]$Receivers = @(1, 2, 3, 5),
    [int]$Repeats = 3,
    [long]$SizeBytes = 104857600,
    [int]$ChunkSize = 262144,
    [switch]$Sniff,
    [int]$ArmTimeoutSeconds = 300,
    [string]$OutputCsv = "$PSScriptRoot\results\fanout.csv",
    [string]$Configuration = 'Release'
)

$ErrorActionPreference = 'Stop'
$repoRoot = Resolve-Path (Join-Path $PSScriptRoot '..\..')
$castr = Join-Path $repoRoot "src\Castr.Cli\bin\$Configuration\net10.0\castr.exe"
$sniffer = Join-Path $PSScriptRoot "Castr.Bench.Sniffer\bin\$Configuration\net10.0\castr-sniff.exe"

if (-not (Test-Path $castr)) { throw "castr.exe not built at $castr. Run: dotnet build -c $Configuration" }
if ($Sniff -and -not (Test-Path $sniffer)) { throw "castr-sniff.exe not built at $sniffer" }

$workspace = Join-Path $PSScriptRoot 'workspace\fanout'
$logs = Join-Path $workspace 'logs'
New-Item -ItemType Directory -Force -Path $workspace, $logs | Out-Null
New-Item -ItemType Directory -Force -Path (Split-Path -Parent $OutputCsv) | Out-Null

# ---- source file -------------------------------------------------------------------------------------
$source = Join-Path $workspace "payload-$SizeBytes.bin"
if (-not (Test-Path $source) -or (Get-Item $source).Length -ne $SizeBytes) {
    fsutil file createnew $source $SizeBytes | Out-Null
}
$sourceHash = (Get-FileHash -Algorithm SHA256 -Path $source).Hash
Write-Host "source $source ($SizeBytes bytes) sha256=$sourceHash"

# ---- sender identity, and the fingerprint every receiver must trust ----------------------------------
$identity = Join-Path $workspace 'sender.key'
$fingerprintFile = Join-Path $workspace 'sender.fingerprint'
if (-not (Test-Path $fingerprintFile)) {
    # castr prints its own identity on send startup and has no natural exit, so start it, read the line,
    # and stop it. Explicit --interface so the membership this leaves behind is irrelevant either way.
    $idOut = Join-Path $workspace 'identity.txt'
    $p = Start-Process -FilePath $castr -PassThru -NoNewWindow -RedirectStandardOutput $idOut -ArgumentList (
        "send `"$source`" --identity `"$identity`" --group $Group --port $Port --interface `"$Interface`"")
    Start-Sleep -Seconds 6
    Stop-Process -Id $p.Id -Force -ErrorAction SilentlyContinue
    $line = (Get-Content $idOut -Raw) -split "`n" | Where-Object { $_ -match '(ed25519:\S+)' } | Select-Object -First 1
    if (-not ($line -match '(ed25519:\S+)')) { throw "Could not read the sender fingerprint from $idOut" }
    Set-Content -Path $fingerprintFile -Value $Matches[1] -Encoding utf8
}
$fingerprint = (Get-Content $fingerprintFile -Raw).Trim()
Write-Host "sender $fingerprint"

$maxReceivers = ($Receivers | Measure-Object -Maximum).Maximum
for ($i = 0; $i -lt $maxReceivers; $i++) {
    # One state DIRECTORY per receiver, not just one trust-store file: castr derives the session-registry
    # path from the trust store's directory, so receivers sharing a directory share that file.
    $store = Join-Path $workspace "state\receiver-$i\trust.json"
    New-Item -ItemType Directory -Force -Path (Split-Path -Parent $store) | Out-Null
    if (-not (Test-Path $store)) {
        & $castr trust add $fingerprint --name 'Bench Sender' --trust-store $store | Out-Null
    }
}

# ---- one arm -----------------------------------------------------------------------------------------
function Invoke-Arm {
    param([int]$Count, [int]$Repeat)

    $tag = "n$Count-r$Repeat"
    $destDirs = @()
    for ($i = 0; $i -lt $Count; $i++) {
        $dest = Join-Path $workspace "dest\$tag-$i"
        if (Test-Path $dest) { Remove-Item $dest -Recurse -Force }
        New-Item -ItemType Directory -Force -Path $dest | Out-Null
        $destDirs += $dest
    }

    $snifferProc = $null
    $snifferJson = Join-Path $workspace "sniff-$tag.json"
    if ($Sniff) {
        Remove-Item $snifferJson -ErrorAction SilentlyContinue
        $snifferProc = Start-Process -FilePath $sniffer -PassThru -NoNewWindow `
            -RedirectStandardError (Join-Path $logs "sniff-$tag.err") -ArgumentList (
            "--group $Group --port $Port --interface `"$Interface`" --idle-ms 6000 " +
            "--max-seconds $ArmTimeoutSeconds --format-version 2 --out `"$snifferJson`"")
        Start-Sleep -Milliseconds 800
    }

    $receiverProcs = @()
    for ($i = 0; $i -lt $Count; $i++) {
        $store = Join-Path $workspace "state\receiver-$i\trust.json"
        $receiverProcs += Start-Process -FilePath $castr -PassThru -NoNewWindow `
            -RedirectStandardOutput (Join-Path $logs "recv-$tag-$i.out") `
            -RedirectStandardError (Join-Path $logs "recv-$tag-$i.err") -ArgumentList (
            "receive --dest-dir `"$($destDirs[$i])`" --trust-store `"$store`" --group $Group " +
            "--port $Port --interface `"$Interface`" --on-unknown-sender Deny")
    }
    # Receivers must be joined before the first datagram: the carousel is single-pass and there is no
    # manifest re-request path, so a receiver that starts late misses the manifest outright and then sits
    # at "0/0 chunks" until the arm times out. A fixed sleep is not enough -- process start plus JIT is
    # variable and the failure is silent -- so wait for each receiver to print its own listening line.
    foreach ($i in 0..($Count - 1)) {
        $log = Join-Path $logs "recv-$tag-$i.out"
        $ready = $false
        foreach ($attempt in 1..300) {
            if ((Test-Path $log) -and ((Get-Content $log -Raw -ErrorAction SilentlyContinue) -match 'listening on')) {
                $ready = $true
                break
            }
            Start-Sleep -Milliseconds 100
        }
        if (-not $ready) { throw "receiver $i never reported listening for arm $tag" }
    }
    # Small extra settle: the line is printed after the socket joins, but the sender's first datagram is the
    # manifest and there is no second chance at it.
    Start-Sleep -Milliseconds 750

    $clock = [System.Diagnostics.Stopwatch]::StartNew()
    $senderProc = Start-Process -FilePath $castr -PassThru -NoNewWindow `
        -RedirectStandardOutput (Join-Path $logs "send-$tag.out") `
        -RedirectStandardError (Join-Path $logs "send-$tag.err") -ArgumentList (
        "send `"$source`" --identity `"$identity`" --chunk-size $ChunkSize --group $Group " +
        "--port $Port --interface `"$Interface`"")

    $timedOut = $false
    foreach ($proc in $receiverProcs) {
        if (-not $proc.WaitForExit($ArmTimeoutSeconds * 1000)) {
            $timedOut = $true
            $proc.Kill()
        }
        # The parameterless overload after the timed one: the timed overload can return before the process
        # object has cached ExitCode, and an uncached ExitCode reads as $null, which then compares -ne 0 and
        # turns a perfectly good arm into a reported failure.
        $proc.WaitForExit()
    }
    $clock.Stop()

    # castr send stays up serving repairs by design and has no natural exit. Force-stopping it skips
    # DisposeAsync and leaks the multicast membership -- harmless here only because every process in this
    # harness passes --interface explicitly. See docs/benchmarks/throughput-runs.md.
    Stop-Process -Id $senderProc.Id -Force -ErrorAction SilentlyContinue

    # Completion is read from each receiver's own "transfer complete." line, not from its exit code:
    # Start-Process -PassThru combined with output redirection returns a Process whose ExitCode is
    # intermittently unreadable ($null), and $null -ne 0, so an exit-code test reports perfectly good arms
    # as failures. The log line is also the more direct assertion -- it is the receiver stating it finished.
    $exitCodes = @($receiverProcs | ForEach-Object { $_.ExitCode })
    $completedLogs = 0
    for ($i = 0; $i -lt $Count; $i++) {
        $log = Get-Content (Join-Path $logs "recv-$tag-$i.out") -Raw -ErrorAction SilentlyContinue
        if ($log -match 'transfer complete\.') { $completedLogs++ }
    }
    $allComplete = (-not $timedOut) -and ($completedLogs -eq $Count)

    $matched = 0
    foreach ($dest in $destDirs) {
        $file = Get-ChildItem -Path $dest -File -Recurse | Where-Object { $_.Name -eq (Split-Path -Leaf $source) }
        if ($file -and (Get-FileHash -Algorithm SHA256 -Path $file.FullName).Hash -eq $sourceHash) { $matched++ }
    }

    $snifferReport = $null
    if ($snifferProc) {
        if (-not $snifferProc.WaitForExit(30000)) { $snifferProc.Kill() }
        if (Test-Path $snifferJson) { $snifferReport = Get-Content $snifferJson -Raw | ConvertFrom-Json }
    }

    $seconds = [math]::Round($clock.Elapsed.TotalSeconds, 3)
    $row = [pscustomobject]@{
        Repeat           = $Repeat
        ReceiverCount    = $Count
        WallSeconds      = $seconds
        GoodputMBps      = if ($seconds -gt 0) { [math]::Round($SizeBytes / $seconds / 1MB, 3) } else { 0 }
        AggregateMBps    = if ($seconds -gt 0) { [math]::Round($SizeBytes * $Count / $seconds / 1MB, 3) } else { 0 }
        Complete         = $allComplete
        ExitCodes        = ($exitCodes -join '|')
        HashesMatched    = $matched
        Interface        = $Interface
        Group            = $Group
        ChunkSize        = $ChunkSize
        SizeBytes        = $SizeBytes
        SniffDatagrams   = if ($snifferReport) { $snifferReport.TotalDatagrams } else { $null }
        SniffPayloadB    = if ($snifferReport) { $snifferReport.TotalPayloadBytes } else { $null }
        SniffEthernetB   = if ($snifferReport) { $snifferReport.EthernetWireBytes } else { $null }
        WireAmplification = if ($snifferReport) { [math]::Round($snifferReport.TotalPayloadBytes / $SizeBytes, 4) } else { $null }
        SniffChunkPacket = if ($snifferReport) { ($snifferReport.ByType | Where-Object Name -eq 'CHUNK_PACKET').Datagrams } else { $null }
        SniffPeerHave    = if ($snifferReport) { ($snifferReport.ByType | Where-Object Name -eq 'PEER_HAVE').Datagrams } else { $null }
        SniffChunkReq    = if ($snifferReport) { ($snifferReport.ByType | Where-Object Name -eq 'CHUNK_REQUEST').Datagrams } else { $null }
        SniffChunkResp   = if ($snifferReport) { ($snifferReport.ByType | Where-Object Name -eq 'CHUNK_RESPONSE').Datagrams } else { $null }
        SniffFragment    = if ($snifferReport) { ($snifferReport.ByType | Where-Object Name -eq 'PACKET_FRAGMENT').Datagrams } else { $null }
    }

    Write-Host ("rep {0} n={1,2} wall={2,7:F2}s goodput={3,6:F2} MB/s aggregate={4,7:F2} MB/s complete={5} hashes={6}/{1}{7}" -f `
        $Repeat, $Count, $row.WallSeconds, $row.GoodputMBps, $row.AggregateMBps, $row.Complete, $matched,
        $(if ($snifferReport) { " datagrams=$($snifferReport.TotalDatagrams) amp=$($row.WireAmplification)x" } else { '' }))

    return $row
}

# ---- interleaved schedule ----------------------------------------------------------------------------
$rows = @()
for ($repeat = 1; $repeat -le $Repeats; $repeat++) {
    foreach ($count in $Receivers) {
        $rows += Invoke-Arm -Count $count -Repeat $repeat
        # Let the force-killed sender's socket and the OS multicast state settle before the next arm binds
        # the same port. Without this the first receiver of the next arm can start against a half-torn-down
        # group and silently miss the manifest.
        Start-Sleep -Seconds 3
    }
}

$rows | Export-Csv -Path $OutputCsv -NoTypeInformation -Encoding utf8
Write-Host "wrote $OutputCsv"

Write-Host "`nsummary (mean of complete, hash-verified arms only):"
$rows | Where-Object { $_.Complete -and $_.HashesMatched -eq $_.ReceiverCount } |
    Group-Object ReceiverCount | ForEach-Object {
        [pscustomobject]@{
            Receivers     = [int]$_.Name
            Arms          = $_.Count
            MeanWall      = [math]::Round(($_.Group | Measure-Object WallSeconds -Average).Average, 3)
            MinWall       = ($_.Group | Measure-Object WallSeconds -Minimum).Minimum
            MaxWall       = ($_.Group | Measure-Object WallSeconds -Maximum).Maximum
            MeanGoodput   = [math]::Round(($_.Group | Measure-Object GoodputMBps -Average).Average, 3)
            MeanAggregate = [math]::Round(($_.Group | Measure-Object AggregateMBps -Average).Average, 3)
        }
    } | Sort-Object Receivers | Format-Table -AutoSize

$failed = $rows | Where-Object { -not $_.Complete -or $_.HashesMatched -ne $_.ReceiverCount }
if ($failed) {
    Write-Warning "$($failed.Count) arm(s) did not complete or did not verify -- excluded from the summary:"
    $failed | Format-Table Repeat, ReceiverCount, WallSeconds, Complete, HashesMatched -AutoSize
}
