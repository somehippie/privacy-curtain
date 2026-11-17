<#
.SYNOPSIS
  Desktop-friendly launcher for the privacy curtain: asks how many hours, then turns it on.

.DESCRIPTION
  Meant to sit behind a desktop shortcut. It opens a small dialog asking how many hours the
  curtain should stay up (prefilled 12, any whole number 1-24), then starts the curtain with no
  console window by calling curtain-on.ps1 -Hours <n>. Cancel the dialog and nothing happens.

  The dialog is drawn by the curtain itself (ClaudeCurtain.exe --ask-launch), in the curtain's
  own design: a dark window with the hours as a large figure, minus and plus, presets from 1 to
  24 hours, and the time the curtain will close. Digits and the arrow keys set the hours, Tab
  moves, Space flips the switch, Enter raises the curtain, Esc cancels.

  Like curtain-on, it refuses to start when no unlock PIN is set, since there would be no way
  to unlock at the keyboard. No admin rights are needed and nothing is installed.

  The dialog also has a switch, "Open Claude Code with Remote Control". On, it starts a Claude
  Code Remote Control session (named "privacy-curtain") just before the curtain goes up, so this
  PC stays reachable from another device while the screen is dark, for example to run
  curtain-off.ps1 remotely: a Windows Terminal window running claude --remote-control, which the
  curtain then covers. The switch starts off when a
  Claude Code session on this PC already has Remote Control on (however it was turned on), so
  launching the curtain again does not pile up windows; the line under it says which session.
  Without Claude Code installed the switch is off and greyed out. Starting it is best-effort: a
  problem never stops the curtain.

  All paths are resolved from $PSScriptRoot, so this script carries no machine-specific paths.

.PARAMETER Preview
  Draw the dialog, as it would open now, into this PNG instead of showing it, and stop there.
  Nothing is started. For checking its layout.
#>
[CmdletBinding()]
param(
    [string]$Preview
)

$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'curtain-common.ps1')

function Show-Note([string]$Text, [string]$Icon = 'Information') {
    # Only for the rare problems. Sharp text on a scaled display needs the process to be DPI aware.
    Add-Type -AssemblyName System.Windows.Forms
    try {
        Add-Type -TypeDefinition @'
using System.Runtime.InteropServices;
namespace CurtainLaunch { public static class Dpi { [DllImport("user32.dll")] public static extern bool SetProcessDPIAware(); } }
'@
        [void][CurtainLaunch.Dpi]::SetProcessDPIAware()
    } catch { }
    [System.Windows.Forms.Application]::EnableVisualStyles()
    [System.Windows.Forms.MessageBox]::Show(
        $Text, 'Privacy Curtain',
        [System.Windows.Forms.MessageBoxButtons]::OK,
        [System.Windows.Forms.MessageBoxIcon]::$Icon) | Out-Null
}

