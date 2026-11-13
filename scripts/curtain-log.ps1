<#
.SYNOPSIS
  Shows the most recent curtain events: starts and stops, wrong PIN attempts, blocked keyboard and
  mouse use (counts only), the security screen, locks, network drops, kills. Read-only.

.PARAMETER Count
  How many events to show. Default 40.

.PARAMETER NoHeartbeat
  Leave out the once-a-minute "curtain up, no physical input" lines.

.PARAMETER Summary
  Also print the last "While you were away" summary.
#>
[CmdletBinding()]
param(
    [int]$Count = 40,
    [switch]$NoHeartbeat,
    [switch]$Summary,
    # Only the tests pass this.
    [string]$LocalDir
)

$ErrorActionPreference = 'Stop'
if (-not $LocalDir) { $LocalDir = Join-Path (Split-Path -Parent $PSScriptRoot) 'local' }
$file = Join-Path $LocalDir 'events.log'

if (-not (Test-Path $file)) {
    Write-Host 'No curtain events have been logged yet.'
    exit 0
}

$events = @(Get-Content $file | ForEach-Object {
    $f = $_ -split "`t"
    if ($f.Count -ge 4) { [pscustomobject]@{ Time = $f[0]; Pid = $f[1]; Kind = $f[2]; Text = $f[3] } }
})
if ($NoHeartbeat) { $events = @($events | Where-Object { $_.Kind -ne 'heartbeat' }) }

$events | Select-Object -Last $Count | ForEach-Object {
    Write-Host ("{0}  {1,-15} {2}" -f $_.Time, $_.Kind, $_.Text)
}
Write-Host ("({0} of {1} events, {2:n0} KB of 1,000 KB)" -f [math]::Min($Count, $events.Count), $events.Count, ((Get-Item $file).Length / 1KB))

if ($Summary) {
    $s = Join-Path $LocalDir 'last-summary.txt'
    if (Test-Path $s) { Write-Host ''; Get-Content $s | Where-Object { $_ } | ForEach-Object { Write-Host $_ } }
}
