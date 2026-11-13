<#
.SYNOPSIS
  Automated tests for the curtain. Uses a throwaway PIN in its own folder (local\test) and never
  touches your real PIN. Every curtain it starts has a short timer and is closed again afterwards.

.DESCRIPTION
  Lifecycle stages work even while the PC is locked:
    selftest smoke off kill watchdog-lost session-end close pin second-instance no-pin freeze two-minute

  Desktop stages need the PC unlocked, because they take screenshots and send clicks and keys:
    capture inject pin-injected setpin

  What no script can test is real fingers on the real keyboard. That is the checklist in the README.

.PARAMETER Stage
  Stage names to run. Default: all lifecycle stages.

.PARAMETER Desktop
  Run the desktop stages instead.

.EXAMPLE
  .\test-curtain.ps1
.EXAMPLE
  .\test-curtain.ps1 -Desktop
.EXAMPLE
  .\test-curtain.ps1 -Stage smoke,off
#>
[CmdletBinding()]
param(
    [string[]]$Stage,
    [switch]$Desktop
)

$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'curtain-common.ps1')

$lifecycle = @('selftest', 'smoke', 'off', 'kill', 'watchdog-lost', 'session-end', 'close', 'pin',
               'second-instance', 'no-pin', 'freeze', 'keep-awake', 'events', 'alerts', 'two-minute')
$desktopStages = @('capture', 'inject', 'pin-injected', 'setpin')
if (-not $Stage) { if ($Desktop) { $Stage = $desktopStages } else { $Stage = $lifecycle } }

$on = Join-Path $PSScriptRoot 'curtain-on.ps1'
$off = Join-Path $PSScriptRoot 'curtain-off.ps1'
$helper = Join-Path $script:CurtainRoot 'bin\CurtainTestHelper.exe'
$testLocal = Join-Path $script:CurtainLocal 'test'
$results = New-Object System.Collections.Generic.List[object]

# ------------------------------------------------------------------ helpers

function Add-Result([string]$StageName, [string]$Name, [bool]$Ok, [string]$Detail = '') {
    $results.Add([pscustomobject]@{ Stage = $StageName; Name = $Name; Ok = $Ok; Detail = $Detail })
    $mark = 'FAIL'
    if ($Ok) { $mark = 'PASS' }
    $tail = ''
    if ($Detail) { $tail = "  ($Detail)" }
    Write-Host ("{0}  [{1}] {2}{3}" -f $mark, $StageName, $Name, $tail)
}

function Add-Note([string]$StageName, [string]$Text) {
    Write-Host ("NOTE  [{0}] {1}" -f $StageName, $Text)
}

function Stop-AnyCurtain {
    if ((Get-CurtainProcess).Count -gt 0) { & $off -Force | Out-Null }
}

function Start-TestCurtain([int]$Seconds, [string[]]$Extra = @(), [switch]$ShowOutput) {
    Stop-AnyCurtain
    Remove-Item (Join-Path $testLocal 'last-exit.json') -Force -ErrorAction SilentlyContinue
    if ($ShowOutput) {
        & $on -Minutes ($Seconds / 60.0) -LocalDir $testLocal -ExtraArgs $Extra
    } else {
        & $on -Minutes ($Seconds / 60.0) -LocalDir $testLocal -ExtraArgs $Extra 6>$null | Out-Null
    }
    if ($LASTEXITCODE -ne 0) { return $null }
    Read-CurtainState $testLocal
}

function Wait-PidGone([int]$ProcessId, [double]$TimeoutSeconds) {
    $sw = [Diagnostics.Stopwatch]::StartNew()
    while ($sw.Elapsed.TotalSeconds -lt $TimeoutSeconds) {
        if (-not (Get-Process -Id $ProcessId -ErrorAction SilentlyContinue)) { return $sw.Elapsed.TotalSeconds }
        Start-Sleep -Milliseconds 100
    }
    return -1
}

function Wait-CurtainGone([double]$TimeoutSeconds) {
    $sw = [Diagnostics.Stopwatch]::StartNew()
    while ($sw.Elapsed.TotalSeconds -lt $TimeoutSeconds) {
        if ((Get-CurtainProcess).Count -eq 0) { return $sw.Elapsed.TotalSeconds }
        Start-Sleep -Milliseconds 100
    }
    return -1
}

function Get-LastExit { Read-CurtainJson (Join-Path $testLocal 'last-exit.json') }

function Get-Windows([int]$ProcessId, [switch]$Children) {
    if ($Children) { $json = & $helper inspect $ProcessId --children } else { $json = & $helper inspect $ProcessId }
    $parsed = $json | ConvertFrom-Json
    @($parsed | ForEach-Object { $_ })
}

function Get-Json([string]$File) {
    for ($i = 0; $i -lt 20; $i++) {
        $j = Read-CurtainJson $File
        if ($j) { return $j }
        Start-Sleep -Milliseconds 100
    }
    $null
}

function Test-CurtainWindows([string]$s, $state) {
    $curtains = @(Get-Windows $state.pid | Where-Object { $_.title -eq 'Claude privacy curtain' })
    Add-Result $s 'one curtain window per monitor' ($curtains.Count -eq $state.monitors) "$($curtains.Count) window(s), $($state.monitors) monitor(s)"

    $expected = @([regex]::Matches($state.layout, '\d+x\d+\+-?\d+\+-?\d+') | ForEach-Object { $_.Value })
    # layered, click-through, topmost, never activates, hidden from Alt+Tab
    $need = 0x80000 -bor 0x20 -bor 0x8 -bor 0x08000000 -bor 0x80
    $styleOk = $true; $visibleOk = $true; $affinityOk = $true; $alphaOk = $true; $rectOk = $true
    foreach ($w in $curtains) {
        if (($w.exstyle -band $need) -ne $need) { $styleOk = $false }
        if (-not $w.visible -or $w.iconic) { $visibleOk = $false }
        if ($w.affinity -ne 17) { $affinityOk = $false }
        if ($w.alpha -ne 255) { $alphaOk = $false }
        if ($expected -notcontains ("{0}x{1}+{2}+{3}" -f $w.w, $w.h, $w.x, $w.y)) { $rectOk = $false }
    }
    $has = $curtains.Count -gt 0
    Add-Result $s 'each window covers its whole monitor' ($has -and $rectOk) $state.layout
    Add-Result $s 'windows are visible and fully opaque' ($has -and $visibleOk -and $alphaOk)
    Add-Result $s 'windows are click-through, topmost and never take focus' ($has -and $styleOk)
    Add-Result $s 'windows are excluded from screen capture (WDA_EXCLUDEFROMCAPTURE)' ($has -and $affinityOk)

    Add-Type -AssemblyName System.Windows.Forms
    $screens = @([System.Windows.Forms.Screen]::AllScreens | ForEach-Object { "{0}x{1}+{2}+{3}" -f $_.Bounds.Width, $_.Bounds.Height, $_.Bounds.X, $_.Bounds.Y })
    $same = (@(Compare-Object $screens $expected).Count -eq 0)
    Add-Result $s 'covered area matches what Windows reports for the monitors' $same ($screens -join ', ')
}

