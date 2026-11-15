<#
.SYNOPSIS
  Desktop-friendly launcher for the privacy curtain: asks how many hours, then turns it on.

.DESCRIPTION
  Meant to sit behind a desktop shortcut. It pops a small dialog asking how many hours the
  curtain should stay up (prefilled 12, any value 1-24), then starts the curtain with no console
  window by calling curtain-on.ps1 -Hours <n>. Cancel the dialog and nothing happens.

  Like curtain-on, it refuses to start when no unlock PIN is set, since there would be no way
  to unlock at the keyboard. No admin rights are needed and nothing is installed.

  Just before raising the curtain it also starts a Claude Code Remote Control session (named
  "privacy-curtain"), so this PC stays reachable from another device while the screen is dark,
  for example to run curtain-off.ps1 remotely: a Windows Terminal window running
  claude --remote-control, which the curtain then covers. It is best-effort: a problem starting
  it (or no Claude Code installed) never stops the curtain.

  All paths are resolved from $PSScriptRoot, so this script carries no machine-specific paths.
#>
[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'curtain-common.ps1')

Add-Type -AssemblyName Microsoft.VisualBasic
Add-Type -AssemblyName System.Windows.Forms

function Show-Note([string]$Text, [string]$Icon = 'Information') {
    [System.Windows.Forms.MessageBox]::Show(
        $Text, 'Privacy Curtain',
        [System.Windows.Forms.MessageBoxButtons]::OK,
        [System.Windows.Forms.MessageBoxIcon]::$Icon) | Out-Null
}

# Same rule as curtain-on: no PIN, no start.
if (-not (Test-Path (Join-Path $script:CurtainLocal 'pin.hash'))) {
    Show-Note "No unlock PIN is set yet, so the curtain will not start (there would be no way to unlock it at the keyboard).`n`nAt the PC, run curtain-setpin.ps1 first, then try again." 'Warning'
    return
}

# Ask for the number of hours. Cancel (or an empty box) does nothing.
$answer = [Microsoft.VisualBasic.Interaction]::InputBox(
    'Close the privacy curtain automatically after how many hours? (1-24)',
    'Privacy Curtain', '12')
if ([string]::IsNullOrWhiteSpace($answer)) { return }

$hours = 0.0
if (-not [double]::TryParse($answer.Trim(),
        [System.Globalization.NumberStyles]::Float,
        [System.Globalization.CultureInfo]::InvariantCulture, [ref]$hours) `
    -or $hours -lt 1 -or $hours -gt 24) {
    Show-Note "Please enter a number of hours between 1 and 24. Nothing was started." 'Warning'
    return
}

# Start Claude Code Remote Control first, so this PC is reachable (e.g. to run curtain-off.ps1
# remotely) by the time the screen goes dark: a Windows Terminal window running
# claude --remote-control <name>. The curtain covers it. Best-effort: never blocks the curtain.
$claude = (Get-Command 'claude' -ErrorAction SilentlyContinue).Source
if (-not $claude) { $claude = Join-Path $env:USERPROFILE '.local\bin\claude.exe' }
$repoRoot = $script:CurtainRoot   # the repository root (the folder above scripts\)
$wt = Join-Path $env:LOCALAPPDATA 'Microsoft\WindowsApps\wt.exe'
try {
    if ((Test-Path $wt) -and (Test-Path $claude)) {
        # One argument string. Start-Process joins an array with spaces and adds no quotes, so the title must carry its own quotes: unquoted, wt took
        # only "Privacy" as the title and tried to run "Curtain Remote ..." as the command (0x80070002).
        $wtArgs = "-d `"$repoRoot`" --title `"Privacy Curtain Remote`" `"$claude`" --remote-control privacy-curtain"
        Start-Process -FilePath $wt -ArgumentList $wtArgs | Out-Null
    } elseif (Test-Path $claude) {
        # No Windows Terminal: fall back to claude in its own minimized console.
        Start-Process -FilePath $claude -WorkingDirectory $repoRoot -WindowStyle Minimized `
            -ArgumentList @('--remote-control', 'privacy-curtain') | Out-Null
    } else {
        Show-Note "Remote Control was not started: the 'claude' CLI could not be found. The curtain will still go up." 'Warning'
    }
} catch {
    Show-Note "Remote Control could not be started: $($_.Exception.Message). The curtain will still go up." 'Warning'
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
