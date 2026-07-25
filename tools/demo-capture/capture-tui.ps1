# Captures the "test lab" demo: one castr.exe --tui sender fanning out to three independent
# castr.exe --tui receiver processes in a 2x2 grid over real loopback multicast. Run New-DemoFiles.ps1
# once first. Output: workspace\media\raw\tui-lab-fanout.mp4 (convert with ConvertTo-Gif.ps1).
# See README.md for prerequisites and known gotchas (esp. the console-resize-corrupts-LiveDisplay one).
$ErrorActionPreference = 'Stop'
$Root = if ($env:CASTR_DEMO_ROOT) { $env:CASTR_DEMO_ROOT } else { Join-Path $PSScriptRoot "workspace" }
. "$PSScriptRoot\WinPos.ps1"

$RepoRoot = Resolve-Path (Join-Path $PSScriptRoot "..\..")
$Castr = Join-Path $RepoRoot "src\Castr.Cli\bin\Release\net10.0\castr.exe"
$File  = "$Root\files\lab_dataset_batch7.bin"

foreach ($n in 1,2,3) {
    Remove-Item "$Root\receive$n\*" -Force -ErrorAction SilentlyContinue
}

foreach ($t in "CastrLab-Sender","CastrLab-01","CastrLab-02","CastrLab-03") {
    Stop-DemoProcessTree -MatchCommandLine $t
}
Start-Sleep -Milliseconds 500

$recvArgsFor = { param($n) "receive --dest-dir `"$Root\receive$n`" --trust-store `"$Root\state\trust-lab$n.json`" --on-unknown-sender Deny --tui" }
$sendArgs = "send `"$File`" --identity `"$Root\state\sender-identity.key`" --chunk-size 60000 --tui"

# 2x2 grid: sender top-left, receivers fill the rest. Cols/Lines fix the console geometry BEFORE
# launch (mode con, inside Start-TitledConsole) so nothing resizes after Spectre.Console's LiveDisplay
# starts painting - only position (never size) after launch, via Set-WindowPosition.
Start-TitledConsole -Title "CastrLab-01" -Exe $Castr -CmdArgs (& $recvArgsFor 1) -WorkDir $Root -Cols 94 -Lines 22
Start-Sleep -Milliseconds 400
Start-TitledConsole -Title "CastrLab-02" -Exe $Castr -CmdArgs (& $recvArgsFor 2) -WorkDir $Root -Cols 94 -Lines 22
Start-Sleep -Milliseconds 400
Start-TitledConsole -Title "CastrLab-03" -Exe $Castr -CmdArgs (& $recvArgsFor 3) -WorkDir $Root -Cols 94 -Lines 22
Start-Sleep -Milliseconds 400

$h1 = Find-WindowByTitle -TitleSubstring "CastrLab-01" -TimeoutSeconds 8
$h2 = Find-WindowByTitle -TitleSubstring "CastrLab-02" -TimeoutSeconds 8
$h3 = Find-WindowByTitle -TitleSubstring "CastrLab-03" -TimeoutSeconds 8

Set-WindowPosition -Handle $h1 -X 915 -Y 10  | Out-Null
Set-WindowPosition -Handle $h2 -X 10  -Y 503 | Out-Null
Set-WindowPosition -Handle $h3 -X 915 -Y 503 | Out-Null

Start-Sleep -Milliseconds 500

$outMp4 = "$Root\media\raw\tui-lab-fanout.mp4"
Remove-Item $outMp4 -Force -ErrorAction SilentlyContinue
# ddagrab (DXGI Desktop Duplication), not gdigrab: Windows Terminal is GPU-composited and gdigrab's
# legacy BitBlt capture washes out/desaturates DWM-composited windows.
$ffArgs = @(
  '-y','-f','lavfi','-i','ddagrab=output_idx=0:framerate=10',
  '-vf','hwdownload,format=bgra,crop=1920:1050:0:0,format=yuv444p',
  '-t','55',
  '-vcodec','libx264','-crf','16','-preset','veryfast',
  $outMp4
)
$ffProc = Start-Process -FilePath "ffmpeg" -ArgumentList $ffArgs -PassThru -WindowStyle Hidden

Start-Sleep -Milliseconds 1000
Start-TitledConsole -Title "CastrLab-Sender" -Exe $Castr -CmdArgs $sendArgs -WorkDir $Root -Cols 94 -Lines 22
$hSend = Find-WindowByTitle -TitleSubstring "CastrLab-Sender" -TimeoutSeconds 8
Set-WindowPosition -Handle $hSend -X 10 -Y 10 | Out-Null

$ffProc.WaitForExit()
Write-Output "Recording done: $outMp4"
Get-Item $outMp4 | Select-Object Length, LastWriteTime

Start-Sleep -Milliseconds 300
foreach ($h in @($h1,$h2,$h3,$hSend)) { Close-WindowAndDescendants -Handle $h }
foreach ($t in "CastrLab-Sender","CastrLab-01","CastrLab-02","CastrLab-03") {
    Stop-DemoProcessTree -MatchCommandLine $t
}