function Test-ExitReason([string]$s, [string]$Expected) {
    $last = Get-Json (Join-Path $testLocal 'last-exit.json')
    $reason = ''
    if ($last) { $reason = [string]$last.reason }
    Add-Result $s "recorded reason is `"$Expected`"" ($reason -like $Expected) $reason
}

function Test-Unlocked {
    ((& $helper locked) -join '').Trim() -eq 'unlocked'
}

# ------------------------------------------------------------------ lifecycle stages

function Invoke-SelfTest {
    $s = 'selftest'
    $out = Join-Path $testLocal 'selftest.txt'
    $p = Start-Process -FilePath $script:CurtainExe -ArgumentList @('--selftest', '--local-dir', "`"$testLocal`"", '--out', "`"$out`"") -PassThru -Wait
    $lines = @(Get-Content $out)
    $fails = @($lines | Where-Object { $_ -like 'FAIL*' })
    $passes = @($lines | Where-Object { $_ -like 'PASS*' })
    Add-Result $s 'logic self-tests (PIN hashing, PIN entry rules, key queue)' ($p.ExitCode -eq 0 -and $fails.Count -eq 0) "$($passes.Count) passed, $($fails.Count) failed"
    $fails | ForEach-Object { Write-Host "      $_" }
}

function Invoke-Smoke {
    $s = 'smoke'
    $state = Start-TestCurtain 15
    Add-Result $s 'curtain starts and reports ready' ([bool]$state)
    if (-not $state) { return }
    $procs = Get-CurtainProcess
    Add-Result $s 'the curtain and its watchdog are both running' ($procs.Count -eq 2) "$($procs.Count) process(es)"
    Test-CurtainWindows $s $state

    $deadline = ([datetime]$state.deadlineUtc).ToUniversalTime()
    $gone = Wait-PidGone $state.pid 25
    $late = ((Get-Date).ToUniversalTime() - $deadline).TotalSeconds
    Add-Result $s 'closes by itself when the 15 s timer runs out' ($gone -ge 0 -and [math]::Abs($late) -le 2.5) ("{0:n1} s from the deadline" -f $late)
    Test-ExitReason $s 'timer'
    Add-Result $s 'the watchdog leaves once the curtain is gone' ((Wait-CurtainGone 5) -ge 0)
    Add-Result $s 'the "running" state file is removed' (-not (Test-Path (Join-Path $testLocal 'state.json')))
}

function Invoke-Off {
    $s = 'off'
    $state = Start-TestCurtain 60
    if (-not $state) { Add-Result $s 'curtain starts' $false; return }
    $sw = [Diagnostics.Stopwatch]::StartNew()
    $text = (& $off) -join ' '
    $code = $LASTEXITCODE
    $took = $sw.Elapsed.TotalSeconds
    Add-Result $s 'curtain-off.ps1 closes the curtain' ($code -eq 0 -and (Get-CurtainProcess).Count -eq 0) ("{0:n1} s, said: {1}" -f $took, $text)
    Add-Result $s 'it closed politely, without needing a kill' ($text -like '*closed on request*')
    Test-ExitReason $s 'remote-stop'

    $text = (& $off) -join ' '
    Add-Result $s 'curtain-off.ps1 is harmless when nothing is running' ($LASTEXITCODE -eq 0 -and $text -like '*not running*') $text

    $state = Start-TestCurtain 60
    if (-not $state) { Add-Result $s 'curtain starts again' $false; return }
    $text = (& $off -Force) -join ' '
    Add-Result $s 'curtain-off.ps1 -Force kills it outright' ($LASTEXITCODE -eq 0 -and (Get-CurtainProcess).Count -eq 0) $text
}

function Invoke-Kill {
    $s = 'kill'
    $state = Start-TestCurtain 60
    if (-not $state) { Add-Result $s 'curtain starts' $false; return }
    Stop-Process -Id $state.pid -Force
    $gone = Wait-CurtainGone 6
    Add-Result $s 'killing the curtain process leaves nothing behind (watchdog exits too)' ($gone -ge 0) ("{0:n1} s" -f $gone)
    $again = Start-TestCurtain 20
    Add-Result $s 'a new curtain can start straight after a kill' ([bool]$again)
    Stop-AnyCurtain
}

function Invoke-WatchdogLost {
    $s = 'watchdog-lost'
    $state = Start-TestCurtain 60
    if (-not $state) { Add-Result $s 'curtain starts' $false; return }
    Stop-Process -Id $state.watchdogPid -Force
    $gone = Wait-PidGone $state.pid 6
    Add-Result $s 'killing the watchdog makes the curtain close itself' ($gone -ge 0) ("{0:n1} s" -f $gone)
    Test-ExitReason $s 'watchdog-lost'
    Stop-AnyCurtain
}

function Invoke-SessionEnd {
    $s = 'session-end'
    foreach ($variant in 'the curtain window', 'the independent shutdown listener') {
        $state = Start-TestCurtain 60
        if (-not $state) { Add-Result $s "curtain starts ($variant)" $false; continue }
        $wins = Get-Windows $state.pid
        if ($variant -eq 'the curtain window') {
            $target = $wins | Where-Object { $_.title -eq 'Claude privacy curtain' } | Select-Object -First 1
        } else {
            $target = $wins | Where-Object { $_.class -like '.NET-BroadcastEventWindow*' } | Select-Object -First 1
        }
        if (-not $target) { Add-Result $s "found $variant" $false; Stop-AnyCurtain; continue }
        # 17 = WM_QUERYENDSESSION, the question Windows asks every app before shutting down or signing out.
        & $helper sendmsg $target.hwnd 17 0 0 3000 | Out-Null
        $gone = Wait-PidGone $state.pid 6
        Add-Result $s "a shutdown request to $variant closes the curtain at once" ($gone -ge 0 -and $gone -le 2) ("{0:n2} s" -f $gone)
        Test-ExitReason $s 'windows-session-ending'
        Stop-AnyCurtain
    }
}

