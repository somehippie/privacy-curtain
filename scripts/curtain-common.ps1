# Shared helpers for curtain-on, curtain-setpin, curtain-status and the tests.
#
# curtain-off.ps1 deliberately does NOT load this file. The remote unlock has to keep working
# even if something in here is broken.

$script:CurtainRoot  = Split-Path -Parent $PSScriptRoot
$script:CurtainExe   = Join-Path $script:CurtainRoot 'bin\ClaudeCurtain.exe'
$script:CurtainLocal = Join-Path $script:CurtainRoot 'local'

function Get-CurtainProcess {
    # Every running curtain process (the curtain and its watchdog).
    @(Get-Process -Name 'ClaudeCurtain' -ErrorAction SilentlyContinue | Where-Object {
        $path = $null
        try { $path = $_.Path } catch { }
        (-not $path) -or ($path -ieq $script:CurtainExe)
    })
}

function Test-CurtainRunning {
    # True only while a real curtain is up. (The short "While you were away" window uses the same exe
    # but does not count.)
    $m = $null
    try {
        if ([System.Threading.Mutex]::TryOpenExisting('Local\ClaudeCurtain.Instance', [ref]$m)) { $m.Dispose(); return $true }
    } catch { }
    return $false
}

function Read-CurtainJson([string]$File) {
    if (-not (Test-Path $File)) { return $null }
    try { Get-Content $File -Raw -ErrorAction Stop | ConvertFrom-Json } catch { $null }
}

function Read-CurtainState([string]$LocalDir = $script:CurtainLocal) {
    # The state file of a curtain that is really running, or $null. A file left by a killed curtain is ignored.
    $state = Read-CurtainJson (Join-Path $LocalDir 'state.json')
    if (-not $state) { return $null }
    $proc = Get-Process -Id $state.pid -ErrorAction SilentlyContinue
    if (-not $proc -or $proc.ProcessName -ne 'ClaudeCurtain') { return $null }
    $state
}

function Get-PowerButtonAction {
    # Read-only. What the physical power button does right now, or $null if it cannot be read.
    try {
        $out = & "$env:WINDIR\System32\powercfg.exe" /qh SCHEME_CURRENT SUB_BUTTONS PBUTTONACTION
        $line = $out | Where-Object { $_ -match 'Current AC Power Setting Index:\s*0x([0-9a-fA-F]+)' } | Select-Object -First 1
        if (-not $line) { return $null }
        [void]($line -match '0x([0-9a-fA-F]+)')
        switch ([Convert]::ToInt32($Matches[1], 16)) {
            0 { 'Do nothing' }
            1 { 'Sleep' }
            2 { 'Hibernate' }
            3 { 'Shut down' }
            4 { 'Turn off the display' }
            default { $null }
        }
    } catch { $null }
}

function Format-CurtainSpan([double]$Seconds) {
    $s = [int][math]::Round($Seconds)
    if ($s -lt 0) { $s = 0 }
    if ($s -ge 3600) { return ('{0} h {1} min' -f [int][math]::Floor($s / 3600), [int][math]::Floor(($s % 3600) / 60)) }
    if ($s -ge 60) { return ('{0} min {1} s' -f [int][math]::Floor($s / 60), ($s % 60)) }
    return "$s s"
}

function Get-CurtainExitMeaning([int]$Code) {
    switch ($Code) {
        2  { 'bad arguments' }
        3  { 'a curtain is already running' }
        4  { 'no usable PIN is set. Run curtain-setpin.ps1 at the PC first' }
        5  { 'the windows could not be hidden from screen capture (needs Windows 10 version 2004 or later)' }
        6  { 'the keyboard/mouse hooks could not be installed' }
        7  { 'the watchdog process could not be started' }
        default { "exit code $Code" }
    }
}

function Get-CurtainLogTail([int]$Lines = 8, [string]$LocalDir = $script:CurtainLocal) {
    $log = Join-Path $LocalDir 'curtain.log'
    if (Test-Path $log) { Get-Content $log -Tail $Lines }
}
