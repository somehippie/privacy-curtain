<#
.SYNOPSIS
  Opens a small dialog ON THE PC'S SCREEN where you type the curtain's unlock PIN.

.DESCRIPTION
  The PIN is typed by you into that dialog and nowhere else. It is never passed on a command line,
  never shown to Claude and never written to disk. Only a salted hash is stored, in local\pin.hash,
  which git ignores.

  Run this while sitting at the PC. The curtain refuses to start until a PIN has been set.

.PARAMETER Wait
  Wait for the dialog to close and report whether a PIN was saved.
#>
[CmdletBinding()]
param(
    [switch]$Wait,
    # For test-curtain.ps1 only.
    [string]$LocalDir
)

$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'curtain-common.ps1')
if (-not $LocalDir) { $LocalDir = $script:CurtainLocal }
$LocalDir = $LocalDir.TrimEnd('\')

if (Test-CurtainRunning) {
    Write-Host 'The curtain is running. Turn it off first (curtain-off.ps1), then set the PIN.'
    exit 3
}

& (Join-Path $PSScriptRoot 'build.ps1')
New-Item -ItemType Directory -Force -Path $LocalDir | Out-Null
$proc = Start-Process -FilePath $script:CurtainExe -ArgumentList @('--set-pin', '--local-dir', "`"$LocalDir`"") -PassThru

if (-not $Wait) {
    Write-Host 'A "set unlock PIN" dialog is now open on the PC''s screen. Type the PIN there.'
    exit 0
}

$proc.WaitForExit()
if ($proc.ExitCode -eq 0) {
    Write-Host 'PIN saved.'
    exit 0
}
Write-Host 'No PIN was saved (the dialog was cancelled or closed).'
exit 2