function Invoke-Close {
    $s = 'close'
    $state = Start-TestCurtain 60
    if (-not $state) { Add-Result $s 'curtain starts' $false; return }
    $target = Get-Windows $state.pid | Where-Object { $_.title -eq 'Claude privacy curtain' } | Select-Object -First 1
    & $helper sendmsg $target.hwnd 16 0 0 3000 | Out-Null    # 16 = WM_CLOSE, what "taskkill" without /F sends
    $gone = Wait-PidGone $state.pid 6
    Add-Result $s 'a plain close request (taskkill without /F) closes the curtain' ($gone -ge 0 -and $gone -le 2) ("{0:n2} s" -f $gone)
    Test-ExitReason $s 'window-closed'
    Stop-AnyCurtain
}

function Get-KeyScript([string]$Pin) {
    # virtual-key codes for the digits, then 13 = Enter
    (@($Pin.ToCharArray() | ForEach-Object { 48 + [int][string]$_ }) + 13) -join ','
}

function Invoke-Pin {
    $s = 'pin'
    $wrong = '00000000'
    if ($script:TestPin -eq $wrong) { $wrong = '11111111' }

    $state = Start-TestCurtain 40 @('--test-keys', "$(Get-KeyScript $wrong)@2")
    if (-not $state) { Add-Result $s 'curtain starts' $false; return }
    Start-Sleep -Seconds 6
    Add-Result $s 'a wrong PIN does not close the curtain' ([bool](Get-Process -Id $state.pid -ErrorAction SilentlyContinue))
    & $off | Out-Null
    $last = Get-Json (Join-Path $testLocal 'last-exit.json')
    Add-Result $s 'the wrong attempt was counted' ($last -and $last.wrongPinAttempts -eq 1) "wrongPinAttempts=$($last.wrongPinAttempts)"

    $state = Start-TestCurtain 40 @('--test-keys', "$(Get-KeyScript $script:TestPin)@2")
    if (-not $state) { Add-Result $s 'curtain starts again' $false; return }
    $gone = Wait-PidGone $state.pid 10
    Add-Result $s 'the right PIN followed by Enter closes the curtain' ($gone -ge 0) ("{0:n1} s after start" -f $gone)
    Test-ExitReason $s 'pin'
    Start-Sleep -Milliseconds 1200
    $view = @(Get-CurtainProcess | ForEach-Object { Get-Windows $_.Id } | Where-Object { $_.title -eq 'While you were away' -and $_.visible })
    Add-Result $s 'a "While you were away" window appears after a PIN unlock' ($view.Count -eq 1)
    Add-Result $s 'the PC is not blocked while it shows (no curtain is running)' (-not (Test-CurtainRunning))
    $closed = Wait-CurtainGone 14
    Add-Result $s 'the summary window closes by itself after about 10 s' ($closed -ge 5 -and $closed -le 12) ("{0:n1} s more" -f $closed)
    Stop-AnyCurtain

    $logText = Get-Content (Join-Path $testLocal 'curtain.log') -Raw
    Add-Result $s 'the log never contains the PIN' ($logText -notmatch [regex]::Escape($script:TestPin))
}

