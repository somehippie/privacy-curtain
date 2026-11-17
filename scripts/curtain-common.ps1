# Shared helpers for curtain-on, curtain-launch, curtain-setpin, curtain-status and the tests.
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

# ------------------------------------------------------------------ Claude Code Remote Control (for the launcher)

function Get-ClaudeSession([string]$SessionsDir) {
    # Read-only. The Claude Code sessions running on this PC, from the list Claude Code keeps of them: one
    # small JSON file per session in ~\.claude\sessions (or under CLAUDE_CONFIG_DIR). RemoteControl is true
    # when Remote Control is on in the session, however it was turned on (claude --remote-control, or
    # /remote-control typed in it): the session then has a bridge session. A file left by a session that has
    # ended is skipped, and so is one whose process id now belongs to another process (the start times differ).
    if (-not $SessionsDir) {
        $config = if ($env:CLAUDE_CONFIG_DIR) { $env:CLAUDE_CONFIG_DIR } else { Join-Path $env:USERPROFILE '.claude' }
        $SessionsDir = Join-Path $config 'sessions'
    }
    $found = New-Object System.Collections.Generic.List[object]
    if (Test-Path -LiteralPath $SessionsDir) {
        foreach ($file in @(Get-ChildItem -LiteralPath $SessionsDir -Filter '*.json' -File -ErrorAction SilentlyContinue)) {
            $j = $null
            try { $j = [System.IO.File]::ReadAllText($file.FullName) | ConvertFrom-Json } catch { continue }
            if (-not $j -or -not $j.pid) { continue }
            $proc = Get-Process -Id ([int]$j.pid) -ErrorAction SilentlyContinue
            if (-not $proc) { continue }
            $started = $null
            try { $started = $proc.StartTime } catch { }
            $recorded = 0L
            if ($started -and $j.procStart -and [long]::TryParse([string]$j.procStart, [ref]$recorded) -and
                [Math]::Abs($started.ToFileTimeUtc() - $recorded) -gt 20000000) { continue }
            $found.Add([pscustomobject]@{
                Pid           = [int]$j.pid
                Name          = [string]$j.name
                Folder        = [string]$j.cwd
                Started       = $started
                RemoteControl = -not [string]::IsNullOrEmpty([string]$j.bridgeSessionId)
            })
        }
    }
    # A session started with claude --remote-control (or claude remote-control) counts whatever the list says,
    # in case another Claude Code version keeps the list differently.
    try {
        foreach ($p in @(Get-CimInstance Win32_Process -Filter "Name = 'claude.exe'" -ErrorAction Stop)) {
            $cmd = [string]$p.CommandLine
            if ($cmd -notmatch '(^|\s)(--)?remote-control(\s|=|$)') { continue }
            $known = @($found | Where-Object { $_.Pid -eq [int]$p.ProcessId })
            if ($known.Count -gt 0) { $known[0].RemoteControl = $true; continue }
            $name = ''
            if ($cmd -match '--remote-control[\s=]+"?([^\s"-][^\s"]*)') { $name = $Matches[1] }
            $found.Add([pscustomobject]@{ Pid = [int]$p.ProcessId; Name = $name; Folder = ''; Started = $p.CreationDate; RemoteControl = $true })
        }
    } catch { }
    $found.ToArray()
}

function Format-ClaudeSession($Session) {
    # "my-project (since 11:27 AM)": the folder it runs in (or its name), and when it started.
    $where = if ($Session.Folder -and $Session.Folder.TrimEnd('\') -ieq $env:USERPROFILE.TrimEnd('\')) { 'your user folder' }
             elseif ($Session.Folder) { Split-Path -Leaf $Session.Folder.TrimEnd('\') }
             elseif ($Session.Name) { $Session.Name }
             else { "process $($Session.Pid)" }
    if (-not $Session.Started) { return $where }
    $t = $Session.Started.ToString('t')
    if ($Session.Started.Date -ne (Get-Date).Date) { $t = $Session.Started.ToString('ddd') + ' ' + $t }
    "$where (since $t)"
}

function Get-RemoteControlAdvice($Sessions, [bool]$ClaudeFound) {
    # What the launcher's "Open Claude Code with Remote Control" switch starts as, and the line under it. On
    # unless a Claude Code session here already has Remote Control on.
    $on = @($Sessions | Where-Object { $_.RemoteControl })
    $off = @($Sessions | Where-Object { -not $_.RemoteControl })
    $list = {
        param($s)
        $names = @($s | ForEach-Object { Format-ClaudeSession $_ })
        if ($names.Count -le 1) { return ($names -join '') }
        ($names[0..($names.Count - 2)] -join ', ') + ' and ' + $names[-1]
    }
    if ($on.Count -gt 0) {
        return [pscustomobject]@{ Check = $false; Enabled = $ClaudeFound
                                  Note = "Already on in Claude Code here: $(& $list $on). No new window needed." }
    }
    if (-not $ClaudeFound) {
        return [pscustomobject]@{ Check = $false; Enabled = $false
                                  Note = "Claude Code isn't installed here, so there's no Remote Control to start." }
    }
    if ($off.Count -gt 0) {
        return [pscustomobject]@{ Check = $true; Enabled = $true
                                  Note = "Claude Code is open here, in $(& $list $off), but without Remote Control. You can type /remote-control in it and turn the switch off instead." }
    }
    [pscustomobject]@{ Check = $true; Enabled = $true; Note = 'No Claude Code session on this PC has Remote Control on.' }
}
