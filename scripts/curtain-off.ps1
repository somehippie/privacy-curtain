<#
.SYNOPSIS
  Turns the privacy curtain off. This is the remote unlock: run it from Remote Control or any shell.

.DESCRIPTION
  Standalone on purpose. It loads no other script and reads no state file, so it keeps working even if
  the rest of the tool is broken. It asks the curtain to close, waits a few seconds, and if the curtain
  is still there it kills the process. Either way the keyboard and mouse come back, because the block
  only exists while that process is alive.

  Exit code 0 means no curtain is running any more. Exit code 1 means one is still running.

.PARAMETER Force
  Skip the polite request and kill the process straight away.

.PARAMETER WaitSeconds
  How long to wait for a polite close before killing. Default 5.
#>
[CmdletBinding()]
param(
    [switch]$Force,
    [int]$WaitSeconds = 5,
    # Where the curtain keeps its files. Only the tests pass this.
    [string]$LocalDir
)

$ErrorActionPreference = 'Continue'
$exe = Join-Path (Split-Path -Parent $PSScriptRoot) 'bin\ClaudeCurtain.exe'
if (-not $LocalDir) { $LocalDir = Join-Path (Split-Path -Parent $PSScriptRoot) 'local' }
$began = Get-Date

function Show-Summary {
    # The curtain writes a "While you were away" summary as it closes. Printing it is a courtesy:
    # nothing about turning the curtain off depends on it.
    try {
        $file = Join-Path $LocalDir 'last-summary.txt'
        if ((Test-Path $file) -and (Get-Item $file).LastWriteTime -ge $began.AddSeconds(-2)) {
            Write-Output ''
            Get-Content $file | Where-Object { $_ } | ForEach-Object { Write-Output $_ }
        } else {
            Write-Output 'No summary was written (the curtain was killed rather than closed). See curtain-log.ps1.'
        }
    } catch { }
}

function Get-Curtain {
    @(Get-Process -Name 'ClaudeCurtain' -ErrorAction SilentlyContinue | Where-Object {
        $path = $null
        try { $path = $_.Path } catch { }
        # If the path cannot be read, assume it is ours: closing a curtain by mistake is the safe mistake.
        (-not $path) -or ($path -ieq $exe)
    })
}

if ((Get-Curtain).Count -eq 0) {
    Write-Output 'Curtain is not running.'
    exit 0
}

$how = 'closed on request'
if (-not $Force) {
    try {
        $signal = [System.Threading.EventWaitHandle]::OpenExisting('Local\ClaudeCurtain.Stop')
        [void]$signal.Set()
        $signal.Dispose()
    } catch {
        $Force = $true
    }
}

if (-not $Force) {
    $giveUp = (Get-Date).AddSeconds($WaitSeconds)
    while ((Get-Date) -lt $giveUp -and (Get-Curtain).Count -gt 0) { Start-Sleep -Milliseconds 100 }
}

if ((Get-Curtain).Count -gt 0) {
    $how = 'process killed'
    foreach ($p in Get-Curtain) {
        try { Stop-Process -Id $p.Id -Force -ErrorAction Stop } catch { }
    }
    Start-Sleep -Milliseconds 400
}

if ((Get-Curtain).Count -gt 0) {
    # A second, different way of doing the same thing.
    & "$env:WINDIR\System32\taskkill.exe" /F /T /IM ClaudeCurtain.exe | Out-Null
    Start-Sleep -Milliseconds 600
}

if ((Get-Curtain).Count -eq 0) {
    Write-Output "Curtain is OFF ($how)."
    Show-Summary
    exit 0
}

Write-Output 'Curtain is STILL RUNNING. From any shell, try:  taskkill /F /IM ClaudeCurtain.exe'
Write-Output 'At the PC: type the PIN then Enter, or Ctrl+Alt+Del > Sign out, or press the power button.'
exit 1
