# Helpers for the Castr demo capture sessions: enumerate ALL top-level windows (not just
# Process.MainWindowHandle, which only reports one window even for multi-window host processes
# like Windows Terminal), find by title substring, and position/size precisely.
Add-Type @"
using System;
using System.Text;
using System.Collections.Generic;
using System.Runtime.InteropServices;

public class Win32 {
    public delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("user32.dll")]
    public static extern bool EnumWindows(EnumWindowsProc enumProc, IntPtr lParam);

    [DllImport("user32.dll")]
    public static extern int GetWindowText(IntPtr hWnd, StringBuilder lpString, int nMaxCount);

    [DllImport("user32.dll")]
    public static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll")]
    public static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);

    [DllImport("user32.dll")]
    public static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    public static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    public static extern bool PostMessage(IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    public static List<KeyValuePair<IntPtr,string>> ListVisibleWindows() {
        var results = new List<KeyValuePair<IntPtr,string>>();
        EnumWindows((hWnd, lParam) => {
            if (IsWindowVisible(hWnd)) {
                var sb = new StringBuilder(256);
                GetWindowText(hWnd, sb, 256);
                string title = sb.ToString();
                if (!string.IsNullOrEmpty(title)) {
                    results.Add(new KeyValuePair<IntPtr,string>(hWnd, title));
                }
            }
            return true;
        }, IntPtr.Zero);
        return results;
    }
}
"@

function Find-WindowByTitle {
    param([string]$TitleSubstring, [int]$TimeoutSeconds = 8)
    $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
    while ((Get-Date) -lt $deadline) {
        $windows = [Win32]::ListVisibleWindows()
        foreach ($w in $windows) {
            if ($w.Value -like "*$TitleSubstring*") { return $w.Key }
        }
        Start-Sleep -Milliseconds 200
    }
    return [IntPtr]::Zero
}

function Set-WindowRect {
    param([IntPtr]$Handle, [int]$X, [int]$Y, [int]$W, [int]$H)
    if ($Handle -eq [IntPtr]::Zero) { return $false }
    [Win32]::ShowWindow($Handle, 9) | Out-Null  # SW_RESTORE
    return [Win32]::SetWindowPos($Handle, [IntPtr]::Zero, $X, $Y, $W, $H, 0x0040) # SWP_SHOWWINDOW
}

# Reposition ONLY (SWP_NOSIZE) - resizing a console window AFTER Spectre.Console's LiveDisplay has
# started painting desyncs its cursor-position repaint math (it measures the buffer once, then repaints
# via relative cursor-up escapes; a live resize mid-render shifts what those escapes land on, producing
# scrambled/overlapping frames). Console geometry must instead be fixed BEFORE launch via `mode con`
# in the .bat file (see Start-TitledConsole) - this function only ever moves the window afterward.
function Set-WindowPosition {
    param([IntPtr]$Handle, [int]$X, [int]$Y)
    if ($Handle -eq [IntPtr]::Zero) { return $false }
    [Win32]::ShowWindow($Handle, 9) | Out-Null  # SW_RESTORE
    $SWP_NOSIZE = 0x0001; $SWP_SHOWWINDOW = 0x0040
    return [Win32]::SetWindowPos($Handle, [IntPtr]::Zero, $X, $Y, 0, 0, ($SWP_NOSIZE -bor $SWP_SHOWWINDOW))
}

# Starts a console command via Windows Terminal in a forced-new window, with a unique title we can
# find afterward via Find-WindowByTitle. Returns the launcher process (NOT necessarily the window
# owner - WT consolidates windows under one host process) - only useful for later cleanup via title.
function Close-WindowAndDescendants {
    param([IntPtr]$Handle)
    if ($Handle -eq [IntPtr]::Zero) { return }
    $ownerPid = 0
    [Win32]::GetWindowThreadProcessId($Handle, [ref]$ownerPid) | Out-Null
    # WM_CLOSE=0x0010 politely asks the WT tab/window to close (kills the console + its child process tree).
    [Win32]::PostMessage($Handle, 0x0010, [IntPtr]::Zero, [IntPtr]::Zero) | Out-Null
}

function Stop-DemoProcessTree {
    param([string]$MatchCommandLine)
    Get-CimInstance Win32_Process | Where-Object { $_.CommandLine -like "*$MatchCommandLine*" } | ForEach-Object {
        try { Stop-Process -Id $_.ProcessId -Force -ErrorAction SilentlyContinue } catch {}
    }
}

function Start-TitledConsole {
    # Writes a standalone .bat file (title line + command line, each fully self-contained) and runs
    # that instead of threading a composed command through wt.exe's -> cmd.exe's -> the target's own
    # argument parsers, which mangles nested quotes/&&-chaining across that many layers.
    #
    # Sizing: wt.exe's own `--size cols,rows` LAUNCH flag is what actually controls the window's pixel
    # dimensions (calibrated empirically: W(px) = 9*cols + 49, H(px) = 19*lines + 65 on this machine's
    # default WT profile/font). An in-session `mode con:` does NOT control outer window size - it's a
    # no-op for the window chrome (verified: identical window pixel size at cols=90/72, two different
    # `mode con` values). Geometry must be correct from launch and never change afterward - a resize
    # AFTER Spectre.Console's LiveDisplay starts painting desyncs its cursor-position repaint math
    # (its relative cursor-up escapes land on the wrong rows once the viewport shifts underneath it),
    # producing scrambled/overlapping frames. So: size only via --size at launch, position only via
    # Set-WindowPosition (SWP_NOSIZE) afterward - never SetWindowRect (which also resizes) post-launch.
    param([string]$Title, [string]$Exe, [string]$CmdArgs, [string]$WorkDir, [int]$Cols = 100, [int]$Lines = 28)
    $batPath = Join-Path $env:TEMP "castr-demo-$Title.bat"
    @"
@echo off
title $Title
"$Exe" $CmdArgs
"@ | Set-Content -Path $batPath -Encoding ASCII
    Start-Process -FilePath "wt.exe" -ArgumentList @('-w', 'new', '--size', "$Cols,$Lines", 'cmd', '/k', $batPath) -WorkingDirectory $WorkDir
}
