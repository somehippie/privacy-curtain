<#
.SYNOPSIS
  Writes a short local forecast to local\weather.txt for the curtain to show on screen.

.DESCRIPTION
  Source: the US National Weather Service (api.weather.gov) - keyless, no account, no install. The curtain
  reads local\weather.txt and refreshes the screen on its own, so this can be run any time (curtain-on.ps1
  runs it in the background at start). Best-effort: on any failure it prints a note and leaves the existing
  file alone, so a lost network never blanks the screen with an error.

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
#>
[CmdletBinding()]
param(
    [string]$LocalDir,
    [string]$Grid,
    [string]$Place,
    [int]$Days = 3,
    [int]$TimeoutSec = 12
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
if (-not $LocalDir) { $LocalDir = Join-Path $root 'local' }
$LocalDir = $LocalDir.TrimEnd('\')
New-Item -ItemType Directory -Force -Path $LocalDir | Out-Null
$out = Join-Path $LocalDir 'weather.txt'
$deg = [char]0x00B0

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

try {
    try { [Net.ServicePointManager]::SecurityProtocol = [Net.ServicePointManager]::SecurityProtocol -bor [Net.SecurityProtocolType]::Tls12 } catch {}
    $ua = 'PrivacyCurtain/1.0 (personal privacy screen)'
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

        $lines.Add(("{0}:  {1}   {2}{3}" -f $p.name, $temp, $desc, $popTxt))
        $shown++
    }

    $tmp = "$out.tmp"
    Set-Content -Path $tmp -Value $lines -Encoding UTF8
    Move-Item -Path $tmp -Destination $out -Force
    $lines | ForEach-Object { Write-Verbose $_ }
    Write-Host ("Wrote {0} ({1} day(s))." -f $out, $shown)
    exit 0
}
catch {
    Write-Host "Weather fetch skipped: $($_.Exception.Message) (the curtain just shows no forecast)."
    exit 1
}
