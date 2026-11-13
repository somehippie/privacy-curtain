<#
.SYNOPSIS
  Says whether the curtain is on, when it will close by itself, and why the last one closed. Read-only.

.PARAMETER Log
  Also print the last lines of the curtain's log.
#>
[CmdletBinding()]
param(
    [int]$Log = 0,
    # For test-curtain.ps1 only.
    [string]$LocalDir
)

$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'curtain-common.ps1')
if (-not $LocalDir) { $LocalDir = $script:CurtainLocal }

$running = Get-CurtainProcess
$state = Read-CurtainState $LocalDir

if ($running.Count -eq 0) {
    Write-Host 'Curtain is OFF.'
} elseif ($state) {
    $until = ([datetime]$state.deadlineUtc).ToLocalTime()
    $left = ($until - (Get-Date)).TotalSeconds
    Write-Host ("Curtain is ON (pid {0}), covering {1} monitor(s): {2}" -f $state.pid, $state.monitors, $state.layout)
    Write-Host ("Closes by itself at {0:t} (in {1})." -f $until, (Format-CurtainSpan $left))
} else {
    Write-Host ("Curtain is ON (pid {0}), details unavailable." -f (($running | ForEach-Object { $_.Id }) -join ', '))
}

$last = Read-CurtainJson (Join-Path $LocalDir 'last-exit.json')
if ($last) {
    $at = ([datetime]$last.atUtc).ToLocalTime()
    $meaning = switch -Wildcard ($last.reason) {
        'pin'                    { 'unlocked with the PIN' }
        'remote-stop'            { 'closed by curtain-off' }
        'timer'                  { 'the timer ran out' }
        'windows-session-ending' { 'Windows was shutting down or signing out' }
        'window-closed'          { 'its window was asked to close' }
        'watchdog-lost'          { 'its watchdog process disappeared' }
        'watchdog-kill:*'        { 'the watchdog had to kill it' }
        default                  { 'closed after an internal problem' }
    }
    Write-Host ("Last curtain closed {0:g}: {1} ({2})." -f $at, $meaning, $last.reason)
}

$pinSet = Test-Path (Join-Path $LocalDir 'pin.hash')
Write-Host ("Unlock PIN: {0}" -f $(if ($pinSet) { 'set' } else { 'NOT set (run curtain-setpin.ps1 at the PC)' }))

if ($Log -gt 0) {
    Write-Host '--- log ---'
    Get-CurtainLogTail $Log $LocalDir | ForEach-Object { Write-Host $_ }
}
