# Captures the "sysadmin fleet push" demo: two castr.exe consoles (receiver + sender) side by side over
# real loopback multicast. Run New-DemoFiles.ps1 once first. Output: workspace\media\raw\cli-sysadmin.mp4
# (convert to GIF with ConvertTo-Gif.ps1). See README.md for prerequisites and known gotchas.
$ErrorActionPreference = 'Stop'
$Root = if ($env:CASTR_DEMO_ROOT) { $env:CASTR_DEMO_ROOT } else { Join-Path $PSScriptRoot "workspace" }
. "$PSScriptRoot\WinPos.ps1"

$RepoRoot = Resolve-Path (Join-Path $PSScriptRoot "..\..")
$Castr = Join-Path $RepoRoot "src\Castr.Cli\bin\Release\net10.0\castr.exe"
$File  = "$Root\files\fleet_config_bundle_v2.tar.gz"
$Dest  = "$Root\receive1"
Remove-Item "$Dest\*" -Force -ErrorAction SilentlyContinue

# Clean process/window slate
Stop-DemoProcessTree -MatchCommandLine "CastrReceiver-Ops"
Stop-DemoProcessTree -MatchCommandLine "CastrSender-Ops"
Start-Sleep -Milliseconds 500

$recvArgs = "receive --dest-dir `"$Dest`" --trust-store `"$Root\state\trust-sysadmin.json`" --on-unknown-sender Deny"
# No --chunk-size: the demo should show the shipped default. Until M8 that default was 8 KB and this
# line carried an explicit 60000 to avoid the worst of it; M8 raised the default to 256 KB (the value
# Castr.Core and wire-protocol.md had specified all along), which is better than the old override, so
# the workaround is gone and the capture now reflects what a user actually gets out of the box.
$sendArgs = "send `"$File`" --identity `"$Root\state\sender-identity.key`""

Start-TitledConsole -Title "CastrReceiver-Ops" -Exe $Castr -CmdArgs $recvArgs -WorkDir $Root -Cols 94 -Lines 26
Start-Sleep -Milliseconds 1500
$hRecv = Find-WindowByTitle -TitleSubstring "CastrReceiver-Ops" -TimeoutSeconds 8
Set-WindowPosition -Handle $hRecv -X 10 -Y 10 | Out-Null
Start-Sleep -Milliseconds 500

# Region to capture: covers both the receiver (left) and where the sender window will appear (right).
# ddagrab (DXGI Desktop Duplication) instead of gdigrab: Windows Terminal is GPU-composited, and
# gdigrab's legacy BitBlt capture desaturates/washes out DWM-composited windows - confirmed by a direct
# side-by-side test (a plain GDI screenshot showed full color; the same window via gdigrab was monochrome).
$regionX = 0; $regionY = 0; $regionW = 1900; $regionH = 620

$outMp4 = "$Root\media\raw\cli-sysadmin.mp4"
Remove-Item $outMp4 -Force -ErrorAction SilentlyContinue
$ffArgs = @(
  '-y','-f','lavfi','-i',"ddagrab=output_idx=0:framerate=12",
  '-vf',"hwdownload,format=bgra,crop=${regionW}:${regionH}:${regionX}:${regionY},format=yuv444p",
  '-t','24',
  '-vcodec','libx264','-crf','16','-preset','veryfast',
  $outMp4
)
$ffProc = Start-Process -FilePath "ffmpeg" -ArgumentList $ffArgs -PassThru -WindowStyle Hidden

Start-Sleep -Milliseconds 800
Start-TitledConsole -Title "CastrSender-Ops" -Exe $Castr -CmdArgs $sendArgs -WorkDir $Root -Cols 94 -Lines 26
$hSend = Find-WindowByTitle -TitleSubstring "CastrSender-Ops" -TimeoutSeconds 8
# The two consoles must sit flush: the capture is a full-screen ddagrab, so any gap between them
# shows whatever is on the desktop behind, and that lands in the GIF.
#
# The non-obvious part: GetWindowRect reports 895x483 for a 94-col console placed at (10,10), but
# ~7px on the left, right and bottom is the invisible DWM drop-shadow rather than painted pixels.
# Tiling on those numbers leaves a ~14px seam of visible desktop. Overlap by the shadow width.
# See ConvertTo-Gif.ps1's -Crop for trimming the outer edge away.
$ShadowPx = 7
Set-WindowPosition -Handle $hSend -X (10 + 895 - (2 * $ShadowPx)) -Y 10 | Out-Null   # 891

# Wait for ffmpeg's -t 24 to finish
$ffProc.WaitForExit()
Write-Output "Recording done: $outMp4"
Get-Item $outMp4 | Select-Object Length, LastWriteTime

Start-Sleep -Milliseconds 500
Close-WindowAndDescendants -Handle $hRecv
Close-WindowAndDescendants -Handle $hSend
Stop-DemoProcessTree -MatchCommandLine "CastrReceiver-Ops"
Stop-DemoProcessTree -MatchCommandLine "CastrSender-Ops"
