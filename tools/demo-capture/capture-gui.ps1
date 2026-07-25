# Captures the "LAN party" demo: two Castr.Gui.Desktop.exe instances (sender + receiver) side by side,
# driven via UI Automation (set file path -> Start send / Start listening -> accept the real TOFU trust
# dialog -> watch it complete). Run New-DemoFiles.ps1 once first. Output:
# workspace\media\raw\gui-lanparty.mp4 (convert with ConvertTo-Gif.ps1) plus two static PNGs
# (gui-trust-dialog.png, gui-final-state.png) written straight into workspace\media\frames\.
# See README.md for prerequisites and known gotchas.
$ErrorActionPreference = 'Stop'
$Root = if ($env:CASTR_DEMO_ROOT) { $env:CASTR_DEMO_ROOT } else { Join-Path $PSScriptRoot "workspace" }
. "$PSScriptRoot\WinPos.ps1"
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes, System.Windows.Forms

$RepoRoot = Resolve-Path (Join-Path $PSScriptRoot "..\..")
$Exe = Join-Path $RepoRoot "src\Castr.Gui.Desktop\bin\Release\net10.0\Castr.Gui.Desktop.exe"
$File = "$Root\files\SuperRaid_ModPack_v3.zip"
$DestDir = "$Root\receive-gui"
Remove-Item "$DestDir\*" -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Force -Path $DestDir | Out-Null

# Fresh trust-on-first-use each take: clear any prior trust decision so the dialog reliably reappears.
$trustFile = Join-Path $env:APPDATA "Castr\trusted-senders.json"
Remove-Item $trustFile -Force -ErrorAction SilentlyContinue

Get-Process -Name "Castr.Gui.Desktop" -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
Start-Sleep -Milliseconds 500

function Get-AutomationRoot([int]$HandleInt) {
    return [System.Windows.Automation.AutomationElement]::FromHandle([IntPtr]$HandleInt)
}
function Find-ByType($Root, [string]$TypeName) {
    $cond = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
        [System.Windows.Automation.ControlType]::$TypeName)
    return $Root.FindAll([System.Windows.Automation.TreeScope]::Descendants, $cond)
}
function Find-ByTypeAndName($Root, [string]$TypeName, [string]$Name) {
    $all = Find-ByType $Root $TypeName
    foreach ($el in $all) { if ($el.Current.Name -eq $Name) { return $el } }
    return $null
}
function Invoke-Element($el) {
    $pat = $el.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern)
    $pat.Invoke()
}
function Set-ElementValue($el, [string]$Value) {
    $pat = $el.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern)
    $pat.SetValue($Value)
}

# --- Launch both instances ---
$pSender = Start-Process -FilePath $Exe -PassThru
Start-Sleep -Seconds 2
$pRecv = Start-Process -FilePath $Exe -PassThru
Start-Sleep -Seconds 2

Get-Process -Id $pSender.Id | Out-Null
$hSenderWin = (Get-Process -Id $pSender.Id).MainWindowHandle
$hRecvWin = (Get-Process -Id $pRecv.Id).MainWindowHandle
Write-Output "sender handle=$hSenderWin recv handle=$hRecvWin"

# The two windows must sit flush: the capture is a full-screen ddagrab, so any gap between them shows
# whatever is on the desktop behind, and that lands in the GIF. Windows reports a rect ~7px wider and
# taller than the pixels actually painted (the invisible DWM drop-shadow), so tiling at X=800 - the
# sender's reported right edge of 770 plus a margin - left a visibly bleeding ~44px seam. Overlap by
# the shadow width instead: sender's visible right edge is 10+760-7 = 763, so the receiver starts at
# 763-7 = 756. See ConvertTo-Gif.ps1's -Crop for trimming the outer edge.
$ShadowPx = 7
Set-WindowRect -Handle $hSenderWin -X 10 -Y 20 -W 760 -H 560 | Out-Null
Set-WindowRect -Handle $hRecvWin   -X (10 + 760 - (2 * $ShadowPx)) -Y 20 -W 760 -H 560 | Out-Null   # 756
Start-Sleep -Milliseconds 500

