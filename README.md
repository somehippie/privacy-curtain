# Privacy curtain

A black screen that covers every monitor ("PC in use" and a clock) and blocks the physical keyboard and
mouse, while automated (injected) clicks, typing and screenshots keep working underneath. It's for hiding
an automated session on a machine you share a room with, without taking the machine away from yourself.

No installs, no administrator rights, nothing that survives a reboot. It compiles with the C# compiler that
already ships inside Windows (.NET Framework 4).

> **This is not a security tool.** It hides a screen and swallows stray input; it does not lock the machine.
> Read [Limitations](#limitations) before relying on it.

## Requirements

- Windows 10 version 2004 (build 19041) or later, or Windows 11. The capture-exclusion it relies on
  (`SetWindowDisplayAffinity(WDA_EXCLUDEFROMCAPTURE)`) needs that version.
- The in-box .NET Framework 4 compiler (`csc.exe` under `%WINDIR%\Microsoft.NET\Framework64\v4.0.30319`).
  Present on a normal Windows install. Nothing is downloaded or installed.

## Build

From the repository root:

```powershell
.\scripts\build.ps1            # produces bin\ClaudeCurtain.exe
.\scripts\build.ps1 -Tests     # also builds the test helper
```

`bin\` is build output and is gitignored.

## Use

```powershell
.\scripts\curtain-setpin.ps1              # once, at the PC: set the unlock PIN
.\scripts\curtain-on.ps1                  # 8 hours
.\scripts\curtain-on.ps1 -Hours 3
.\scripts\curtain-on.ps1 -Minutes 2       # handy for a first test
.\scripts\curtain-off.ps1                 # remote unlock (from another shell / remote session)
.\scripts\curtain-status.ps1 -Log 20
```

The curtain refuses to start until a PIN is set. The PIN is typed into a dialog on the PC's screen; only a
salted PBKDF2-HMAC-SHA256 hash is kept, in `local\pin.hash` (gitignored). A fresh random salt is generated
per machine the first time you set a PIN; nothing about the PIN is ever committed. Timer: default 8 hours,
at most 24.

## Ways back in

Any one of these is enough, and they do not depend on each other.

1. **PIN.** Type the digits, then Enter, on the physical keyboard. Backspace corrects, Esc clears. Five
   wrong tries in a row cost a 30-second pause, never more.
2. **`curtain-off.ps1`.** Asks the curtain to close, then kills the process if needed. A standalone script
   that loads nothing else, so it works even if the rest of the tool is broken.
3. **The timer.** Two independent clocks (wall time and system uptime); either one ends it. Time asleep
   counts.
4. **The watchdog.** A second process ends the curtain if it overruns its timer by a few seconds or stops
   responding.
5. **Ctrl+Alt+Del.** Never intercepted. From that screen: Sign out, or the power icon.
6. **The power button.** Not keyboard input, so it cannot be blocked. The curtain closes the instant Windows
   asks apps to end the session, on a listener that does not depend on its window thread, so it can't hold
   up a shutdown. (Set the button to *Shut down* under Power Options if you want a one-press exit.)
7. **Anything that ends the process.** The block exists only while `ClaudeCurtain.exe` is alive:
   `taskkill /F /IM ClaudeCurtain.exe`, a crash, or a reboot.

When anything looks wrong (a stuck thread, a lost watchdog, a hook error) the curtain closes itself. It
never changes sleep settings, Windows sign-in, the registry, or startup. The design rule throughout: being
open by mistake costs some privacy; being stuck closed would cost you the use of your own PC, so every
fault fails open.

## Staying awake and unlocked

While the curtain is up it holds a keep-awake request (`SetThreadExecutionState`: system + display). That
puts sleep, hibernate, display-off and the screen saver on hold, because any of them can end at the Windows
lock screen where the automated session can't work. It is a request held by the process, not a settings
change: it ends the moment the curtain closes or its process dies, and your normal settings apply again.

It does **not** override two things that can still lock the machine behind the curtain. Check these
yourself before relying on it:

- **A machine inactivity limit** (the `InactivityTimeoutSecs` policy), if your organization sets one.
- **Dynamic Lock** (locks when a paired phone walks away), under Settings > Accounts > Sign-in options.

`curtain-on.ps1` also lists any administrator window that is open when it starts, because physical keys can
reach those behind the curtain (see [Limitations](#limitations)).

## Event log and "While you were away"

The curtain says on screen that attempts to use, unlock or interrupt the PC are logged, and
`local\events.log` (gitignored, capped at 1 MB, oldest part dropped) is that log. It is written on events
only, plus at most one line a minute. It holds counts, never PIN digits and never which keys were pressed.

```powershell
.\scripts\curtain-log.ps1                 # last 40 events
.\scripts\curtain-log.ps1 -NoHeartbeat -Summary
```

Recorded, each with a timestamp: start and stop (and how it stopped); wrong-PIN attempts as a count;
physical key/mouse use while blocked as counts per minute; the Windows security screen opening and closing;
Task Manager coming to the front and any administrator window taking focus; session lock/unlock and Remote
Desktop; network drops and returns; the process being killed; and, at the next start, an earlier curtain
that ended with no record (power cut, crash, kill).

On a PIN unlock a "While you were away" window shows the totals and closes itself after 10 seconds;
`curtain-off.ps1` prints the same summary. The closing time is not shown on screen; `curtain-on.ps1` and
`curtain-status.ps1` print it.

## Alerts (optional)

The curtain can make interference *visible* without blocking anything. When it detects the security screen
opening, Task Manager coming to the front, or an administrator window taking focus, it can send an alert
through an external notify hook (a PowerShell script you point it at), on top of the event-log line, and
records it in `local\alerts.log`.

- The hook is optional. With none configured, alerts are logged only. Point it at your own script with
  `--notify-hook` (the default looks for `%USERPROFILE%\.claude\hooks\notify.ps1` and is skipped if absent).
- Repeat alerts of the same kind within 30 seconds collapse into one. One "all clear" with the counts is
  sent when the curtain closes cleanly. After the security screen closes, the curtain re-asserts itself to
  the foreground (without stealing focus) so the black screen returns on top.
- Nothing here blocks or changes anything; a failed alert never affects the curtain.

## On-screen info

Across the top of the curtain, dim so it never competes with the message below, is a glanceable panel that
drifts slowly with everything else to avoid burn-in:

- **Machine stats**, live from inside the curtain (sampled about once a second, no performance counters, no
  admin): CPU busy %, memory used (% and GB), network throughput (down / up), then system uptime and free
  space on the system drive.
- **A short local forecast.** Set your location in `config.json` (copy `config.example.json`):

  ```json
  { "grid": "ABC/12,34", "place": "Your City", "wuApiKey": "" }
  ```

  Find your `grid` at `https://api.weather.gov/points/<lat>,<lon>` (read `gridId`, `gridX`, `gridY`).
  `curtain-weather.ps1` then writes three days to `local\weather.txt`, and the curtain reads that file and
  refreshes whenever it changes. `curtain-on.ps1` runs the fetch in the background at start; you can also
  run it any time:

  ```powershell
  .\scripts\curtain-weather.ps1
  ```

  The source is the US National Weather Service (`api.weather.gov`): keyless, no account, no install. It is
  best-effort: a lost network shows no forecast rather than an error, and anything older than a day is
  dropped. The User-Agent sent to NWS is a generic client name. Weather Underground's forecast API now needs
  a paid key, so it is not the default; `config.json` has a `wuApiKey` slot for where one would go.

Everything here is read-only and isolated: a failed reading shows `--`, and nothing in it can hold up the
curtain or a shutdown.

## Limitations

Please read these before relying on the curtain.

- **It is not a security boundary.** It is a personal privacy curtain: it hides the screen and swallows
  stray keyboard and mouse input from someone walking up. It does not lock the machine. To actually secure
  the PC, lock Windows.
- **The PIN is not a real lock.** It is a convenience for dismissing the curtain at the keyboard. It is only
  lightly rate-limited (a 30-second pause after five wrong tries) with no lasting lockout, so it will not
  withstand determined guessing. And anyone who can reach the running process can simply close it (Task
  Manager, `taskkill`, Ctrl+Alt+Del > Sign out, or the power button). That is deliberate, so you can never
  be locked out of your own machine.
- **Injected input is trusted.** The curtain blocks physical keyboard and mouse but deliberately lets
  *injected* (synthetic) input through, so an automation tool can keep driving the machine underneath. This
  means anyone or anything that can inject input — including remote-control software — operates the machine
  through the curtain. Treat the curtain as hiding a session you trust, not as walling one off.
- **Administrator windows.** Without admin rights, Windows hides input aimed at an elevated window (Task
  Manager, an admin shell) from the hooks, so physical keys reach that window. While one has focus the
  curtain says so and switches PIN entry to registered hotkeys. If the PIN seems dead, press the Windows key
  first.
- **Things Windows draws above every app** can show over the curtain: toast notifications, the Start menu,
  Alt+Tab, the volume popup. Turn on Focus Assist if toasts matter. The mouse pointer also stays visible.
- **Games.** Apps that read raw input (most games) and game controllers are not blocked. Not tested with
  exclusive-fullscreen games.

## Tests

```powershell
.\scripts\test-curtain.ps1            # lifecycle, works while locked
.\scripts\test-curtain.ps1 -Desktop   # screenshots and injected input; run with the PC unlocked
```

They use a throwaway PIN in `local\test` and never touch the real one. The self-tests (~67 checks,
including PIN hashing/entry rules, the stats sampler and weather-file reading) and the full lifecycle suite
(~95 checks: a screenshot seeing the desktop through the curtain, injected clicks and typing passing
through, simulated shutdown, every hang being recovered, the keep-awake request appearing with the curtain
and vanishing with it, the alerts, the re-assert, and the event log/summary) both pass. The on-screen info
panel and real physical key presses are not script-testable (the curtain is excluded from screen capture by
design); a person-at-the-PC checklist is in the lifecycle test's output.

`-Stage display-off` is a stronger keep-awake proof kept out of the default run, because it changes a
display-timeout setting for about two minutes and then restores it. Nobody should touch the PC while it
runs.

## Layout

- `src/` — C# source, compiled by `scripts/build.ps1` with the in-box Windows `csc.exe` into `bin/`
  (ignored).
- `scripts/` — the PowerShell entry points (on, off, set-pin, status, log, weather, build, test).
- `tests/` — the test helper used by `test-curtain.ps1`.
- `local/` (ignored) — the PIN hash, logs, run state, and the cached forecast.

## License

MIT. See [LICENSE](LICENSE).