function Invoke-SecondInstance {
    $s = 'second-instance'
    $state = Start-TestCurtain 40
    if (-not $state) { Add-Result $s 'curtain starts' $false; return }
    $p = Start-Process -FilePath $script:CurtainExe -ArgumentList @('--seconds', '40', '--local-dir', "`"$testLocal`"") -PassThru -Wait
    Add-Result $s 'a second curtain refuses to start' ($p.ExitCode -eq 3) "exit code $($p.ExitCode)"
    Add-Result $s 'the first curtain is untouched' ((Get-CurtainProcess).Count -eq 2 -and [bool](Get-Process -Id $state.pid -ErrorAction SilentlyContinue))
    $text = (& $on -Minutes 1 -LocalDir $testLocal 6>&1 | Out-String)
    Add-Result $s 'curtain-on.ps1 says it is already on' ($LASTEXITCODE -eq 3 -and $text -like '*already on*')
    Stop-AnyCurtain
}

function Invoke-NoPin {
    $s = 'no-pin'
    Stop-AnyCurtain
    $missing = Join-Path $testLocal 'no-such-pin.hash'
    $p = Start-Process -FilePath $script:CurtainExe -ArgumentList @('--seconds', '30', '--local-dir', "`"$testLocal`"", '--pin-file', "`"$missing`"") -PassThru -Wait
    Add-Result $s 'the curtain refuses to start without a PIN file' ($p.ExitCode -eq 4 -and (Get-CurtainProcess).Count -eq 0) "exit code $($p.ExitCode)"

    $bad = Join-Path $testLocal 'bad-pin.hash'
    Set-Content -Path $bad -Value 'this is not a pin file' -Encoding ascii
    $p = Start-Process -FilePath $script:CurtainExe -ArgumentList @('--seconds', '30', '--local-dir', "`"$testLocal`"", '--pin-file', "`"$bad`"") -PassThru -Wait
    Add-Result $s 'the curtain refuses to start with a damaged PIN file' ($p.ExitCode -eq 4 -and (Get-CurtainProcess).Count -eq 0) "exit code $($p.ExitCode)"
    Remove-Item $bad -Force

    $empty = Join-Path $testLocal 'empty'
    New-Item -ItemType Directory -Force -Path $empty | Out-Null
    $text = (& $on -Minutes 1 -LocalDir $empty 6>&1 | Out-String)
    Add-Result $s 'curtain-on.ps1 explains that a PIN must be set first' ($LASTEXITCODE -eq 4 -and $text -like '*curtain-setpin*' -and (Get-CurtainProcess).Count -eq 0)

    $p = Start-Process -FilePath $script:CurtainExe -ArgumentList @('--seconds', '999999', '--local-dir', "`"$testLocal`"") -PassThru -Wait
    Add-Result $s 'a timer longer than 24 hours is refused' ($p.ExitCode -eq 2 -and (Get-CurtainProcess).Count -eq 0) "exit code $($p.ExitCode)"
}

function Invoke-Freeze {
    $s = 'freeze'

    $state = Start-TestCurtain 60 @('--test-freeze', 'ui@3')
    if ($state) {
        $gone = Wait-PidGone $state.pid 25
        Add-Result $s 'a hung window thread makes the curtain close itself' ($gone -ge 0) ("{0:n1} s after start" -f $gone)
        Test-ExitReason $s 'ui-thread-hung'
    } else { Add-Result $s 'curtain starts (ui freeze)' $false }
    Stop-AnyCurtain

    $state = Start-TestCurtain 60 @('--test-freeze', 'hook@3')
    if ($state) {
        $gone = Wait-PidGone $state.pid 25
        Add-Result $s 'a hung input-hook thread makes the curtain close itself' ($gone -ge 0) ("{0:n1} s after start" -f $gone)
        Test-ExitReason $s 'hook-thread-hung'
    } else { Add-Result $s 'curtain starts (hook freeze)' $false }
    Stop-AnyCurtain

    $state = Start-TestCurtain 60 @('--test-freeze', 'control@3')
    if ($state) {
        $gone = Wait-PidGone $state.pid 40
        Add-Result $s 'a hung control thread gets the curtain killed by the watchdog' ($gone -ge 0) ("{0:n1} s after start" -f $gone)
        Test-ExitReason $s 'watchdog-kill:heartbeat-lost'
    } else { Add-Result $s 'curtain starts (control freeze)' $false }
    Stop-AnyCurtain

    # The curtain's own timer is frozen here, so only the watchdog's independent deadline can end it.
    $state = Start-TestCurtain 10 @('--test-freeze', 'control@1')
    if ($state) {
        $deadline = ([datetime]$state.deadlineUtc).ToUniversalTime()
        $gone = Wait-PidGone $state.pid 40
        $late = ((Get-Date).ToUniversalTime() - $deadline).TotalSeconds
        Add-Result $s 'if the timer itself fails, the watchdog ends the curtain shortly after the deadline' ($gone -ge 0 -and $late -le 14) ("{0:n1} s after the deadline" -f $late)
        Test-ExitReason $s 'watchdog-kill:*'
    } else { Add-Result $s 'curtain starts (timer freeze)' $false }
    Stop-AnyCurtain
}

function Invoke-TwoMinute {
    $s = 'two-minute'
    $state = Start-TestCurtain 120 -ShowOutput
    Add-Result $s 'curtain starts with a 2-minute timer' ([bool]$state)
    if (-not $state) { return }
    Test-CurtainWindows $s $state
    $deadline = ([datetime]$state.deadlineUtc).ToUniversalTime()

    Start-Sleep -Seconds 60
    $alive = [bool](Get-Process -Id $state.pid -ErrorAction SilentlyContinue)
    Add-Result $s 'still up after one minute' $alive
    if ($alive) {
        $curtains = @(Get-Windows $state.pid | Where-Object { $_.title -eq 'Claude privacy curtain' })
        $still = ($curtains.Count -eq $state.monitors) -and (@($curtains | Where-Object { $_.affinity -ne 17 -or -not $_.visible -or (($_.exstyle -band 0x8) -eq 0) }).Count -eq 0)
        Add-Result $s 'windows are still topmost, visible and capture-excluded after one minute' $still
    }

    $gone = Wait-PidGone $state.pid 75
    $late = ((Get-Date).ToUniversalTime() - $deadline).TotalSeconds
    Add-Result $s 'closes by itself when the 2-minute timer runs out' ($gone -ge 0 -and [math]::Abs($late) -le 2.5) ("{0:n1} s from the deadline" -f $late)
    Test-ExitReason $s 'timer'
    Add-Result $s 'nothing is left running' ((Wait-CurtainGone 5) -ge 0)
    $beats = @(Get-RunEvents $state | Where-Object { $_.Kind -eq 'heartbeat' -or $_.Kind -eq 'input' }).Count
    Add-Result $s 'the event log got a once-a-minute heartbeat, and no more than that' ($beats -ge 1 -and $beats -le 3) "$beats line(s) in 2 minutes"
}

function Get-Events {
    $file = Join-Path $testLocal 'events.log'
    if (-not (Test-Path $file)) { return @() }
    @(Get-Content $file | ForEach-Object {
        $f = $_ -split "`t"
        if ($f.Count -ge 4) { [pscustomobject]@{ Time = $f[0]; Run = $f[1]; Kind = $f[2]; Text = $f[3] } }
    })
}

function Get-RunEvents($state) { @(Get-Events | Where-Object { $_.Run -eq "$($state.pid)" }) }

