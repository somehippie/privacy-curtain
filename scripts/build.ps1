<#
.SYNOPSIS
  Compiles the curtain with the C# compiler that ships inside Windows (.NET Framework). Nothing is installed.

.PARAMETER Tests
  Also build bin\CurtainTestHelper.exe, used only by test-curtain.ps1.

.PARAMETER Force
  Rebuild even when the exe is newer than the sources.
#>
[CmdletBinding()]
param(
    [switch]$Tests,
    [switch]$Force
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$bin = Join-Path $root 'bin'
$srcDir = Join-Path $root 'src'

$csc = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (-not (Test-Path $csc)) { $csc = Join-Path $env:WINDIR 'Microsoft.NET\Framework\v4.0.30319\csc.exe' }
if (-not (Test-Path $csc)) { throw 'csc.exe (.NET Framework 4) was not found under the Windows folder.' }

New-Item -ItemType Directory -Force -Path $bin | Out-Null

function Test-UpToDate([string]$Target, [object[]]$Inputs) {
    if (-not (Test-Path $Target)) { return $false }
    $built = (Get-Item $Target).LastWriteTimeUtc
    foreach ($f in $Inputs) { if ($f.LastWriteTimeUtc -gt $built) { return $false } }
    return $true
}

function Invoke-Csc([string]$Target, [string[]]$CscArgs) {
    # Compile to a temporary name first so a failed build never leaves a half-written exe behind.
    $tmp = "$Target.new"
    if (Test-Path $tmp) { Remove-Item $tmp -Force }
    # csc wants /out before the source files, and reports its errors on stdout.
    $output = & $csc "/out:$tmp" @CscArgs | Out-String
    if ($LASTEXITCODE -ne 0 -or -not (Test-Path $tmp)) {
        if (Test-Path $tmp) { Remove-Item $tmp -Force }
        throw "Compile failed for $(Split-Path -Leaf $Target):`n$output"
    }
    if ($output.Trim()) { Write-Host $output.Trim() }
    try {
        Move-Item -Path $tmp -Destination $Target -Force
    } catch {
        Remove-Item $tmp -Force -ErrorAction SilentlyContinue
        throw "Could not replace $(Split-Path -Leaf $Target). Is the curtain running? Turn it off first (curtain-off.ps1)."
    }
}

$common = @('/nologo', '/optimize+', '/warn:4', '/platform:anycpu',
            '/r:System.dll', '/r:System.Core.dll', '/r:System.Drawing.dll', '/r:System.Windows.Forms.dll')

# ---- the curtain ----
$exe = Join-Path $bin 'ClaudeCurtain.exe'
$manifest = Join-Path $srcDir 'app.manifest'
$sources = @(Get-ChildItem $srcDir -Filter *.cs)
$inputs = $sources + (Get-Item $manifest) + (Get-Item $PSCommandPath)
if ($Force -or -not (Test-UpToDate $exe $inputs)) {
    Invoke-Csc $exe ($common + @('/target:winexe', "/win32manifest:$manifest") + $sources.FullName)
    Write-Host "Built $exe"
} else {
    Write-Verbose 'ClaudeCurtain.exe is up to date.'
}

# ---- the test helper ----
if ($Tests) {
    $helper = Join-Path $bin 'CurtainTestHelper.exe'
    $testSources = @(Get-ChildItem (Join-Path $root 'tests') -Filter *.cs) + (Get-Item (Join-Path $srcDir 'PinStore.cs'))
    if ($Force -or -not (Test-UpToDate $helper ($testSources + (Get-Item $PSCommandPath)))) {
        Invoke-Csc $helper ($common + @('/target:exe') + $testSources.FullName)
        Write-Host "Built $helper"
    } else {
        Write-Verbose 'CurtainTestHelper.exe is up to date.'
    }
}
