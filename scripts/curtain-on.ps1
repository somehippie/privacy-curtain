<#
.SYNOPSIS
  Turns the privacy curtain on: a black screen on every monitor, physical keyboard and mouse blocked,
  Claude's own clicks, typing and screenshots unaffected.

.DESCRIPTION
  Ways back in, any one of which is enough:
    - type your PIN, then Enter, on the physical keyboard
    - run curtain-off.ps1 (for example from Remote Control)
    - wait for the timer
    - Ctrl+Alt+Del, or the physical power button: neither is ever intercepted

.PARAMETER Hours
  Close automatically after this many hours. Default 8, at most 24. Fractions are fine.

.PARAMETER Minutes
  Close automatically after this many minutes instead (handy for a test: -Minutes 2).

.PARAMETER Restart
  Replace a curtain that is already running instead of refusing.

.EXAMPLE
  .\curtain-on.ps1
.EXAMPLE
  .\curtain-on.ps1 -Hours 3
.EXAMPLE
  .\curtain-on.ps1 -Minutes 2
#>
[CmdletBinding()]
param(
    [double]$Hours = 8,
    [double]$Minutes = 0,
    [switch]$Restart,
    # The two below are for test-curtain.ps1 only.
    [string]$LocalDir,
    [string[]]$ExtraArgs
)

$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'curtain-common.ps1')
if (-not $LocalDir) { $LocalDir = $script:CurtainLocal }
$LocalDir = $LocalDir.TrimEnd('\')

if ($Minutes -gt 0) { $seconds = [int][math]::Round($Minutes * 60) } else { $seconds = [int][math]::Round($Hours * 3600) }
if ($seconds -lt 10 -or $seconds -gt 86400) {
    Write-Host 'The timer must be between 10 seconds and 24 hours. Nothing was started.'
    exit 2
}

if (Test-CurtainRunning) {
    if (-not $Restart) {
        $state = Read-CurtainState $LocalDir
        if ($state) {
            $until = ([datetime]$state.deadlineUtc).ToLocalTime()
            Write-Host ("The curtain is already on (pid {0}), closing by itself at {1:t}. Use -Restart to replace it." -f $state.pid, $until)
        } else {
            Write-Host 'The curtain is already on. Use -Restart to replace it.'
        }
        exit 3
    }
    & (Join-Path $PSScriptRoot 'curtain-off.ps1') | Out-Null
    if (Test-CurtainRunning) {
        Write-Host 'Could not close the running curtain. Nothing was started.'
        exit 1
    }
}

& (Join-Path $PSScriptRoot 'build.ps1')

$pinFile = Join-Path $LocalDir 'pin.hash'
if (-not (Test-Path $pinFile)) {
    Write-Host 'No unlock PIN is set yet, so the curtain will not start (there would be no way to unlock it at the keyboard).'
    Write-Host "At the PC, run:  & '$(Join-Path $PSScriptRoot 'curtain-setpin.ps1')'"
    exit 4
}

New-Item -ItemType Directory -Force -Path $LocalDir | Out-Null
$stateFile = Join-Path $LocalDir 'state.json'
Remove-Item $stateFile -Force -ErrorAction SilentlyContinue

$argList = @('--seconds', "$seconds", '--local-dir', "`"$LocalDir`"")
if ($ExtraArgs) { $argList += $ExtraArgs }
$proc = Start-Process -FilePath $script:CurtainExe -ArgumentList $argList -PassThru

# Wait for the curtain to say it is fully up (windows shown, hooks installed, watchdog running).
$giveUp = (Get-Date).AddSeconds(15)
$state = $null
while ((Get-Date) -lt $giveUp) {
    if ($proc.HasExited) { break }
    $state = Read-CurtainJson $stateFile
    if ($state -and $state.pid -eq $proc.Id) { break }
    $state = $null
    Start-Sleep -Milliseconds 100
}

if (-not $state) {
    if ($proc.HasExited) {
        Write-Host ("The curtain did not start: {0}." -f (Get-CurtainExitMeaning $proc.ExitCode))
    } else {
        Write-Host 'The curtain did not report ready within 15 seconds. Closing it.'
        & (Join-Path $PSScriptRoot 'curtain-off.ps1') -Force | Out-Null
    }
    Get-CurtainLogTail 6 $LocalDir | ForEach-Object { Write-Host "  $_" }
    exit 1
}

$until = ([datetime]$state.deadlineUtc).ToLocalTime()
Write-Host ("Curtain is ON (pid {0}), covering {1} monitor(s): {2}" -f $state.pid, $state.monitors, $state.layout)
Write-Host ("Closes by itself at {0:t} (in {1})." -f $until, (Format-CurtainSpan $seconds))
Write-Host 'Unlock at the PC : type your PIN, then Enter.'
Write-Host "Unlock remotely  : & '$(Join-Path $PSScriptRoot 'curtain-off.ps1')'"
Write-Host "Event log        : & '$(Join-Path $PSScriptRoot 'curtain-log.ps1')'  (the closing time is not shown on screen)"
Write-Host 'Keep-awake       : sleep, display-off and screen saver are on hold until the curtain closes.'

# Glanceable info on screen: CPU/RAM/network, uptime and disk are live from inside the curtain; the short
# local forecast is refreshed here in the background (keyless, never fatal) and the curtain picks it up live.
try {
    $wx = Join-Path $PSScriptRoot 'curtain-weather.ps1'
    Start-Process -FilePath 'powershell.exe' -WindowStyle Hidden -ArgumentList @(
        '-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', "`"$wx`"", '-LocalDir', "`"$LocalDir`"") | Out-Null
    Write-Host 'On-screen info   : CPU / RAM / network, uptime, disk, and a short local forecast.'
} catch {
    Write-Host 'On-screen info   : CPU / RAM / network, uptime, disk.'
}

# Physical keys can still reach an administrator (elevated) window behind the curtain: warn about any.
try {
    $listFile = Join-Path $LocalDir 'elevated.txt'
    Remove-Item $listFile -Force -ErrorAction SilentlyContinue
    Start-Process -FilePath $script:CurtainExe -ArgumentList @('--list-elevated', '--local-dir', "`"$LocalDir`"", '--out', "`"$listFile`"") -Wait
    $elevated = @(Get-Content $listFile -ErrorAction SilentlyContinue | Where-Object { $_ })
    Remove-Item $listFile -Force -ErrorAction SilentlyContinue
    if ($elevated.Count -gt 0) {
        Write-Host ("WARNING: {0} administrator window(s) are open. The curtain cannot block physical keys or clicks aimed at them. Close them if you can:" -f $elevated.Count)
        foreach ($line in $elevated) {
            $f = $line -split "`t"
            Write-Host ("  - {0} (pid {1}): {2}" -f $f[1], $f[0], $f[2])
        }
    }
} catch {
    Write-Host 'Note: could not check for administrator windows.'
}

$button = Get-PowerButtonAction
if ($button -eq 'Shut down') {
    Write-Host 'Emergency        : Ctrl+Alt+Del, or the power button (set to: Shut down).'
} elseif ($button) {
    Write-Host 'Emergency        : Ctrl+Alt+Del always works.'
    Write-Host ("NOTE: the power button is set to '{0}', not 'Shut down'. Change it under Control Panel > Power Options > Choose what the power buttons do." -f $button)
} else {
    Write-Host 'Emergency        : Ctrl+Alt+Del, or the power button.'
}
exit 0