$rootSender = Get-AutomationRoot ([int]$hSenderWin)
$rootRecv = Get-AutomationRoot ([int]$hRecvWin)

# --- Configure receiver: switch to Receive tab, set policy to Prompt, set dest dir, start listening ---
$recvTab = Find-ByTypeAndName $rootRecv "TabItem" "Receive"
$recvTab.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select()
Start-Sleep -Milliseconds 400

$combo = (Find-ByType $rootRecv "ComboBox")[0]
$combo.SetFocus()
[System.Windows.Forms.SendKeys]::SendWait("{DOWN}{ENTER}")
Start-Sleep -Milliseconds 300

$recvEdits = Find-ByType $rootRecv "Edit"
Set-ElementValue $recvEdits[0] $DestDir
Start-Sleep -Milliseconds 200

$startListenBtn = Find-ByTypeAndName $rootRecv "Button" "Start listening"
Invoke-Element $startListenBtn
Start-Sleep -Milliseconds 500

# --- Start recording now: empty/idle moment, then the live flow ---
$outMp4 = "$Root\media\raw\gui-lanparty.mp4"
Remove-Item $outMp4 -Force -ErrorAction SilentlyContinue
$ffArgs = @(
  '-y','-f','lavfi','-i','ddagrab=output_idx=0:framerate=12',
  '-vf','hwdownload,format=bgra,crop=1580:620:0:0,format=yuv444p',
  '-t','50',
  '-vcodec','libx264','-crf','16','-preset','veryfast',
  $outMp4
)
$ffProc = Start-Process -FilePath "ffmpeg" -ArgumentList $ffArgs -PassThru -WindowStyle Hidden

Start-Sleep -Milliseconds 800

# --- Configure sender: set file path, start send ---
$senderEdits = Find-ByType $rootSender "Edit"
Set-ElementValue $senderEdits[0] $File
Start-Sleep -Milliseconds 300
$startSendBtn = Find-ByTypeAndName $rootSender "Button" "Start send"
Invoke-Element $startSendBtn

# --- Wait for the TOFU trust dialog to appear on the receiver side, then accept it ---
$dialogHandle = Find-WindowByTitle -TitleSubstring "Trust this sender" -TimeoutSeconds 10
Write-Output "dialog handle=$dialogHandle"
if ($dialogHandle -ne [IntPtr]::Zero) {
    Set-WindowPosition -Handle $dialogHandle -X 900 -Y 150 | Out-Null
    Start-Sleep -Milliseconds 1800
    Add-Type -AssemblyName System.Drawing
    $bmpD = New-Object System.Drawing.Bitmap 1580, 620
    $gD = [System.Drawing.Graphics]::FromImage($bmpD)
    $gD.CopyFromScreen(0, 0, 0, 0, $bmpD.Size)
    $bmpD.Save("$Root\media\frames\gui-trust-dialog.png", [System.Drawing.Imaging.ImageFormat]::Png)
    $gD.Dispose(); $bmpD.Dispose()
    $rootDialog = [System.Windows.Automation.AutomationElement]::FromHandle($dialogHandle)
    $acceptBtn = Find-ByTypeAndName $rootDialog "Button" "Trust and accept"
    Invoke-Element $acceptBtn
} else {
    Write-Output "WARNING: trust dialog never appeared"
}

$ffProc.WaitForExit()
Write-Output "Recording done: $outMp4"
Get-Item $outMp4 | Select-Object Length, LastWriteTime

# Static completion screenshot
Add-Type -AssemblyName System.Drawing
Start-Sleep -Milliseconds 500
$bmp = New-Object System.Drawing.Bitmap 1580, 620
$g = [System.Drawing.Graphics]::FromImage($bmp)
$g.CopyFromScreen(0, 0, 0, 0, $bmp.Size)
$bmp.Save("$Root\media\frames\gui-final-state.png", [System.Drawing.Imaging.ImageFormat]::Png)
$g.Dispose(); $bmp.Dispose()

Write-Output "DONE"