function Invoke-Events {
    $s = 'events'
    $eventsFile = Join-Path $testLocal 'events.log'
    $wrong = '00000000'
    if ($script:TestPin -eq $wrong) { $wrong = '11111111' }
    Stop-AnyCurtain
    Start-Sleep -Milliseconds 600
    Remove-Item $eventsFile -Force -ErrorAction SilentlyContinue

    # --- start, wrong PIN, curtain-off, summary
    $state = Start-TestCurtain 40 @('--test-keys', "$(Get-KeyScript $wrong)@2")
    if (-not $state) { Add-Result $s 'curtain starts' $false; return }
    Start-Sleep -Seconds 5
    $text = (& $off -LocalDir $testLocal) -join "`n"
    $ev = Get-RunEvents $state
    Add-Result $s 'the start is logged with a timestamp' (@($ev | Where-Object { $_.Kind -eq 'start' -and $_.Time -match '^\d{4}-\d\d-\d\d \d\d:\d\d:\d\d$' }).Count -eq 1)
    Add-Result $s 'a wrong PIN attempt is logged as a count' (@($ev | Where-Object { $_.Kind -eq 'wrong-pin' -and $_.Text -like '*attempt 1*' }).Count -eq 1)
    Add-Result $s 'the stop is logged, saying curtain-off closed it' (@($ev | Where-Object { $_.Kind -eq 'stop' -and $_.Text -eq 'curtain-off' }).Count -eq 1)
    Add-Result $s 'curtain-off.ps1 prints the "While you were away" summary' ($text -like '*While you were away*' -and $text -like '*Closed by: curtain-off*')
    Add-Result $s 'the summary counts the wrong PIN attempt' ($text -like '*Wrong PIN attempts: 1*')
    $raw = Get-Content $eventsFile -Raw
    Add-Result $s 'the event log holds neither the wrong digits nor the real PIN' ($raw -notmatch [regex]::Escape($wrong) -and $raw -notmatch [regex]::Escape($script:TestPin))
    $shown = (& (Join-Path $PSScriptRoot 'curtain-log.ps1') -LocalDir $testLocal -Count 10 6>&1 | Out-String)
    Add-Result $s 'curtain-log.ps1 shows the recent events' ($shown -like '*wrong-pin*' -and $shown -like '*stop*curtain-off*')

    # --- a quiet run says so
    $state = Start-TestCurtain 40
    Start-Sleep -Seconds 2
    $text = (& $off -LocalDir $testLocal) -join "`n"
    Add-Result $s 'a run where nothing happened says so in the summary' ($text -like '*Nobody touched the PC*')

    # --- timer
    $state = Start-TestCurtain 10
    [void](Wait-PidGone $state.pid 20)
    Add-Result $s 'a timer close is logged as "timer"' (@(Get-RunEvents $state | Where-Object { $_.Kind -eq 'stop' -and $_.Text -eq 'timer' }).Count -eq 1)
    [void](Wait-CurtainGone 5)

    # --- shutdown and sign-out requests
    foreach ($case in @(@{ L = '0'; Kind = 'shutdown'; How = 'Windows shutdown or restart' }, @{ L = '2147483648'; Kind = 'signout'; How = 'Windows sign-out' })) {
        $state = Start-TestCurtain 40
        $target = Get-Windows $state.pid | Where-Object { $_.title -eq 'Claude privacy curtain' } | Select-Object -First 1
        & $helper sendmsg $target.hwnd 17 0 $case.L 3000 | Out-Null
        [void](Wait-PidGone $state.pid 6)
        $ev = Get-RunEvents $state
        $ok = (@($ev | Where-Object { $_.Kind -eq $case.Kind }).Count -eq 1) -and (@($ev | Where-Object { $_.Kind -eq 'stop' -and $_.Text -eq $case.How }).Count -eq 1)
        Add-Result $s "a $($case.Kind) request is logged, and the stop says `"$($case.How)`"" $ok
        [void](Wait-CurtainGone 5)
    }

    # --- the curtain process being killed (its watchdog survives to write it down)
    $state = Start-TestCurtain 40
    Stop-Process -Id $state.pid -Force
    [void](Wait-CurtainGone 6)
    Start-Sleep -Milliseconds 300
    Add-Result $s 'killing the curtain process is logged' (@(Get-RunEvents $state | Where-Object { $_.Kind -eq 'killed' }).Count -eq 1)

    # --- watchdog kills are logged as a stop
    $state = Start-TestCurtain 60 @('--test-freeze', 'control@2')
    [void](Wait-PidGone $state.pid 40)
    [void](Wait-CurtainGone 5)
    Add-Result $s 'a watchdog kill is logged as the way it stopped' (@(Get-RunEvents $state | Where-Object { $_.Kind -eq 'stop' -and $_.Text -like 'watchdog*' }).Count -eq 1)

    # --- a fault is logged as a fault
    $state = Start-TestCurtain 60 @('--test-freeze', 'ui@2')
    [void](Wait-PidGone $state.pid 25)
    [void](Wait-CurtainGone 5)
    Add-Result $s 'a fault close is logged as a fault' (@(Get-RunEvents $state | Where-Object { $_.Kind -eq 'stop' -and $_.Text -like 'fault:*' }).Count -eq 1)

    # --- nothing survives to log it (like a power cut): the next start notices
    $state = Start-TestCurtain 40
    Get-CurtainProcess | ForEach-Object { Stop-Process -Id $_.Id -Force -ErrorAction SilentlyContinue }
    [void](Wait-CurtainGone 6)
    $killedLogged = @(Get-RunEvents $state | Where-Object { $_.Kind -eq 'killed' -or $_.Kind -eq 'stop' }).Count
    $next = Start-TestCurtain 40
    $unclean = @(Get-RunEvents $next | Where-Object { $_.Kind -eq 'unclean' })
    if ($killedLogged -eq 0) {
        Add-Result $s 'a curtain that vanished with no record is reported at the next start' ($unclean.Count -eq 1) "$($unclean.Text)"
    } else {
        Add-Note $s 'the watchdog managed to log the kill before it died, so the next-start check had nothing to report this time'
    }
    $text = (& $off -LocalDir $testLocal) -join "`n"
    if ($killedLogged -eq 0) { Add-Result $s 'and the summary mentions it' ($text -like '*earlier curtain*') }

    # --- 1 MB cap
    $filler = ('2026-01-01 00:00:00' + "`t1`theartbeat`t" + ('x' * 80) + "`r`n") * 13000
    [IO.File]::WriteAllText($eventsFile, $filler)
    $bigBefore = (Get-Item $eventsFile).Length
    $state = Start-TestCurtain 40
    & $off -LocalDir $testLocal | Out-Null
    $size = (Get-Item $eventsFile).Length
    Add-Result $s 'the event log is cut back when it passes 1 MB' ($bigBefore -gt 1000000 -and $size -lt 1000000) ("{0:n0} bytes before, {1:n0} after" -f $bigBefore, $size)
    Add-Result $s 'new events still land after the cut' (@(Get-RunEvents $state | Where-Object { $_.Kind -eq 'start' -or $_.Kind -eq 'stop' }).Count -eq 2)
    Remove-Item $eventsFile -Force -ErrorAction SilentlyContinue
}

function Invoke-Alerts {
    $s = 'alerts'
    Stop-AnyCurtain
    Start-Sleep -Milliseconds 400

    # A stub hook stands in for notify.ps1: it records its -Event and the stdin payload, so we can prove the
    # curtain actually reaches the hook with the right message, without firing a real toast.
    $stub = Join-Path $testLocal 'notify-stub.ps1'
    $hookLog = Join-Path $testLocal 'notify-stub.log'
    Remove-Item $hookLog -Force -ErrorAction SilentlyContinue
    $stubBody = @'
param([string]$Event = '')
$log = Join-Path $PSScriptRoot 'notify-stub.log'
$raw = ''
if ([Console]::IsInputRedirected) { $raw = [Console]::In.ReadToEnd() }
$flat = ($raw -replace '\r?\n', ' ').Trim()
Add-Content -LiteralPath $log -Value "$Event`t$flat" -Encoding UTF8
exit 0
'@
    Set-Content -Path $stub -Value $stubBody -Encoding utf8
    $alertsFile = Join-Path $testLocal 'alerts.log'
    Remove-Item $alertsFile -Force -ErrorAction SilentlyContinue

    # --test-secure <onMs>@<atSec> simulates the security screen opening (so the alert + re-assert paths run).
    $state = Start-TestCurtain 40 @('--notify-hook', $stub, '--test-secure', '2000@2')
    if (-not $state) { Add-Result $s 'curtain starts' $false; return }

    Start-Sleep -Seconds 6   # past the open (t=2s) and the close (t=4s)
    $ev = Get-RunEvents $state

    Add-Result $s 'the security screen opening is logged' (@($ev | Where-Object { $_.Kind -eq 'security-screen' -and $_.Text -like '*opened*' }).Count -eq 1)
    Add-Result $s 'the security screen closing is logged' (@($ev | Where-Object { $_.Kind -eq 'security-screen' -and $_.Text -like '*closed*' }).Count -eq 1)
    Add-Result $s 'the curtain re-asserts itself after the security screen closes' (@($ev | Where-Object { $_.Kind -eq 'reassert' }).Count -ge 1)

    $alerts = @()
    if (Test-Path $alertsFile) { $alerts = @(Get-Content $alertsFile | ForEach-Object { $_ -split "`t" } ) }
    $alertLines = @()
    if (Test-Path $alertsFile) { $alertLines = @(Get-Content $alertsFile) }
    Add-Result $s 'an alert was raised for the security screen' (@($alertLines | Where-Object { $_ -match "`talert`tsecurity-screen`t" }).Count -eq 1)

    # The stub proves the alert reached the hook with a readable message and the curtain_alert event.
    $hook = @()
    if (Test-Path $hookLog) { $hook = @(Get-Content $hookLog) }
    Add-Result $s 'the alert reached the notify hook' (@($hook | Where-Object { $_ -like 'curtain_alert*' }).Count -ge 1) "$($hook.Count) hook call(s)"
    Add-Result $s 'the hook payload carries a readable message, not raw codes' (@($hook | Where-Object { $_ -like '*security screen*' }).Count -ge 1)
    Add-Result $s 'the hook payload contains no PIN or key codes' (-not ($hook -join ' ' -match '\\u00|keycode'))

    # Rate limit: fire the same kind twice fast via a second simulated open shortly after. Easier to assert
    # the unit-tested collapse here by checking the alerts log never sent two security-screen alerts within 30s.
    $secSent = @($alertLines | Where-Object { $_ -match "`talert`tsecurity-screen`t" }).Count
    Add-Result $s 'repeat security-screen alerts within 30 s collapse to one' ($secSent -eq 1) "$secSent sent"

    & $off -LocalDir $testLocal | Out-Null
    Start-Sleep -Milliseconds 500
    $hook = @()
    if (Test-Path $hookLog) { $hook = @(Get-Content $hookLog) }
    Add-Result $s 'an all-clear reaches the hook when the curtain closes' (@($hook | Where-Object { $_ -like '*all clear*' }).Count -eq 1)
    $allClearLine = ($hook | Where-Object { $_ -like '*all clear*' }) -join ' '
    Add-Result $s 'the all-clear carries the counts' ($allClearLine -like '*Security screen 1*' -and $allClearLine -like '*Task Manager*')

    Remove-Item $stub, $hookLog, $alertsFile -Force -ErrorAction SilentlyContinue
}

function Get-ExecState { ((& $helper execstate) -join '').Trim() }

function Invoke-KeepAwake {
    $s = 'keep-awake'
    Stop-AnyCurtain
    Start-Sleep -Milliseconds 500
    $before = Get-ExecState
    Add-Note $s "keep-awake requests before the curtain: $before  (1 = no sleep, 2 = display stays on, 3 = both, 0 = none)"

    $state = Start-TestCurtain 40
    if (-not $state) { Add-Result $s 'curtain starts' $false; return }
    Start-Sleep -Milliseconds 800
    $during = Get-ExecState
    Add-Result $s 'while the curtain is up, Windows is told not to sleep and not to blank the display' ($during -eq '3') "state $during"
    & $off | Out-Null
    Start-Sleep -Milliseconds 800
    $after = Get-ExecState
    Add-Result $s 'the hold ends the moment the curtain closes normally' ($after -eq $before) "state $after, was $before"

    $state = Start-TestCurtain 40
    if (-not $state) { Add-Result $s 'curtain starts again' $false; return }
    Start-Sleep -Milliseconds 800
    Stop-Process -Id $state.pid -Force
    [void](Wait-CurtainGone 6)
    Start-Sleep -Milliseconds 500
    $after = Get-ExecState
    Add-Result $s 'the hold also ends when the curtain process is killed' ($after -eq $before) "state $after, was $before"
}

function Get-DisplayTimeout {
    $o = & "$env:WINDIR\System32\powercfg.exe" /qh SCHEME_CURRENT SUB_VIDEO VIDEOIDLE
    $line = ($o | Select-String 'Current AC Power Setting Index').Line
    [void]($line -match '0x([0-9a-fA-F]+)')
    [Convert]::ToInt32($Matches[1], 16)
}

function Set-DisplayTimeout([int]$Seconds) {
    & "$env:WINDIR\System32\powercfg.exe" /setacvalueindex SCHEME_CURRENT SUB_VIDEO VIDEOIDLE $Seconds
    & "$env:WINDIR\System32\powercfg.exe" /setactive SCHEME_CURRENT
}

# Changes one setting for about two minutes: "turn off the display after" becomes 30 s, and is put back.
# Needs nobody touching the PC, since the idle timer has to run out.
function Invoke-DisplayOff {
    $s = 'display-off'
    Stop-AnyCurtain
    $original = Get-DisplayTimeout
    Write-Host "BEFORE  turn off the display after: $original s"
    try {
        Set-DisplayTimeout 30
        Add-Result $s 'display timeout temporarily shortened to 30 s' ((Get-DisplayTimeout) -eq 30)

        $control = ((& $helper displaywatch 50) -join '').Trim()
        Add-Result $s 'control: with no curtain, Windows turns the display off after 30 idle seconds' ($control -eq 'off') "display went: $control"
        Start-Sleep -Seconds 2

        $state = Start-TestCurtain 80
        if (-not $state) { Add-Result $s 'curtain starts' $false; return }
        $held = ((& $helper displaywatch 50) -join '').Trim()
        Add-Result $s 'with the curtain up, the display stays on past the same 30 s timeout' ($held -eq 'on') "display stayed: $held"
        Add-Result $s 'the curtain is still up afterwards' ([bool](Get-Process -Id $state.pid -ErrorAction SilentlyContinue))
    } finally {
        Stop-AnyCurtain
        Set-DisplayTimeout $original
        $now = Get-DisplayTimeout
        Write-Host "AFTER   turn off the display after: $now s"
        Add-Result $s 'display timeout restored to its original value' ($now -eq $original) "$now s"
    }
}

# ------------------------------------------------------------------ desktop stages (PC must be unlocked)

function Start-Harness([int]$Seconds) {
    $status = Join-Path $testLocal 'harness.json'
    Remove-Item $status, "$status.stop" -Force -ErrorAction SilentlyContinue
    Start-Process -FilePath $helper -ArgumentList @('harness', "`"$status`"", "$Seconds", '140', '140') -WindowStyle Hidden | Out-Null
    $h = Get-Json $status
    if (-not $h) { throw 'the test target window did not appear' }
    $status
}

function Stop-Harness([string]$Status) {
    Set-Content -Path "$Status.stop" -Value 'stop' -Encoding ascii
    Start-Sleep -Milliseconds 400
    Remove-Item $Status, "$Status.stop" -Force -ErrorAction SilentlyContinue
}

function Invoke-Capture {
    $s = 'capture'
    $state = Start-TestCurtain 60
    if (-not $state) { Add-Result $s 'curtain starts' $false; return }
    $status = Start-Harness 50
    try {
        Start-Sleep -Milliseconds 1500    # let the curtain re-assert itself above the target window
        $h = Get-Json $status
        $shot = & $helper capture '-' ($h.panelX - 20) ($h.panelY - 20) 40 40 | ConvertFrom-Json
        Add-Result $s 'a screenshot taken under the curtain shows the window beneath it, not black' ($shot.center -eq 'FF00FF') "pixel #$($shot.center), expected #FF00FF"
        Add-Type -AssemblyName System.Windows.Forms
        $b = [System.Windows.Forms.Screen]::PrimaryScreen.Bounds
        $png = Join-Path $testLocal 'capture-under-curtain.png'
        $full = & $helper capture $png $b.X $b.Y $b.Width $b.Height | ConvertFrom-Json
        Add-Result $s 'a full-screen screenshot is not a black frame' ($full.nonBlack -gt 0.02) ("{0:p0} of pixels are not black; saved {1}" -f $full.nonBlack, $png)
    } finally {
        Stop-Harness $status
        Stop-AnyCurtain
    }
}

function Invoke-Inject {
    $s = 'inject'
    $before = ((& $helper cursor) -join '').Trim()
    $state = Start-TestCurtain 60
    if (-not $state) { Add-Result $s 'curtain starts' $false; return }
    $status = Start-Harness 50
    try {
        Start-Sleep -Milliseconds 1500
        $h = Get-Json $status

        $pos = ((& $helper move $h.buttonX $h.buttonY) -join '').Trim()
        Add-Result $s 'an injected mouse move (SendInput) moves the pointer' ($pos -eq "$($h.buttonX),$($h.buttonY)") "pointer at $pos"
        $pos = ((& $helper setpos $h.panelX $h.panelY) -join '').Trim()
        Add-Result $s 'an injected mouse move (SetCursorPos) moves the pointer' ($pos -eq "$($h.panelX),$($h.panelY)") "pointer at $pos"

        & $helper click $h.buttonX $h.buttonY | Out-Null
        Start-Sleep -Milliseconds 400
        $h = Get-Json $status
        Add-Result $s 'an injected click passes through the curtain to the button beneath' ($h.clicks -eq 1) "clicks=$($h.clicks)"

        & $helper click $h.boxX $h.boxY | Out-Null
        Start-Sleep -Milliseconds 400
        $h = Get-Json $status
        Add-Result $s 'clicking a window beneath the curtain gives it keyboard focus' ($h.foreground -and $h.focused)
        if ($h.foreground -and $h.focused) {
            & $helper text 'curtain-ok-123' | Out-Null
            & $helper keys '65,66' | Out-Null       # A, B as plain key presses
            Start-Sleep -Milliseconds 400
            $h = Get-Json $status
            Add-Result $s 'injected typing reaches the focused window' ($h.text -eq 'curtain-ok-123ab') "text='$($h.text)'"
        } else {
            Add-Note $s 'typing was skipped because the test window did not get focus'
        }
        Add-Result $s 'the curtain is still up after all that' ([bool](Get-Process -Id $state.pid -ErrorAction SilentlyContinue))
    } finally {
        Stop-Harness $status
        Stop-AnyCurtain
        if ($before -match '^(-?\d+),(-?\d+)$') { & $helper setpos $Matches[1] $Matches[2] | Out-Null }
    }
}

function Invoke-PinInjected {
    $s = 'pin-injected'
    $wrong = '00000000'
    if ($script:TestPin -eq $wrong) { $wrong = '11111111' }
    $state = Start-TestCurtain 60 @('--test-pin-from-injected')
    if (-not $state) { Add-Result $s 'curtain starts' $false; return }
    $status = Start-Harness 50
    try {
        Start-Sleep -Milliseconds 1500
        $h = Get-Json $status
        & $helper click $h.boxX $h.boxY | Out-Null
        Start-Sleep -Milliseconds 400
        $h = Get-Json $status
        if (-not ($h.foreground -and $h.focused)) {
            Add-Result $s 'test window has focus (keys must land somewhere harmless)' $false
            return
        }
        & $helper keys (Get-KeyScript $wrong) | Out-Null
        Start-Sleep -Milliseconds 1200
        Add-Result $s 'a wrong PIN through the keyboard hook does not close the curtain' ([bool](Get-Process -Id $state.pid -ErrorAction SilentlyContinue))
        Start-Sleep -Milliseconds 2600
        & $helper keys (Get-KeyScript $script:TestPin) | Out-Null
        $gone = Wait-PidGone $state.pid 6
        Add-Result $s 'the right PIN through the keyboard hook closes the curtain' ($gone -ge 0) ("{0:n1} s" -f $gone)
        Test-ExitReason $s 'pin'
    } finally {
        Stop-Harness $status
        Stop-AnyCurtain
    }
}

function Invoke-SetPin {
    $s = 'setpin'
    Stop-AnyCurtain
    $dir = Join-Path $testLocal 'setpin'
    New-Item -ItemType Directory -Force -Path $dir | Out-Null
    $file = Join-Path $dir 'pin.hash'
    Remove-Item $file -Force -ErrorAction SilentlyContinue
    $p = Start-Process -FilePath $script:CurtainExe -ArgumentList @('--set-pin', '--local-dir', "`"$dir`"") -PassThru
    try {
        Start-Sleep -Milliseconds 1500
        $dialog = Get-Windows $p.Id -Children | Where-Object { $_.title -like 'Claude curtain*' } | Select-Object -First 1
        if (-not $dialog) { Add-Result $s 'the set-PIN dialog opens' $false; return }
        Add-Result $s 'the set-PIN dialog opens' $true
        $edits = @($dialog.children | Where-Object { $_.class -like '*EDIT*' } | Sort-Object y)
        if ($edits.Count -ne 2) { Add-Result $s 'the dialog has two PIN boxes' $false "$($edits.Count) found"; return }
        $digits = (@('2', '7', '1', '8', '2', '8') | ForEach-Object { 48 + [int]$_ }) -join ','
        & $helper click ($edits[0].x + [int]($edits[0].w / 2)) ($edits[0].y + [int]($edits[0].h / 2)) | Out-Null
        Start-Sleep -Milliseconds 300
        & $helper keys $digits | Out-Null
        & $helper click ($edits[1].x + [int]($edits[1].w / 2)) ($edits[1].y + [int]($edits[1].h / 2)) | Out-Null
        Start-Sleep -Milliseconds 300
        & $helper keys "$digits,13" | Out-Null
        $exited = $p.WaitForExit(6000)
        Add-Result $s 'typing a PIN twice and pressing Enter saves and closes the dialog' ($exited -and $p.ExitCode -eq 0) "exit code $($p.ExitCode)"
        $ok = (Test-Path $file) -and (((& $helper checkpin $file '271828') -join '').Trim() -eq 'match')
        Add-Result $s 'the saved hash matches the PIN that was typed' $ok
        $text = ''
        if (Test-Path $file) { $text = Get-Content $file -Raw }
        Add-Result $s 'the file holds a salted hash, not the PIN' ($text -like 'v1$pbkdf2-sha256$*' -and $text -notmatch '271828')
    } finally {
        if (-not $p.HasExited) { Stop-Process -Id $p.Id -Force -ErrorAction SilentlyContinue }
        Remove-Item $dir -Recurse -Force -ErrorAction SilentlyContinue
    }
}

# ------------------------------------------------------------------ run

& (Join-Path $PSScriptRoot 'build.ps1') -Tests
New-Item -ItemType Directory -Force -Path $testLocal | Out-Null
Stop-AnyCurtain
$script:TestPin = ((& $helper makepin (Join-Path $testLocal 'pin.hash')) -join '').Trim()
if ($script:TestPin -notmatch '^\d{8}$') { throw 'could not create the throwaway test PIN' }

$needsDesktop = @($Stage | Where-Object { $desktopStages -contains $_ })
$unlocked = Test-Unlocked
if ($needsDesktop.Count -gt 0 -and -not $unlocked) {
    Write-Host "SKIP  the PC is locked, so these stages cannot run now: $($needsDesktop -join ', ')"
    $Stage = @($Stage | Where-Object { $desktopStages -notcontains $_ })
}
Write-Host ("Running: {0}   (PC is {1})" -f ($Stage -join ', '), $(if ($unlocked) { 'unlocked' } else { 'locked' }))

$started = Get-Date
try {
    foreach ($name in $Stage) {
        try {
            switch ($name) {
                'selftest'        { Invoke-SelfTest }
                'smoke'           { Invoke-Smoke }
                'off'             { Invoke-Off }
                'kill'            { Invoke-Kill }
                'watchdog-lost'   { Invoke-WatchdogLost }
                'session-end'     { Invoke-SessionEnd }
                'close'           { Invoke-Close }
                'pin'             { Invoke-Pin }
                'second-instance' { Invoke-SecondInstance }
                'no-pin'          { Invoke-NoPin }
                'freeze'          { Invoke-Freeze }
                'two-minute'      { Invoke-TwoMinute }
                'keep-awake'      { Invoke-KeepAwake }
                'events'          { Invoke-Events }
                'alerts'          { Invoke-Alerts }
                'display-off'     { Invoke-DisplayOff }
                'capture'         { Invoke-Capture }
                'inject'          { Invoke-Inject }
                'pin-injected'    { Invoke-PinInjected }
                'setpin'          { Invoke-SetPin }
                default           { Add-Result $name 'known stage name' $false }
            }
        } catch {
            Add-Result $name 'stage ran without an error' $false $_.Exception.Message
        }
    }
} finally {
    Stop-AnyCurtain
    Remove-Item (Join-Path $testLocal 'pin.hash') -Force -ErrorAction SilentlyContinue
}

$failed = @($results | Where-Object { -not $_.Ok })
$left = (Get-CurtainProcess).Count
$summary = "{0} checks, {1} passed, {2} failed, in {3}. Curtain processes left running: {4}." -f `
    $results.Count, ($results.Count - $failed.Count), $failed.Count, (Format-CurtainSpan ((Get-Date) - $started).TotalSeconds), $left
Write-Host ''
Write-Host $summary

$report = Join-Path $testLocal ("report-{0:yyyyMMdd-HHmmss}.txt" -f (Get-Date))
@($results | ForEach-Object {
    $mark = 'FAIL'
    if ($_.Ok) { $mark = 'PASS' }
    $tail = ''
    if ($_.Detail) { $tail = "  ($($_.Detail))" }
    "{0}  [{1}] {2}{3}" -f $mark, $_.Stage, $_.Name, $tail
}) + '' + $summary | Set-Content -Path $report -Encoding utf8
Write-Host "Report: $report"

if ($failed.Count -gt 0 -or $left -gt 0) { exit 1 }
exit 0
