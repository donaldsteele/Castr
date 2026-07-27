<#
.SYNOPSIS
  M12a: measure the sustained datagram rate a Castr receiver's transport can drain without loss.

.DESCRIPTION
  Runs one `castr-dgram drain` process (a real UdpMulticastTransport, consumer doing nothing) and sweeps
  `castr-dgram blast` against it, one arm per requested offered rate. Emits one CSV row per arm.

  The result is an UPPER BOUND on any real receiver: a ReceiverSession adds decode, Merkle verification,
  AEAD open, a disk write and an outbound broadcast on top of the drain measured here.

  Always pass -Interface, and record which one: leaked memberships make a default group's interface
  ambiguous, and a leaked join always flatters the result. See docs/benchmarks/throughput-runs.md.

.EXAMPLE
  ./Measure-DatagramCeiling.ps1 -Interface 'Loopback Pseudo-Interface 1' -Threads 1,2,4 -Seconds 10
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$Interface,
    [string]$Group = '239.192.58.10',
    [int]$Port = 45058,
    [int]$Size = 1472,
    [int]$Seconds = 10,
    [int[]]$Threads = @(1, 2, 4),
    # 0 = unpaced (offer as fast as the sender can). Any other value is a TOTAL datagrams/second target.
    [int[]]$Rates = @(0),
    # Number of concurrent drain processes. >1 makes this a fan-out component measurement: every extra
    # receiver on the host costs the kernel another inline multicast copy per datagram.
    [int]$Receivers = 1,
    [string]$OutputCsv = "$PSScriptRoot\results\datagram-ceiling.csv",
    [string]$Configuration = 'Release'
)

$ErrorActionPreference = 'Stop'
$exe = Join-Path $PSScriptRoot "Castr.Bench.DatagramCeiling\bin\$Configuration\net10.0\castr-dgram.exe"
if (-not (Test-Path $exe)) {
    throw "castr-dgram not built at $exe. Run: dotnet build tools/bench/Castr.Bench.DatagramCeiling -c $Configuration"
}

$workspace = Join-Path $PSScriptRoot 'workspace'
New-Item -ItemType Directory -Force -Path $workspace | Out-Null
New-Item -ItemType Directory -Force -Path (Split-Path -Parent $OutputCsv) | Out-Null

$rows = @()
foreach ($rate in $Rates) {
    foreach ($threadCount in $Threads) {
        $blastJson = Join-Path $workspace "blast-t$threadCount-r$rate.json"
        Remove-Item $blastJson -ErrorAction SilentlyContinue

        $drains = @()
        $drainJsons = @()
        for ($r = 0; $r -lt $Receivers; $r++) {
            $drainJson = Join-Path $workspace "drain-n$Receivers-$r-t$threadCount-r$rate.json"
            $drainErr = Join-Path $workspace "drain-n$Receivers-$r-t$threadCount-r$rate.err"
            Remove-Item $drainJson -ErrorAction SilentlyContinue
            $drainJsons += $drainJson

            # Start-Process does NOT quote array elements containing spaces; an interface name like
            # 'Loopback Pseudo-Interface 1' silently splits into three arguments and the process exits on a
            # parse error while the run still looks successful. Build one pre-quoted argument string instead.
            $drainArgs = "drain --group $Group --port $Port --interface `"$Interface`" --idle-ms 2500 " +
                         "--max-seconds $($Seconds + 45) --out `"$drainJson`""
            $drains += Start-Process -FilePath $exe -ArgumentList $drainArgs -PassThru -NoNewWindow -RedirectStandardError $drainErr
        }
        Start-Sleep -Seconds 2

        & $exe blast --group $Group --port $Port --interface $Interface --size $Size --rate $rate `
            --seconds $Seconds --threads $threadCount --out $blastJson | Out-Null

        foreach ($drain in $drains) {
            if (-not $drain.WaitForExit(60000)) { $drain.Kill(); throw "drain did not exit for threads=$threadCount rate=$rate" }
        }

        $ds = $drainJsons | ForEach-Object { Get-Content $_ -Raw | ConvertFrom-Json }
        $b = Get-Content $blastJson -Raw | ConvertFrom-Json
        # Report the WORST receiver, not the mean: a fan-out claim is only as good as its slowest member, and
        # averaging hides the one receiver that fell off.
        $worst = $ds | Sort-Object LossPercent -Descending | Select-Object -First 1
        $rows += [pscustomobject]@{
            Interface           = $Interface
            Group               = $Group
            DatagramBytes       = $Size
            Receivers           = $Receivers
            Threads             = $threadCount
            TargetRate          = $rate
            OfferedPerSecond    = $b.OfferedDatagramsPerSecond
            OfferedMBps         = $b.OfferedMegabytesPerSecond
            WorstDrainedPerSec  = $worst.ReceivedDatagramsPerSecond
            WorstDrainedMBps    = $worst.ReceivedMegabytesPerSecond
            WorstLossPercent    = $worst.LossPercent
            MeanLossPercent     = [math]::Round(($ds | Measure-Object LossPercent -Average).Average, 3)
            TotalLostDatagrams  = ($ds | Measure-Object LostDatagrams -Sum).Sum
            SpanSeconds         = $worst.SpanSeconds
        }
        Write-Host ("recv={0,2} threads={1,2} rate={2,-8} offered={3,10:N0}/s worst-drained={4,10:N0}/s ({5,6:F1} MB/s) worst-loss={6:F3}% mean-loss={7:F3}%" -f `
            $Receivers, $threadCount, $rate, $b.OfferedDatagramsPerSecond, $worst.ReceivedDatagramsPerSecond, `
            $worst.ReceivedMegabytesPerSecond, $worst.LossPercent, ($ds | Measure-Object LossPercent -Average).Average)
    }
}

$rows | Export-Csv -Path $OutputCsv -NoTypeInformation -Encoding utf8
Write-Host "wrote $OutputCsv"
$rows | Format-Table -AutoSize
