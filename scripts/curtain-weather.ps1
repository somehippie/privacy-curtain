<#
.SYNOPSIS
  Writes a short local forecast for the curtain to show on screen: three days to local\weather.txt and the
  next twelve hours to local\weather-hourly.txt.

.DESCRIPTION
  Source: the US National Weather Service (api.weather.gov) - keyless, no account, no install. The curtain
  reads local\ and refreshes the screen on its own, so this can be run any time. Best-effort: on any failure
  it prints a note and leaves the existing files alone, so a lost network never blanks the screen with an
  error.

  curtain-on.ps1 starts it with -Watch and the curtain's process ID: it then refreshes every
  -RefreshMinutes (30) for as long as that curtain is up, so the "next 6 hours" strip stays current
  through a long session, and exits by itself when the curtain closes. The curtain process itself never
  touches the network.

  Your location is NOT baked into this script. Set it in config.json (copy config.example.json). Until a
  real NWS grid is configured, this writes nothing and the curtain simply shows no forecast.

  Weather Underground's v3 forecast API needs a paid key, so a standalone screen can't assume it. If you
  have one, put it in config.json as "wuApiKey" - this script does not use it yet, but that is where it
  would go.

.PARAMETER Grid
  NWS forecast office + grid cell, e.g. "ABC/12,34". Overrides config.json. Find yours:
  Invoke-RestMethod "https://api.weather.gov/points/<lat>,<lon>" -Headers @{'User-Agent'='app'} |
    ForEach-Object properties | ForEach-Object { "$($_.gridId)/$($_.gridX),$($_.gridY)" }

.PARAMETER Place
  A label to show on screen, e.g. "Your City". Overrides config.json.

.PARAMETER Days
  How many days to show (default 3).

.PARAMETER Watch
  Keep refreshing while the curtain with process ID -CurtainPid is running.
