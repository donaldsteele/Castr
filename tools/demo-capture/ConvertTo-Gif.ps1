# Converts one of the capture scripts' raw .mp4 recordings into an optimized, palette-generated GIF
# (two-pass palettegen/paletteuse - much better quality per byte than a naive single-pass GIF encode).
#
# Usage:
#   .\ConvertTo-Gif.ps1 -InputMp4 .\workspace\media\raw\cli-sysadmin.mp4 -OutputGif ..\..\docs\media\sysadmin-fleet-push-cli.gif -Start 1.2 -End 23 -Fps 8 -Width 1300
#
# Defaults (Fps 8, Width 1300) match what was used for all three showcase GIFs in docs/media/ - keep
# them consistent if you're regenerating one to match the others. Trim Start/End to skip the
# window-launch/positioning moment at the very beginning and any dead air at the end.
#
# -Crop "W:H:X:Y" trims the surrounding desktop before scaling. The capture scripts record the whole
# screen via ddagrab (see capture-cli.ps1 for why), so anything the demo windows don't cover - a file
# tree, an editor, whatever happened to be behind them - lands in the recording and then in the GIF.
# Crop to the window bounds rather than relying on nothing being open. Verify the region by extracting
# one frame first (`ffmpeg -ss <t> -i in.mp4 -frames:v 1 -vf "crop=W:H:X:Y" probe.png`) and looking at
# it; guessing the numbers wastes a full re-record.
param(
    [Parameter(Mandatory)] [string]$InputMp4,
    [Parameter(Mandatory)] [string]$OutputGif,
    [double]$Start = 0,
    [double]$End = 0,        # 0 = to end of clip
    [int]$Fps = 8,
    [int]$Width = 1300,
    [string]$Crop = ''       # '' = no crop; otherwise ffmpeg crop geometry "W:H:X:Y"
)
$ErrorActionPreference = 'Stop'

if (-not (Test-Path $InputMp4)) { throw "Input not found: $InputMp4" }

$toArg = if ($End -gt 0) { @('-to', "$End") } else { @() }
$cropStage = if ($Crop) { "crop=$Crop," } else { '' }
$vf = "${cropStage}fps=$Fps,scale=${Width}:-1:flags=lanczos,split[s0][s1];[s0]palettegen=stats_mode=diff[p];[s1][p]paletteuse=dither=bayer"

& ffmpeg -y -ss $Start @toArg -i $InputMp4 -vf $vf $OutputGif
Write-Output "Wrote: $OutputGif"
Get-Item $OutputGif | Select-Object Length, LastWriteTime