function Show-LaunchDialog([string]$Hours, $Advice, [string]$PreviewFile) {
    # The dialog lives in the curtain's exe. It gets what is known here in a small file (the hours to
    # start from, the switch's state, the line under it) and, on "Raise curtain", writes the chosen hours
    # and the switch back into the same file and exits 0; Cancel or Esc exit 20. Returns the hours and
    # the switch, or $null on Cancel. With PreviewFile it only draws the dialog into that PNG and returns
    # its size.
    $ask = Join-Path $env:TEMP ('curtain-launch-{0}.txt' -f $PID)
    $remote = if (-not $Advice.Enabled) { 'unavailable' } elseif ($Advice.Check) { 'on' } else { 'off' }
    [System.IO.File]::WriteAllLines($ask, [string[]]@("hours=$Hours", "remote=$remote", "note=$($Advice.Note)"),
                                    (New-Object System.Text.UTF8Encoding $false))
    try {
        $exeArgs = @('--ask-launch', "`"$ask`"")
        if ($PreviewFile) { $exeArgs += @('--render-preview', "`"$PreviewFile`"") }
        $proc = Start-Process -FilePath $script:CurtainExe -ArgumentList $exeArgs -Wait -PassThru
        if ($PreviewFile) {
            if ($proc.ExitCode -ne 0 -or -not (Test-Path $PreviewFile)) { return $null }
            Add-Type -AssemblyName System.Drawing
            $img = [System.Drawing.Image]::FromFile($PreviewFile)
            try { return $img.Size } finally { $img.Dispose() }
        }
        if ($proc.ExitCode -eq 20) { return $null }
        if ($proc.ExitCode -ne 0) { throw "the dialog closed with code $($proc.ExitCode)" }
        $answer = @{}
        foreach ($line in [System.IO.File]::ReadAllLines($ask)) {
            $kv = $line -split '=', 2
            if ($kv.Count -eq 2) { $answer[$kv[0].Trim()] = $kv[1].Trim() }
        }
        $h = 0.0
        if (-not [double]::TryParse([string]$answer['hours'], [System.Globalization.NumberStyles]::Float,
                                    [System.Globalization.CultureInfo]::InvariantCulture, [ref]$h) -or $h -lt 1 -or $h -gt 24) {
            throw "the dialog answered '$($answer['hours'])' hours"
        }
        return @{ Ok = $true; Hours = $h; RemoteControl = ($answer['remote'] -eq 'on') }
    } finally {
        Remove-Item -LiteralPath $ask -Force -ErrorAction SilentlyContinue
    }
}

# Same rule as curtain-on: no PIN, no start.
if (-not $Preview -and -not (Test-Path (Join-Path $script:CurtainLocal 'pin.hash'))) {
    Show-Note "No unlock PIN is set yet, so the curtain will not start (there would be no way to unlock it at the keyboard).`n`nAt the PC, run curtain-setpin.ps1 first, then try again." 'Warning'
    return
}
if (-not (Test-Path $script:CurtainExe)) {
    Show-Note "The curtain is not built yet ($script:CurtainExe is missing).`n`nRun scripts\build.ps1 first, then try again." 'Warning'
    return
}

# Where Claude Code is, and whether a session here already has Remote Control on.
$claude = (Get-Command 'claude' -ErrorAction SilentlyContinue).Source
if (-not $claude) { $claude = Join-Path $env:USERPROFILE '.local\bin\claude.exe' }
$claudeFound = Test-Path $claude
$sessions = @()
try { $sessions = @(Get-ClaudeSession) } catch { }
$advice = Get-RemoteControlAdvice $sessions $claudeFound

if ($Preview) {
    $size = Show-LaunchDialog '12' $advice $Preview
    Write-Host ("Drew the dialog ({0} x {1}) into {2}. The Remote Control switch starts {3}: {4}" -f $size.Width, $size.Height, $Preview,
                $(if ($advice.Check) { 'on' } else { 'off' }), $advice.Note)
    return
}
try {
    $result = Show-LaunchDialog '12' $advice
} catch {
    Show-Note "The curtain's dialog could not open: $($_.Exception.Message).`n`nFor details, run curtain-log.ps1 at the PC." 'Error'
    return
}
if (-not $result) { return }
$hours = $result.Hours

# Start Claude Code Remote Control first, so this PC is reachable (e.g. to run curtain-off.ps1
# remotely) by the time the screen goes dark: a Windows Terminal window running
# claude --remote-control <name>. The curtain covers it. Best-effort: never blocks the curtain.
$repoRoot = $script:CurtainRoot   # the repository root (the folder above scripts\)
$wt = Join-Path $env:LOCALAPPDATA 'Microsoft\WindowsApps\wt.exe'
if ($result.RemoteControl) {
    try {
        if ((Test-Path $wt) -and $claudeFound) {
            # One argument string. Start-Process joins an array with spaces and adds no quotes, so the
            # title must carry its own quotes: unquoted, wt took
            # only "Privacy" as the title and tried to run "Curtain Remote ..." as the command (0x80070002).
            $wtArgs = "-d `"$repoRoot`" --title `"Privacy Curtain Remote`" `"$claude`" --remote-control privacy-curtain"
            Start-Process -FilePath $wt -ArgumentList $wtArgs | Out-Null
        } elseif ($claudeFound) {
            # No Windows Terminal: fall back to claude in its own minimized console.
            Start-Process -FilePath $claude -WorkingDirectory $repoRoot -WindowStyle Minimized `
                -ArgumentList @('--remote-control', 'privacy-curtain') | Out-Null
        } else {
            Show-Note "Remote Control was not started: the 'claude' CLI could not be found. The curtain will still go up." 'Warning'
        }
    } catch {
        Show-Note "Remote Control could not be started: $($_.Exception.Message). The curtain will still go up." 'Warning'
    }
}

# Start the curtain in a hidden PowerShell so no console window appears.
$curtainOn = Join-Path $PSScriptRoot 'curtain-on.ps1'
$hoursArg  = $hours.ToString([System.Globalization.CultureInfo]::InvariantCulture)
$proc = Start-Process -FilePath 'powershell.exe' -WindowStyle Hidden -PassThru -Wait -ArgumentList @(
    '-NoProfile', '-ExecutionPolicy', 'Bypass', '-WindowStyle', 'Hidden',
    '-File', "`"$curtainOn`"", '-Hours', $hoursArg)

switch ($proc.ExitCode) {
    0 { }  # Up and running; the curtain is now visible, so stay quiet.
    3 { Show-Note 'The curtain is already on.' 'Information' }
    default {
        Show-Note "The curtain could not start (code $($proc.ExitCode)).`n`nFor details, run curtain-log.ps1 at the PC." 'Error'
    }
}