#>
[CmdletBinding()]
param(
    [string]$LocalDir,
    [string]$Grid,
    [string]$Place,
    [int]$Days = 3,
    [int]$TimeoutSec = 12,
    [switch]$Watch,
    [int]$CurtainPid = 0,
    [int]$RefreshMinutes = 30
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
if (-not $LocalDir) { $LocalDir = Join-Path $root 'local' }
$LocalDir = $LocalDir.TrimEnd('\')
New-Item -ItemType Directory -Force -Path $LocalDir | Out-Null
$out = Join-Path $LocalDir 'weather.txt'
$hourlyOut = Join-Path $LocalDir 'weather-hourly.txt'
$deg = [char]0x00B0
$ua = 'PrivacyCurtain/1.0 (personal privacy screen)'

# Location comes from config.json (yours, gitignored); config.example.json is the shipped template.
function Read-CurtainConfig {
    foreach ($name in 'config.json', 'config.example.json') {
        $path = Join-Path $root $name
        if (Test-Path $path) {
            try { return (Get-Content $path -Raw -Encoding UTF8 | ConvertFrom-Json) } catch { }
        }
    }
    return $null
}

$cfg = Read-CurtainConfig
if (-not $Grid  -and $cfg -and $cfg.grid)  { $Grid  = [string]$cfg.grid }
if (-not $Place -and $cfg -and $cfg.place) { $Place = [string]$cfg.place }
if (-not $Place) { $Place = 'Local' }

# A real location must be set first. The template ships with no grid on purpose.
if (-not $Grid -or $Grid -notmatch '^[A-Za-z]{3}/\d+,\d+$') {
    Write-Host 'No location set. Copy config.example.json to config.json and set your NWS "grid" (see the file). Nothing written.'
    exit 0
}
$inv = [Globalization.CultureInfo]::InvariantCulture

function ConvertTo-Start($value) {
    if ($value -is [datetime]) { return [DateTimeOffset]$value }
    return [DateTimeOffset]::Parse([string]$value, $inv)
}

# Day labels the curtain can lay out in a fixed column: Today, Tonight or a weekday. NWS sometimes names a
# period after a holiday ("Thanksgiving Day") or a part of the day ("This Afternoon").
function Get-DayLabel($period) {
    switch -Regex ([string]$period.name) {
        '^(Today|This Afternoon)$' { return 'Today' }
        '^(Tonight|Overnight|This Evening)$' { return 'Tonight' }
        default {
            $day = (ConvertTo-Start $period.startTime).DayOfWeek.ToString()
            if ($period.isDaytime) { return $day } else { return "$day Night" }
        }
    }
}

function Write-Atomic([string]$Path, $Lines) {
    $tmp = "$Path.tmp"
    Set-Content -Path $tmp -Value $Lines -Encoding UTF8
    Move-Item -Path $tmp -Destination $Path -Force
}

function Update-Daily {
    $r = Invoke-RestMethod -Uri "https://api.weather.gov/gridpoints/$Grid/forecast?units=us" `
                           -Headers @{ 'User-Agent' = $ua } -TimeoutSec $TimeoutSec
    $periods = @($r.properties.periods)
    if ($periods.Count -eq 0) { throw 'no forecast periods returned' }

    $lines = New-Object System.Collections.Generic.List[string]
    $lines.Add(("{0} forecast  (as of {1})" -f $Place, (Get-Date -Format 't')))
    $shown = 0
    for ($i = 0; $i -lt $periods.Count -and $shown -lt $Days; $i++) {
        $p = $periods[$i]
        $hi = $null; $lo = $null
        if ($p.isDaytime) {
            $hi = $p.temperature
            if (($i + 1) -lt $periods.Count -and -not $periods[$i + 1].isDaytime) { $lo = $periods[$i + 1].temperature; $i++ }
        } else {
            $lo = $p.temperature   # ran in the evening: first period is a night
        }
        if ($null -ne $hi -and $null -ne $lo) { $temp = "Hi $hi$deg / Lo $lo$deg" }
        elseif ($null -ne $hi) { $temp = "Hi $hi$deg" }
        else { $temp = "Lo $lo$deg" }

        $desc = ($p.shortForecast -replace '\s+', ' ').Trim()
        $pop = $null
        try { $pop = [int]$p.probabilityOfPrecipitation.value } catch {}
        $popTxt = if ($pop -gt 0) { "  ({0}% precip)" -f $pop } else { '' }
        $lines.Add(("{0}:  {1}   {2}{3}" -f (Get-DayLabel $p), $temp, $desc, $popTxt))
        $shown++
    }
    Write-Atomic $out $lines
    Write-Host ("Wrote {0} ({1} day(s))." -f $out, $shown)
}

# The next twelve hours, one tab-separated line each: start (ISO 8601 with offset), temperature (F),
# chance of precipitation (%; empty if unknown), 1 for daytime or 0, short forecast, relative humidity
# (%; empty if unknown), wind ("5-10 mph WNW", or "Calm"). A "# location <lat> <lon>" line gives the
# forecast grid's centre, from which the curtain works out sunrise and sunset itself.
function Update-Hourly {
    $r = Invoke-RestMethod -Uri "https://api.weather.gov/gridpoints/$Grid/forecast/hourly?units=us" `
                           -Headers @{ 'User-Agent' = $ua } -TimeoutSec $TimeoutSec
    $now = [DateTimeOffset]::Now
    $lines = New-Object System.Collections.Generic.List[string]
    $lines.Add(("# hourly forecast for {0}, as of {1}" -f $Place, (Get-Date -Format 't')))
    try {
        $ring = @($r.geometry.coordinates[0])
        if ($ring.Count -gt 0) {
            $lat = ($ring | ForEach-Object { [double]$_[1] } | Measure-Object -Average).Average
            $lon = ($ring | ForEach-Object { [double]$_[0] } | Measure-Object -Average).Average
            $lines.Add(('# location {0} {1}' -f $lat.ToString('0.0000', $inv), $lon.ToString('0.0000', $inv)))
        }
    } catch {}
    foreach ($p in @($r.properties.periods)) {
        $start = ConvertTo-Start $p.startTime
        if ($start.AddHours(1) -le $now) { continue }   # already over
        $pop = ''
        try { if ($null -ne $p.probabilityOfPrecipitation.value) { $pop = [int]$p.probabilityOfPrecipitation.value } } catch {}
        $day = if ($p.isDaytime) { 1 } else { 0 }
        $hum = ''
        try { if ($null -ne $p.relativeHumidity.value) { $hum = [int]$p.relativeHumidity.value } } catch {}
        $ws = (([string]$p.windSpeed).Trim() -replace ' to ', [string][char]0x2013)
        $wind = if ($ws -match '^0 mph$' -or -not $ws) { 'Calm' } else { ('{0} {1}' -f $ws, $p.windDirection).Trim() }
        $lines.Add(("{0}`t{1}`t{2}`t{3}`t{4}`t{5}`t{6}" -f $start.ToString('o', $inv), $p.temperature, $pop, $day, (($p.shortForecast -replace '\s+', ' ').Trim()), $hum, $wind))
        if (($lines | Where-Object { $_ -notlike '#*' }).Count -ge 12) { break }
    }
    $hours = @($lines | Where-Object { $_ -notlike '#*' }).Count
    if ($hours -lt 1) { throw 'no upcoming hours returned' }
    Write-Atomic $hourlyOut $lines
    Write-Host ("Wrote {0} ({1} hour(s))." -f $hourlyOut, $hours)
}

function Update-Forecast {
    try { [Net.ServicePointManager]::SecurityProtocol = [Net.ServicePointManager]::SecurityProtocol -bor [Net.SecurityProtocolType]::Tls12 } catch {}
    $ok = $true
    try { Update-Daily } catch { $ok = $false; Write-Host "Forecast fetch skipped: $($_.Exception.Message) (the curtain keeps what it has)." }
    try { Update-Hourly } catch { Write-Host "Hourly forecast skipped: $($_.Exception.Message)." }
    return $ok
}

function Test-CurtainAlive {
    if ($CurtainPid -le 0) { return $false }
    return [bool](Get-Process -Id $CurtainPid -ErrorAction SilentlyContinue)
}

if (-not $Watch) {
    if (Update-Forecast) { exit 0 } else { exit 1 }
}

# Watch: refresh now, then every RefreshMinutes while the curtain is up; leave when it closes.
while ($true) {
    [void](Update-Forecast)
    $next = (Get-Date).AddMinutes([Math]::Max(5, $RefreshMinutes))
    while ((Get-Date) -lt $next) {
        if (-not (Test-CurtainAlive)) { exit 0 }
        Start-Sleep -Seconds 15
    }
}
