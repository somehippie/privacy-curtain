<#
.SYNOPSIS
  Writes a short local forecast for the curtain to show on screen: three days to local\weather.txt and the
  next twelve hours to local\weather-hourly.txt.

.DESCRIPTION
  Source: the US National Weather Service (api.weather.gov) - keyless, no account, no install. The curtain
  reads local\ and refreshes the screen on its own, so this can be run any time. Best-effort: on any failure
  it prints a note and leaves the existing files alone, so a lost network never blanks the screen with an
  error.

  It also keeps the curtain's weather map current: NOAA's radar mosaic and GOES satellite pictures for
  the last hour, over a box around the forecast grid, plus a land and water map (local\map.txt lists
  them). See Update-Map below.

  curtain-on.ps1 starts it with -Watch and the curtain's process ID: it then refreshes the forecast every
  -RefreshMinutes (30) and the map every -MapMinutes (5) for as long as that curtain is up, so the
  "next 6 hours" strip and the radar stay current through a long session, and exits by itself when the
  curtain closes. The curtain process itself never touches the network.

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

.PARAMETER NoMap
  Leave the weather map out (no radar or satellite downloads).
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
    [int]$RefreshMinutes = 30,
    [int]$MapMinutes = 5,
    [switch]$NoMap
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

# Each hour's wind and gusts (mph) from the forecast grid's raw data, which NWS gives as km/h over intervals
# ("2026-11-21T16:00:00+00:00/PT3H"). Empty when the request fails: the days then show no wind.
function Get-WindHours {
    $hours = @{}
    try {
        $g = Invoke-RestMethod -Uri "https://api.weather.gov/gridpoints/$Grid" -Headers @{ 'User-Agent' = $ua } -TimeoutSec $TimeoutSec
        foreach ($kind in 'windSpeed', 'windGust', 'temperature') {
            foreach ($v in @($g.properties.$kind.values)) {
                if ($null -eq $v.value) { continue }
                $parts = ([string]$v.validTime).Split('/')
                $start = [DateTimeOffset]::Parse($parts[0], $inv)
                $span = [Xml.XmlConvert]::ToTimeSpan($parts[1])
                $value = if ($kind -eq 'temperature') { [double]$v.value } else { [double]$v.value / 1.609344 }   # degrees C; km/h to mph
                for ($h = 0; $h -lt [Math]::Max(1, [int]$span.TotalHours); $h++) {
                    $at = $start.AddHours($h).ToLocalTime()
                    $key = $at.ToString('yyyy-MM-ddTHH', $inv)
                    if (-not $hours.ContainsKey($key)) { $hours[$key] = [pscustomobject]@{ At = $at; Speed = $null; Gust = $null; Temp = $null } }
                    switch ($kind) { 'windSpeed' { $hours[$key].Speed = $value } 'windGust' { $hours[$key].Gust = $value } default { $hours[$key].Temp = $value } }
                }
            }
        }
    } catch {
        Write-Host "Wind for the days skipped: $($_.Exception.Message)."
    }
    return @($hours.Values | Sort-Object At)
}

# The strongest wind in a stretch of hours, as " [wind 18 mph at 3 PM, gusts 30]" (the first hour it peaks),
# or '' when the stretch has no hours.
function Get-WindPeak($WindHours, [DateTimeOffset]$From, [DateTimeOffset]$To) {
    $in = @($WindHours | Where-Object { $_.At -ge $From -and $_.At -lt $To -and $null -ne $_.Speed })
    if ($in.Count -eq 0) { return '' }
    $top = [Math]::Round(($in | Measure-Object -Property Speed -Maximum).Maximum)
    $peak = $in | Where-Object { [Math]::Round($_.Speed) -eq $top } | Select-Object -First 1
    $gusts = @($WindHours | Where-Object { $_.At -ge $From -and $_.At -lt $To -and $null -ne $_.Gust })
    $gustTxt = ''
    if ($gusts.Count -gt 0) {
        $gust = [Math]::Round(($gusts | Measure-Object -Property Gust -Maximum).Maximum)
        if ($gust -gt $top) { $gustTxt = ", gusts $gust" }
    }
    return ("  [wind {0} mph at {1}{2}]" -f $top, $peak.At.ToString('h tt', [Globalization.CultureInfo]::GetCultureInfo('en-US')), $gustTxt)
}

# When a day is warmest and when the night after it is coolest, as " [hi 3 PM, lo 6 AM]" (either part may be
# missing; '' when neither is known): the first hour at the top (or the bottom) of the grid's hourly temperature.
function Get-TempTimes($Hours, $HiFrom, $HiTo, $LoFrom, $LoTo) {
    $us = [Globalization.CultureInfo]::GetCultureInfo('en-US')
    $parts = @()
    foreach ($pick in @(@('hi', $HiFrom, $HiTo, 'Descending'), @('lo', $LoFrom, $LoTo, 'Ascending'))) {
        if ($null -eq $pick[1]) { continue }
        $in = @($Hours | Where-Object { $_.At -ge $pick[1] -and $_.At -lt $pick[2] -and $null -ne $_.Temp })
        if ($in.Count -eq 0) { continue }
        $top = if ($pick[3] -eq 'Descending') { ($in | Measure-Object -Property Temp -Maximum).Maximum } else { ($in | Measure-Object -Property Temp -Minimum).Minimum }
        $at = ($in | Where-Object { $_.Temp -eq $top } | Select-Object -First 1).At
        $parts += ('{0} {1}' -f $pick[0], $at.ToString('h tt', $us))
    }
    if ($parts.Count -eq 0) { return '' }
    return '  [' + ($parts -join ', ') + ']'
}

# A row's hours for the curtain's graphs (2026-11-21): temperature (F), wind and gusts (mph) every hour from From
# (on the hour) until To, as " [hours START t,t,... w,w,... g,g,...]", START the first hour (ISO 8601 with its
# offset) and "x" for an hour the grid lacks; '' when the grid gave no temperatures for them.
function Get-DayHours($Hours, [DateTimeOffset]$From, [DateTimeOffset]$To) {
    $byHour = @{}
    foreach ($h in $Hours) { $byHour[$h.At.ToString('yyyy-MM-ddTHH', $inv)] = $h }
    $start = $From.AddMinutes(-$From.Minute).AddSeconds(-$From.Second).AddMilliseconds(-$From.Millisecond)
    $t = New-Object System.Collections.Generic.List[string]
    $w = New-Object System.Collections.Generic.List[string]
    $g = New-Object System.Collections.Generic.List[string]
    $known = 0
    for ($at = $start; $at -lt $To -and $t.Count -lt 48; $at = $at.AddHours(1)) {
        $h = $byHour[$at.ToLocalTime().ToString('yyyy-MM-ddTHH', $inv)]
        if ($h -and $null -ne $h.Temp) { $t.Add(([Math]::Round($h.Temp * 9 / 5 + 32)).ToString($inv)); $known++ } else { $t.Add('x') }
        if ($h -and $null -ne $h.Speed) { $w.Add(([Math]::Round($h.Speed)).ToString($inv)) } else { $w.Add('x') }
        if ($h -and $null -ne $h.Gust) { $g.Add(([Math]::Round($h.Gust)).ToString($inv)) } else { $g.Add('x') }
    }
    if ($known -lt 2) { return '' }
    return ('  [hours {0} {1} {2} {3}]' -f $start.ToString("yyyy-MM-dd'T'HH:mm:sszzz", $inv), ($t -join ','), ($w -join ','), ($g -join ','))
}

function Update-Daily {
    $r = Invoke-RestMethod -Uri "https://api.weather.gov/gridpoints/$Grid/forecast?units=us" `
                           -Headers @{ 'User-Agent' = $ua } -TimeoutSec $TimeoutSec
    $periods = @($r.properties.periods)
    if ($periods.Count -eq 0) { throw 'no forecast periods returned' }

    $windHours = Get-WindHours
    $now = [DateTimeOffset]::Now
    $lines = New-Object System.Collections.Generic.List[string]
    $lines.Add(("{0} forecast  (as of {1})" -f $Place, (Get-Date -Format 't')))
    $shown = 0
    for ($i = 0; $i -lt $periods.Count -and $shown -lt $Days; $i++) {
        $p = $periods[$i]
        # the hours this row speaks for: its calendar day (from now on, for today), or tonight until 6 AM
        $from = ConvertTo-Start $p.startTime
        $day = [DateTimeOffset]::new($from.LocalDateTime.Date, $from.Offset)
        # a night that starts after midnight (an "Overnight" period) ends at 6 AM the same day
        $nightEnd = if ($from.LocalDateTime.Hour -lt 6) { $day.AddHours(6) } else { $day.AddDays(1).AddHours(6) }
        if ($p.isDaytime) { $from = $day; $to = $day.AddDays(1) } else { $to = $nightEnd }
        if ($from -lt $now) { $from = $now.AddMinutes(-$now.Minute).AddSeconds(-$now.Second) }
        $windTxt = Get-WindPeak $windHours $from $to
        # the hours the curtain graphs: from 6 AM (or now) to 6 AM the next morning, or for a night from its start
        $hoursFrom = if ($p.isDaytime -and $from -lt $day.AddHours(6)) { $day.AddHours(6) } else { $from }
        $hoursTo = if ($p.isDaytime) { $day.AddDays(1).AddHours(6) } else { $nightEnd }
        $hoursTxt = Get-DayHours $windHours $hoursFrom $hoursTo
        # the high in the day's hours (even those gone by: the high is the day's), the low in the night after it
        # (tonight, for a night row)
        if ($p.isDaytime) { $timesTxt = Get-TempTimes $windHours $day.AddHours(6) $day.AddHours(21) $day.AddHours(18) $day.AddDays(1).AddHours(10) }
        else { $timesTxt = Get-TempTimes $windHours $null $null $from $day.AddDays(1).AddHours(10) }
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
        if ($null -eq $hi) { $timesTxt = $timesTxt -replace 'hi [^,\]]+(, )?', '' -replace '\[\]', '' }
        $lines.Add(("{0}:  {1}   {2}{3}{4}{5}{6}" -f (Get-DayLabel $p), $temp, $desc, $popTxt, $windTxt, $timesTxt, $hoursTxt))
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
        $dew = ''   # NWS gives the dew point in degrees C, even with units=us
        try {
            if ($null -ne $p.dewpoint.value) {
                $d = [double]$p.dewpoint.value
                if ([string]$p.dewpoint.unitCode -notmatch 'degF') { $d = $d * 9 / 5 + 32 }
                $dew = [int][Math]::Round($d, [MidpointRounding]::AwayFromZero)
            }
        } catch {}
        $lines.Add(("{0}`t{1}`t{2}`t{3}`t{4}`t{5}`t{6}`t{7}" -f $start.ToString('o', $inv), $p.temperature, $pop, $day, (($p.shortForecast -replace '\s+', ' ').Trim()), $hum, $wind, $dew))
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

# ------------------------------------------------------------------ the weather map
# Radar: NOAA's MRMS mosaic (every radar that covers the area, blended and cleaned of ground clutter), about
# every 4 minutes. Clouds: NOAA's GOES satellite pictures, every 5 minutes, visible by day and infrared at
# night. Both through NOAA nowCOAST. Land and water: NASA GIBS's map made from OpenStreetMap, fetched once,
# with the rivers, canals and lakes of the USGS National Hydrography Dataset added (a river delta's channels, the
# reservoirs in the hills), also fetched once. All keyless. The requests carry a box rounded to a tenth of a degree around the forecast grid, so the
# servers learn the area, never the address. The curtain reads the pictures from local\ (map.txt lists them).
$nowcoast = 'https://nowcoast.noaa.gov/geoserver'
$mapNorth = 1.0       # the box: from 0.6 degrees south of the grid to 1.0 north (the curtain shows more of the north)
$mapSouth = 0.6       # and 1.7 either side, 1.6 x 3.4 degrees (about 180 x 300 km at 37 degrees north), more than the
$mapHalfLon = 1.7     # curtain shows; whole tenths only, since map.txt gives the box to one decimal
$mapFile = Join-Path $LocalDir 'map.txt'
$mapNamePattern = '^(radar|sat-vis|sat-ir)-\d{8}T\d{6}Z\.(png|jpg)$'

# The forecast grid's centre, which Update-Hourly writes as "# location lat lon".
function Get-MapCenter {
    if (-not (Test-Path $hourlyOut)) { return $null }
    foreach ($line in (Get-Content -Path $hourlyOut -Encoding UTF8)) {
        if ($line -match '^# location (-?[0-9.]+) (-?[0-9.]+)\s*$') {
            return @([double]::Parse($Matches[1], $inv), [double]::Parse($Matches[2], $inv))
        }
    }
    return $null
}

# How high the sun is (degrees), from NOAA's solar equations: the visible picture is only worth showing by day.
function Get-SunElevation([double]$Lat, [double]$Lon, [datetime]$Utc) {
    $hours = $Utc.TimeOfDay.TotalHours
    $days = if ([DateTime]::IsLeapYear($Utc.Year)) { 366 } else { 365 }
    $g = 2 * [Math]::PI / $days * ($Utc.DayOfYear - 1 + ($hours - 12) / 24)
    $eq = 229.18 * (0.000075 + 0.001868 * [Math]::Cos($g) - 0.032077 * [Math]::Sin($g) - 0.014615 * [Math]::Cos(2 * $g) - 0.040849 * [Math]::Sin(2 * $g))
    $decl = 0.006918 - 0.399912 * [Math]::Cos($g) + 0.070257 * [Math]::Sin($g) - 0.006758 * [Math]::Cos(2 * $g) + 0.000907 * [Math]::Sin(2 * $g) - 0.002697 * [Math]::Cos(3 * $g) + 0.00148 * [Math]::Sin(3 * $g)
    $hourAngle = (($hours * 60 + $eq + 4 * $Lon) / 4 - 180) * [Math]::PI / 180
    $phi = $Lat * [Math]::PI / 180
    $cosZ = [Math]::Sin($phi) * [Math]::Sin($decl) + [Math]::Cos($phi) * [Math]::Cos($decl) * [Math]::Cos($hourAngle)
    return 90 - [Math]::Acos([Math]::Max(-1, [Math]::Min(1, $cosZ))) * 180 / [Math]::PI
}

# The times a nowCOAST layer has pictures for (its own small capabilities document).
function Get-LayerTimes([string]$Workspace, [string]$Layer) {
    $url = "$nowcoast/$Workspace/$Layer/ows?service=WMS&version=1.3.0&request=GetCapabilities"
    $xml = [xml](Invoke-WebRequest -UseBasicParsing -Uri $url -Headers @{ 'User-Agent' = $ua } -TimeoutSec $TimeoutSec).Content
    $ns = New-Object Xml.XmlNamespaceManager $xml.NameTable
    $ns.AddNamespace('w', 'http://www.opengis.net/wms')
    $dim = $xml.SelectSingleNode("//w:Layer/w:Dimension[@name='time']", $ns)
    if (-not $dim) { throw "no picture times listed for $Layer" }
    return @($dim.InnerText.Split(',') | ForEach-Object { $_.Trim() } | Where-Object { $_ })
}

# The pictures of the last SpanMinutes, one per EveryMinutes slot of the clock (the newest in each), oldest
# first. Fixed slots rather than spacing counted back from the newest picture, so a refresh downloads only
# the new picture and the ones already here stay valid.
function Select-Frames([string[]]$Times, [int]$EveryMinutes, [int]$SpanMinutes) {
    $all = @($Times | ForEach-Object { [pscustomobject]@{ Text = $_; Utc = [DateTimeOffset]::Parse($_, $inv).UtcDateTime } } | Sort-Object Utc -Descending)
    if ($all.Count -eq 0) { return @() }
    $latest = $all[0].Utc
    $epoch = New-Object DateTime 2000, 1, 1, 0, 0, 0, ([DateTimeKind]::Utc)
    $slots = @{}
    $picked = New-Object System.Collections.Generic.List[object]
    foreach ($t in $all) {   # newest first, so the first picture met in a slot is that slot's newest
        if (($latest - $t.Utc).TotalMinutes -gt $SpanMinutes) { break }
        $slot = [Math]::Floor(($t.Utc - $epoch).TotalMinutes / $EveryMinutes)
        if ($slots.ContainsKey($slot)) { continue }
        $slots[$slot] = $true
        $picked.Add($t)
    }
    $list = $picked.ToArray()
    [array]::Reverse($list)
    return $list
}

function Get-MapUrl([string]$Service, [string]$Layer, [double[]]$Box, [int]$Width, [int]$Height, [string]$Format, [string]$Time) {
    $bbox = ($Box | ForEach-Object { $_.ToString('0.###', $inv) }) -join ','   # WMS 1.3.0 in EPSG:4326: south,west,north,east
    $url = "$Service`?SERVICE=WMS&VERSION=1.3.0&REQUEST=GetMap&LAYERS=$Layer&STYLES=&CRS=EPSG:4326&BBOX=$bbox&WIDTH=$Width&HEIGHT=$Height&FORMAT=$Format"
    if ($Format -eq 'image/png') { $url += '&TRANSPARENT=true' }
    if ($Time) { $url += '&TIME=' + [uri]::EscapeDataString($Time) }
    return $url
}

# Marsh: the hydrography's swamps and marshes and its inundation areas (seasonal wetlands, flood basins), as a
# mask on the same grid; the curtain draws it as rows of short dashes, like a topographic print.
function Get-MarshUrl([double[]]$Box, [int]$Width, [int]$Height, [int]$Dpi = 48) {
    $fill = '{"type":"simple","symbol":{"type":"esriSFS","style":"esriSFSSolid","color":[0,255,0,255],"outline":{"type":"esriSLS","style":"esriSLSNull","color":[0,0,0,0],"width":0}}}'
    $layers = '[{"id":10,"source":{"type":"mapLayer","mapLayerId":10},"definitionExpression":"FTYPE = ''SwampMarsh''","drawingInfo":{"renderer":' + $fill +
              ',"showLabels":false},"minScale":0,"maxScale":0},{"id":7,"source":{"type":"mapLayer","mapLayerId":7},"definitionExpression":"FTYPE = ''Inundation Area''","drawingInfo":{"renderer":' + $fill +
              ',"showLabels":false},"minScale":0,"maxScale":0}]'
    return Get-HydroUrl $Box $Width $Height $layers $Dpi
}

# Creeks: the hydrography's streams and ditches from Strahler order 2 up (order 1, every gully, would be a
# scribble), one pixel wide, the order in the red channel (2, 3, 4 and up), so bigger streams draw stronger,
# and the green channel full for water that flows all year (perennial streams, canals and ditches) and empty
# for seasonal creeks (intermittent, ephemeral or not known), which the curtain draws less blue. The year-round
# layer comes first in the list, so it draws on top where the two meet.
function Get-StreamsUrl([double[]]$Box, [int]$Width, [int]$Height, [int]$Dpi = 48) {
    $layer = {
        param($id, $g, $where)
        $line = { param($r) '{"type":"esriSLS","style":"esriSLSSolid","color":[' + $r + ',' + $g + ',255,255],"width":1.5}' }
        $renderer = '{"type":"classBreaks","field":"StreamOrde","minValue":2,"classBreakInfos":[{"classMaxValue":2,"symbol":' + (& $line 85) +
                    '},{"classMaxValue":3,"symbol":' + (& $line 170) + '},{"classMaxValue":99,"symbol":' + (& $line 255) + '}]}'
        '{"id":' + $id + ',"source":{"type":"mapLayer","mapLayerId":4},"definitionExpression":"' + $where + ' AND StreamOrde >= 2","drawingInfo":{"renderer":' +
            $renderer + ',"showLabels":false},"minScale":0,"maxScale":0}'
    }
    $yearRound = & $layer 101 255 "(FCODE = 46006 OR FTYPE = 'CanalDitch')"
    $seasonal = & $layer 102 0 "FTYPE = 'StreamRiver' AND FCODE <> 46006"
    return Get-HydroUrl $Box $Width $Height ('[' + $yearRound + ',' + $seasonal + ']') $Dpi
}

function Get-HydroUrl([double[]]$Box, [int]$Width, [int]$Height, [string]$Layers, [int]$Dpi = 48) {
    $bbox = (@($Box[1], $Box[0], $Box[3], $Box[2]) | ForEach-Object { $_.ToString('0.###', $inv) }) -join ','   # west,south,east,north
    return 'https://hydro.nationalmap.gov/arcgis/rest/services/nhd/MapServer/export?bbox=' + $bbox + '&bboxSR=4326&imageSR=4326' +
           "&size=$Width,$Height&dpi=$Dpi&format=png32&transparent=true&dynamicLayers=" + [uri]::EscapeDataString($Layers) + '&f=image'
}

# The same water from the high-resolution hydrography (its large-scale layers, where FTYPE is a number):
# reservoirs the 1:100,000 data lacks (some newer or smaller ones) and a river delta's narrow sloughs in
# full. The server draws those layers only at close scales, and works out the scale from the picture's dpi,
# so it is asked at 4 dpi: the same pixels, drawn as if close up (plain fills do not depend on dpi). The
# curtain adds it to the other water, so each source only ever adds what the other lacks (2026-11-21).
function Get-WaterFineUrl([double[]]$Box, [int]$Width, [int]$Height) {
    $fill = '{"type":"simple","symbol":{"type":"esriSFS","style":"esriSFSSolid","color":[0,0,255,255],"outline":{"type":"esriSLS","style":"esriSLSNull","color":[0,0,0,0],"width":0}}}'
    $area = 'FTYPE IN (460, 312, 445, 336, 398, 461, 537)'   # streams and rivers, bays, sea, canals, locks, submerged streams, braided channels
    $body = 'FTYPE IN (390, 436, 493) AND AREASQKM >= 0.05'  # lakes and ponds, reservoirs, estuaries
    $layers = '[{"id":9,"source":{"type":"mapLayer","mapLayerId":9},"definitionExpression":"' + $area + '","drawingInfo":{"renderer":' + $fill +
              ',"showLabels":false},"minScale":0,"maxScale":0},{"id":12,"source":{"type":"mapLayer","mapLayerId":12},"definitionExpression":"' + $body +
              '","drawingInfo":{"renderer":' + $fill + ',"showLabels":false},"minScale":0,"maxScale":0}]'
    return Get-HydroUrl $Box $Width $Height $layers 4
}

# The USGS hydrography service's export as a water mask: rivers, bays, canals, lakes, reservoirs and estuaries
# in plain blue, without the outlines and names of its own style and without marsh, at any scale (its small-
# scale layers would otherwise switch off at this size). Same grid as the land and water map.
function Get-WaterUrl([double[]]$Box, [int]$Width, [int]$Height, [int]$Dpi = 48) {
    $fill = '{"type":"simple","symbol":{"type":"esriSFS","style":"esriSFSSolid","color":[0,0,255,255],"outline":{"type":"esriSLS","style":"esriSLSNull","color":[0,0,0,0],"width":0}}}'
    $area = "FTYPE IN ('StreamRiver','BayInlet','SeaOcean','CanalDitch','Lock Chamber','Submerged Stream')"
    $body = "FTYPE IN ('LakePond','Reservoir','Estuary') AND AREASQKM >= 0.05"   # stock ponds too (0.2 km2 until 2026-11-21), not specks
    $layers = '[{"id":7,"source":{"type":"mapLayer","mapLayerId":7},"definitionExpression":"' + $area + '","drawingInfo":{"renderer":' + $fill +
              ',"showLabels":false},"minScale":0,"maxScale":0},{"id":10,"source":{"type":"mapLayer","mapLayerId":10},"definitionExpression":"' + $body +
              '","drawingInfo":{"renderer":' + $fill + ',"showLabels":false},"minScale":0,"maxScale":0}]'
    $bbox = (@($Box[1], $Box[0], $Box[3], $Box[2]) | ForEach-Object { $_.ToString('0.###', $inv) }) -join ','   # west,south,east,north
    return 'https://hydro.nationalmap.gov/arcgis/rest/services/nhd/MapServer/export?bbox=' + $bbox + '&bboxSR=4326&imageSR=4326' +
           "&size=$Width,$Height&dpi=$Dpi&format=png32&transparent=true&dynamicLayers=" + [uri]::EscapeDataString($layers) + '&f=image'
}

# Downloads a picture to a temporary name, checks it is one, then moves it into place.
function Save-MapImage([string]$Url, [string]$Path, [int]$WaitSec = 30) {
    $tmp = "$Path.part"
    try {
        $r = Invoke-WebRequest -UseBasicParsing -Uri $Url -Headers @{ 'User-Agent' = $ua } -TimeoutSec $WaitSec -OutFile $tmp -PassThru
        $type = [string]$r.Headers['Content-Type']
        if ($type -notlike 'image/*') { throw "the server sent $type, not a picture" }
        Move-Item -Path $tmp -Destination $Path -Force
    } finally {
        if (Test-Path $tmp) { Remove-Item -Path $tmp -Force -ErrorAction SilentlyContinue }
    }
}

# What the current map.txt lists for one kind of line ("radar", "sat", "dem", "station", "tidestation",
# "tide"), to keep it when a refresh fails. Picture lines only while their file is still here.
function Get-MapEntries([string]$Kind) {
    $list = New-Object System.Collections.Generic.List[string]
    if (Test-Path $mapFile) {
        foreach ($line in (Get-Content -Path $mapFile -Encoding UTF8)) {
            if (-not $line.StartsWith("$Kind ")) { continue }
            if ($Kind -eq 'radar' -or $Kind -eq 'sat') {
                if (-not (Test-Path (Join-Path $LocalDir (($line -split '\s+')[-1])))) { continue }
            }
            $list.Add($line)
        }
    }
    return $list.ToArray()
}

# Weather stations: the latest report (METAR) from every airport weather station in the box, from NOAA's
# Aviation Weather Center, at most every 15 minutes. Temperature (F) and wind (mph) for the map's labels.
$script:stationLines = $null
$script:stationAt = [DateTime]::MinValue

function Get-StationLines([double[]]$Box) {
    if ($null -ne $script:stationLines -and ((Get-Date) - $script:stationAt).TotalMinutes -lt 15) { return $script:stationLines }
    $bbox = ($Box | ForEach-Object { $_.ToString('0.###', $inv) }) -join ','   # south,west,north,east
    $reports = Invoke-RestMethod -Uri "https://aviationweather.gov/api/data/metar?bbox=$bbox&format=json" -Headers @{ 'User-Agent' = $ua } -TimeoutSec $TimeoutSec
    $lines = New-Object System.Collections.Generic.List[string]
    foreach ($m in @($reports)) {
        $id = [string]$m.icaoId
        if ($id -notmatch '^[A-Z0-9]{3,6}$' -or $null -eq $m.lat -or $null -eq $m.lon -or -not $m.reportTime) { continue }
        $temp = '-'; if ($null -ne $m.temp -and "$($m.temp)" -ne '') { $temp = ([Math]::Round([double]$m.temp * 9 / 5 + 32)).ToString($inv) }
        $from = '-'; if ($null -ne $m.wdir -and -not ($m.wdir -is [string])) { $from = ([int]$m.wdir).ToString($inv) }   # "VRB": variable
        $speed = '-'; if ($null -ne $m.wspd) { $speed = ([Math]::Round([double]$m.wspd * 1.15078)).ToString($inv) }   # knots to mph
        $gust = '-'; if ($null -ne $m.wgst) { $gust = ([Math]::Round([double]$m.wgst * 1.15078)).ToString($inv) }
        if ($m.reportTime -is [datetime]) { $utc = $m.reportTime.ToUniversalTime() } else { $utc = [DateTimeOffset]::Parse([string]$m.reportTime, $inv).UtcDateTime }
        $lines.Add(('station {0} {1} {2} {3} {4} {5} {6} {7}' -f $id, ([double]$m.lat).ToString('0.####', $inv), ([double]$m.lon).ToString('0.####', $inv),
                    $temp, $from, $speed, $gust, $utc.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", $inv)))
    }
    $script:stationLines = $lines.ToArray()
    $script:stationAt = Get-Date
    return $script:stationLines
}

# Elevation, once per place: AWS Terrain Tiles ("terrarium" PNGs; USGS 3DEP elevation in the US), zoom 10
# (about 120 m a pixel; zoom 9 until 2026-11-17), the 70 or so tiles that cover the box, about 7 MB.
# Returns the map.txt line that lists them.
function Get-TileX([double]$Lon, [int]$Z) { return [int][Math]::Floor(($Lon + 180) / 360 * [Math]::Pow(2, $Z)) }
function Get-TileY([double]$Lat, [int]$Z) {
    $r = $Lat * [Math]::PI / 180
    return [int][Math]::Floor((1 - [Math]::Log([Math]::Tan($r) + 1 / [Math]::Cos($r)) / [Math]::PI) / 2 * [Math]::Pow(2, $Z))
}

function Update-Dem([double[]]$Box, [int]$Zoom = 10) {
    $z = $Zoom
    $x0 = Get-TileX $Box[1] $z; $x1 = Get-TileX $Box[3] $z
    $y0 = Get-TileY $Box[2] $z; $y1 = Get-TileY $Box[0] $z
    $names = @{}
    for ($x = $x0; $x -le $x1; $x++) {
        for ($y = $y0; $y -le $y1; $y++) {
            $name = 'dem-{0}-{1}-{2}.png' -f $z, $x, $y
            $names[$name] = $true
            $path = Join-Path $LocalDir $name
            if (-not (Test-Path $path)) { Save-MapImage "https://s3.amazonaws.com/elevation-tiles-prod/terrarium/$z/$x/$y.png" $path }
        }
    }
    # only this zoom's tiles: the close-up keeps its own, finer ones
    Get-ChildItem -Path $LocalDir -Filter "dem-$z-*.png" | Where-Object { -not $names.ContainsKey($_.Name) } | Remove-Item -Force -ErrorAction SilentlyContinue
    return ('dem {0} {1} {2} {3} {4}' -f $z, $x0, $y0, $x1, $y1)
}

# Streets for the close-up: the Census Bureau's TIGER roads (public domain) as a mask, local streets in the
# green channel and the bigger roads in the red one (state routes 170, freeways 255), drawn after the streets
# so they win where they cross. The service draws its street layer only at close scales and works out the scale
# from the dpi, so it is asked at 24 dpi; the line widths are in points, 3 to a pixel at that dpi: a pixel
# (about 12 m) for a street, two for a state route, three for a freeway.
function Get-RoadsUrl([double[]]$Box, [int]$Width, [int]$Height) {
    $layer = {
        param($id, $r, $g, $width)
        '{"id":' + $id + ',"source":{"type":"mapLayer","mapLayerId":' + $id + '},"drawingInfo":{"renderer":{"type":"simple","symbol":{"type":"esriSLS","style":"esriSLSSolid","color":[' +
            $r + ',' + $g + ',0,255],"width":' + $width + '}},"showLabels":false},"minScale":0,"maxScale":0}'
    }
    $layers = '[' + (& $layer 8 0 255 3) + ',' + (& $layer 6 170 0 6) + ',' + (& $layer 2 255 0 9) + ']'
    $bbox = (@($Box[1], $Box[0], $Box[3], $Box[2]) | ForEach-Object { $_.ToString('0.###', $inv) }) -join ','   # west,south,east,north
    return 'https://tigerweb.geo.census.gov/arcgis/rest/services/TIGERweb/Transportation/MapServer/export?bbox=' + $bbox + '&bboxSR=4326&imageSR=4326' +
           "&size=$Width,$Height&dpi=24&format=png32&transparent=true&dynamicLayers=" + [uri]::EscapeDataString($layers) + '&f=image'
}

# Creeks for the close-up: the high-resolution hydrography's own lines (its large-scale flowline layer, which
# has every local creek but no stream order), at 96 dpi, two pixels wide: perennial streams full in the red
# and green channels; intermittent ones, and canals and ditches (in a town mostly flood channels, dry much of
# the year), full in the red channel only: seasonal, as Get-StreamsUrl encodes them, but at full size (this
# layer has no stream order, and close in every creek reads as a creek). Ephemeral gullies and the paths drawn
# through lakes are left out.
function Get-StreamsFineUrl([double[]]$Box, [int]$Width, [int]$Height) {
    $line = { param($r, $g) '{"type":"simple","symbol":{"type":"esriSLS","style":"esriSLSSolid","color":[' + $r + ',' + $g + ',255,255],"width":1.5}}' }
    $layers = '[{"id":101,"source":{"type":"mapLayer","mapLayerId":6},"definitionExpression":"FCODE = 46006","drawingInfo":{"renderer":' + (& $line 255 255) +
              ',"showLabels":false},"minScale":0,"maxScale":0},{"id":102,"source":{"type":"mapLayer","mapLayerId":6},"definitionExpression":"FCODE = 46003 OR FTYPE = 336","drawingInfo":{"renderer":' +
              (& $line 255 0) + ',"showLabels":false},"minScale":0,"maxScale":0}]'
    return Get-HydroUrl $Box $Width $Height $layers 96
}

# The close-up under the map (2026-11-21): a box 0.2 x 0.4 degrees (about 22 x 35 km) around the place rounded to
# a tenth of a degree, like the big box, so the servers learn the area and not the address; more of it lies west
# of the place, since the close-up shows home 7/10 of the way across (its shape needs up to 0.25 degree west of
# the rounded place at 45 degrees north). Its own ground at 0.000125 degree a pixel (about 12 m): the land and water, the
# hydrography's water, marsh and creeks, streets, and zoom-13 elevation (about 15 m). Fetched once per place,
# best-effort, each part on its own. The weather on it is the big map's own pictures, so the two always show the
# same moment. Returns its map.txt lines. (The first close-up, 11.5 km tall, had a box of 0.3 x 0.7 degrees at
# twice the cell and zoom-12 elevation; the user found it "too zoomed out", so it showed 4.6 km, then "a little
# too zoomed in", so 5.5 km, then 5.75 km, twice as close as the first, and since 7.5 km, the extra all to the
# north: it reaches 2.9 km south of home and 4.6 km north.)
$detailHalfLat = 0.1
$detailWest = 0.25
$detailEast = 0.15
$detailCell = 0.000125
$detailDemZoom = 13
$mapDemZoom = 10

function Update-Detail([double]$CLat, [double]$CLon) {
    [double[]]$dBox = @(($CLat - $detailHalfLat), ($CLon - $detailWest), ($CLat + $detailHalfLat), ($CLon + $detailEast))
    $tag = ($dBox | ForEach-Object { $_.ToString('0.00', $inv) }) -join '_'
    $w = [int][Math]::Round(($detailWest + $detailEast) / $detailCell)
    $h = [int][Math]::Round(2 * $detailHalfLat / $detailCell)
    $lines = New-Object System.Collections.Generic.List[string]
    $lines.Add('detail box ' + (($dBox | ForEach-Object { $_.ToString('0.00', $inv) }) -join ' '))
    foreach ($dk in 'base', 'water', 'waterfine', 'marsh', 'streams', 'roads') {
        $name = 'map-detail-{0}-{1}.png' -f $dk, $tag
        $path = Join-Path $LocalDir $name
        if (-not (Test-Path $path)) {
            try {
                $url = switch ($dk) {
                    'base'      { Get-MapUrl 'https://gibs.earthdata.nasa.gov/wms/epsg4326/best/wms.cgi' 'OSM_Land_Water_Map' $dBox $w $h 'image/png' '' }
                    'water'     { Get-WaterUrl $dBox $w $h 192 }   # 192 dpi: the small-scale layers' own range at this cell
                    'waterfine' { Get-WaterFineUrl $dBox $w $h }
                    'marsh'     { Get-MarshUrl $dBox $w $h 192 }
                    'streams'   { Get-StreamsFineUrl $dBox $w $h }
                    default     { Get-RoadsUrl $dBox $w $h }
                }
                Save-MapImage $url $path 90
            } catch { Write-Host "Close-up $dk skipped: $($_.Exception.Message)" }
        }
        if (Test-Path $path) {
            $lines.Add("detail $dk $name")
            # the same part cut for another box (a new place, or the close-up's earlier shape) is never used again
            Get-ChildItem -Path $LocalDir -Filter "map-detail-$dk-*.png" | Where-Object { $_.Name -ne $name } | Remove-Item -Force -ErrorAction SilentlyContinue
        }
    }
    if (-not ($lines | Where-Object { $_ -like 'detail base *' })) { return @() }   # no ground, no close-up
    try { $lines.Add('detail ' + (Update-Dem $dBox $detailDemZoom)) } catch { Write-Host "Close-up elevation skipped: $($_.Exception.Message)" }
    return $lines.ToArray()
}

# Tides: the nearest NOAA tide station that keeps its own predictions (a "reference" station) within 60 km,
# looked up once per place and remembered in local\tide-station.txt; its highs and lows from yesterday to
# three days ahead, refreshed every 6 hours. Inland, there is none and the map simply shows no tide.
$tideFile = Join-Path $LocalDir 'tide-station.txt'
$script:tideLines = $null
$script:tideAt = [DateTime]::MinValue

function Get-TideStation([double]$Lat, [double]$Lon, [string]$BoxText) {
    if (Test-Path $tideFile) {
        $saved = [string](Get-Content -Path $tideFile -Encoding UTF8 | Select-Object -First 1)
        $prefix = "box $BoxText "
        if ($saved.StartsWith($prefix)) {
            $rest = $saved.Substring($prefix.Length) -split '\s+'
            if ($rest[0] -eq 'none') { return $null }
            if ($rest.Count -eq 4 -and $rest[0] -eq 'station') { return @($rest[1], $rest[2], $rest[3]) }
        }
    }
    $json = (Invoke-WebRequest -UseBasicParsing -Uri 'https://api.tidesandcurrents.noaa.gov/mdapi/prod/webapi/stations.json?type=tidepredictions' -Headers @{ 'User-Agent' = $ua } -TimeoutSec 90).Content
    Add-Type -AssemblyName System.Web.Extensions
    $parser = New-Object System.Web.Script.Serialization.JavaScriptSerializer
    $parser.MaxJsonLength = 64MB   # the list is about 2 MB, past ConvertFrom-Json's limit
    $k = 111.2 * [Math]::Cos($Lat * [Math]::PI / 180)
    $best = $null; $bestKm = 60.0
    foreach ($s in $parser.DeserializeObject($json)['stations']) {
        if ([string]$s['type'] -ne 'R') { continue }
        $dx = ([double]$s['lng'] - $Lon) * $k; $dy = ([double]$s['lat'] - $Lat) * 111.2
        $km = [Math]::Sqrt($dx * $dx + $dy * $dy)
        if ($km -lt $bestKm) { $best = $s; $bestKm = $km }
    }
    if ($null -eq $best -or [string]$best['id'] -notmatch '^[A-Za-z0-9]{3,12}$') { Write-Atomic $tideFile @("box $BoxText none"); return $null }
    $found = @([string]$best['id'], ([double]$best['lat']).ToString('0.####', $inv), ([double]$best['lng']).ToString('0.####', $inv))
    Write-Atomic $tideFile @(('box {0} station {1} {2} {3}' -f $BoxText, $found[0], $found[1], $found[2]))
    return $found
}

function Get-TideLines([double]$Lat, [double]$Lon, [string]$BoxText) {
    if ($null -ne $script:tideLines -and ((Get-Date) - $script:tideAt).TotalHours -lt 6) { return $script:tideLines }
    $station = Get-TideStation $Lat $Lon $BoxText
    $lines = New-Object System.Collections.Generic.List[string]
    if ($station) {
        $begin = [DateTime]::UtcNow.AddDays(-1).ToString('yyyyMMdd', $inv)
        $url = "https://api.tidesandcurrents.noaa.gov/api/prod/datagetter?begin_date=$begin&range=96&station=$($station[0])&product=predictions&datum=MLLW&time_zone=gmt&interval=hilo&units=english&application=PrivacyCurtain&format=json"
        $r = Invoke-RestMethod -Uri $url -Headers @{ 'User-Agent' = $ua } -TimeoutSec $TimeoutSec
        $lines.Add(('tidestation {0} {1} {2}' -f $station[0], $station[1], $station[2]))
        foreach ($p in @($r.predictions)) {
            if ($p.type -ne 'H' -and $p.type -ne 'L') { continue }
            $t = [DateTime]::ParseExact([string]$p.t, 'yyyy-MM-dd HH:mm', $inv, [Globalization.DateTimeStyles]::AssumeUniversal -bor [Globalization.DateTimeStyles]::AdjustToUniversal)
            $lines.Add(('tide {0} {1} {2}' -f $t.ToString("yyyy-MM-dd'T'HH:mm':00Z'", $inv), $p.type, ([double]$p.v).ToString('0.00', $inv)))
        }
        if ($lines.Count -lt 3) { throw 'no tide predictions returned' }
    }
    $script:tideLines = $lines.ToArray()
    $script:tideAt = Get-Date
    return $script:tideLines
}

# The pictures of the last hour for one layer, downloading the ones not here yet. Returns index lines.
function Update-Frames([string]$Prefix, [string]$Kind, [string]$Workspace, [string]$Layer, [int]$Every, [int]$Span,
                       [double[]]$Box, [int]$Width, [int]$Height, [string]$Format, [string]$Ext) {
    $lines = New-Object System.Collections.Generic.List[string]
    foreach ($f in (Select-Frames (Get-LayerTimes $Workspace $Layer) $Every $Span)) {
        $name = '{0}-{1}.{2}' -f $Prefix, $f.Utc.ToString("yyyyMMdd'T'HHmmss'Z'", $inv), $Ext
        $path = Join-Path $LocalDir $name
        if (-not (Test-Path $path)) {
            try { Save-MapImage (Get-MapUrl "$nowcoast/$Workspace/wms" $Layer $Box $Width $Height $Format $f.Text) $path }
            catch { Write-Host "Map picture skipped ($name): $($_.Exception.Message)"; continue }
        }
        $lines.Add(('{0} {1} {2}' -f $Kind, $f.Utc.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", $inv), $name))
    }
    return $lines.ToArray()
}

# ------------------------------------------------------------------ warnings and earthquakes
# The National Weather Service's active alerts for the state the forecast grid is in (warnings in coral on the
# map, watches and advisories in amber): surf, wind, air quality, flood, fire, tornado. An alert names forecast
# zones; their outlines come from the same API, kept in local\zone-<id>.txt (a zone's outline seldom changes;
# fetched again after 30 days), thinned to points about 400 m apart. Only alerts that reach the map's box,
# widened by a degree, are written. Outlooks and statements are left out. Lines in map.txt:
#   alert N LEVEL ENDS_UTC|- EVENT      (LEVEL 2 = warning, 1 = watch or advisory)
#   alertring N lat,lon lat,lon ...
function Get-Rings($Geometry) {
    $rings = @()
    if ($null -eq $Geometry) { return ,$rings }
    $polys = if ($Geometry.type -eq 'Polygon') { @(,$Geometry.coordinates) } elseif ($Geometry.type -eq 'MultiPolygon') { @($Geometry.coordinates) } else { @() }
    foreach ($poly in $polys) {
        $outer = @($poly)[0]
        $pts = New-Object System.Collections.Generic.List[string]
        $lastLat = 999.0; $lastLon = 999.0
        foreach ($c in @($outer)) {
            $lon = [double]$c[0]; $lat = [double]$c[1]
            if ([Math]::Abs($lat - $lastLat) + [Math]::Abs($lon - $lastLon) -lt 0.004) { continue }
            $pts.Add(('{0},{1}' -f $lat.ToString('0.####', $inv), $lon.ToString('0.####', $inv)))
            $lastLat = $lat; $lastLon = $lon
        }
        if ($pts.Count -ge 3) { $rings += ,($pts -join ' ') }
    }
    return ,$rings
}

function Get-ZoneRings([string]$Url) {
    $id = ($Url -split '/')[-1]
    if ($id -notmatch '^[A-Z]{2}[CZ]\d{3}$') { return @() }
    $file = Join-Path $LocalDir "zone-$id.txt"
    if ((Test-Path $file) -and ((Get-Date) - (Get-Item $file).LastWriteTime).TotalDays -lt 30) { return @(Get-Content $file) }
    $z = Invoke-RestMethod -Uri $Url -Headers @{ 'User-Agent' = $ua; 'Accept' = 'application/geo+json' } -TimeoutSec $TimeoutSec
    $rings = Get-Rings $z.geometry
    if ($rings.Count -gt 0) { Write-Atomic $file $rings }
    return $rings
}

function Get-AlertLines([double[]]$Box, [double]$Lat, [double]$Lon) {
    $areaFile = Join-Path $LocalDir 'alert-area.txt'
    $area = if (Test-Path $areaFile) { (Get-Content $areaFile -TotalCount 1).Trim() } else { '' }
    if ($area -notmatch '^[A-Z]{2}$') {
        $pt = Invoke-RestMethod -Uri ('https://api.weather.gov/points/{0},{1}' -f $Lat.ToString('0.0###', $inv), $Lon.ToString('0.0###', $inv)) `
                                -Headers @{ 'User-Agent' = $ua } -TimeoutSec $TimeoutSec
        $area = [string]$pt.properties.relativeLocation.properties.state
        if ($area -match '^[A-Z]{2}$') { Write-Atomic $areaFile @($area) }
    }
    if ($area -notmatch '^[A-Z]{2}$') { return @() }
    $r = Invoke-RestMethod -Uri "https://api.weather.gov/alerts/active?area=$area" -Headers @{ 'User-Agent' = $ua; 'Accept' = 'application/geo+json' } -TimeoutSec $TimeoutSec
    $s = $Box[0] - 1; $w = $Box[1] - 1; $n = $Box[2] + 1; $e = $Box[3] + 1
    $lines = New-Object System.Collections.Generic.List[string]
    $k = 0
    foreach ($f in @($r.features)) {
        $p = $f.properties
        $event = ([string]$p.event -replace '\s+', ' ').Trim()
        if ($p.status -ne 'Actual' -or $p.messageType -eq 'Cancel' -or -not $event) { continue }
        if ($event -match 'Statement|Outlook|Short Term Forecast|Test|Message') { continue }
        $rings = if ($f.geometry) { Get-Rings $f.geometry } else { @(@($p.affectedZones) | ForEach-Object { try { Get-ZoneRings $_ } catch { } }) }
        $near = @($rings | Where-Object {
            $in = $false
            foreach ($pair in ($_ -split ' ')) { $ll = $pair -split ','; $a = [double]$ll[0]; $o = [double]$ll[1]; if ($a -gt $s -and $a -lt $n -and $o -gt $w -and $o -lt $e) { $in = $true; break } }
            $in })
        if ($near.Count -eq 0) { continue }
        $level = if ($event -match 'Warning$') { 2 } else { 1 }
        $ends = $p.ends; if (-not $ends) { $ends = $p.expires }
        $endsTxt = if ($ends) { (ConvertTo-Start $ends).UtcDateTime.ToString('yyyy-MM-ddTHH:mm:ssZ', $inv) } else { '-' }
        $k++
        $lines.Add(('alert {0} {1} {2} {3}' -f $k, $level, $endsTxt, $event))
        foreach ($ring in $near) { $lines.Add(('alertring {0} {1}' -f $k, $ring)) }
    }
    return $lines
}

# Earthquakes of magnitude 2.5 and up over the last three days from the USGS feed, within about 3 degrees of
# the map's box (the curtain draws those on the map and lists strong ones nearby). quake UTC LAT LON MAG
function Get-QuakeLines([double[]]$Box) {
    $q = Invoke-RestMethod -Uri 'https://earthquake.usgs.gov/earthquakes/feed/v1.0/summary/2.5_week.geojson' -Headers @{ 'User-Agent' = $ua } -TimeoutSec $TimeoutSec
    $since = [DateTimeOffset]::UtcNow.AddDays(-3)
    $lines = New-Object System.Collections.Generic.List[string]
    foreach ($f in @($q.features)) {
        $c = $f.geometry.coordinates
        $lat = [double]$c[1]; $lon = [double]$c[0]
        if ($lat -lt $Box[0] - 3 -or $lat -gt $Box[2] + 3 -or $lon -lt $Box[1] - 3 -or $lon -gt $Box[3] + 3) { continue }
        $t = [DateTimeOffset]::FromUnixTimeMilliseconds([long]$f.properties.time)
        if ($t -lt $since) { continue }
        $lines.Add(('quake {0} {1} {2} {3}' -f $t.UtcDateTime.ToString('yyyy-MM-ddTHH:mm:ssZ', $inv), $lat.ToString('0.###', $inv), $lon.ToString('0.###', $inv),
                    ([double]$f.properties.mag).ToString('0.0', $inv)))
    }
    return $lines
}

function Update-Map {
    try { [Net.ServicePointManager]::SecurityProtocol = [Net.ServicePointManager]::SecurityProtocol -bor [Net.SecurityProtocolType]::Tls12 } catch {}
    $center = Get-MapCenter
    if (-not $center) { Write-Host 'Map skipped: no location yet (it comes with the hourly forecast).'; return }
    $cLat = [Math]::Round($center[0], 1)
    $cLon = [Math]::Round($center[1], 1)
    [double[]]$box = @(($cLat - $mapSouth), ($cLon - $mapHalfLon), ($cLat + $mapNorth), ($cLon + $mapHalfLon))
    $boxText = ($box | ForEach-Object { $_.ToString('0.0', $inv) }) -join ' '
    $lonSpan = 2 * $mapHalfLon
    $latSpan = $mapNorth + $mapSouth

    # Land and water, once per place.
    $baseName = 'map-base-{0}.png' -f (($box | ForEach-Object { $_.ToString('0.0', $inv) }) -join '_')
    $basePath = Join-Path $LocalDir $baseName
    $baseW = [int][Math]::Round($lonSpan / 0.0015)
    $baseH = [int][Math]::Round($latSpan / 0.0015)
    if (-not (Test-Path $basePath)) {
        try {
            Save-MapImage (Get-MapUrl 'https://gibs.earthdata.nasa.gov/wms/epsg4326/best/wms.cgi' 'OSM_Land_Water_Map' $box $baseW $baseH 'image/png' '') $basePath
        } catch {
            Write-Host "Map skipped: the land and water map did not download ($($_.Exception.Message))."
            return
        }
        Get-ChildItem -Path $LocalDir -Filter 'map-base-*.png' | Where-Object { $_.Name -ne $baseName } | Remove-Item -Force -ErrorAction SilentlyContinue
        # A new box (a new place, or a new shape): radar and cloud pictures cut for the old one would sit in the
        # wrong place, and they are kept by time alone, so they all go and are fetched again for this box.
        Get-ChildItem -Path $LocalDir -File | Where-Object { $_.Name -match $mapNamePattern } | Remove-Item -Force -ErrorAction SilentlyContinue
    }

    # Rivers, canals and lakes, once per place, on the same grid; best-effort (without them the map is as before).
    $waterName = 'map-water-v2-{0}.png' -f (($box | ForEach-Object { $_.ToString('0.0', $inv) }) -join '_')   # v2: stock ponds too
    $waterPath = Join-Path $LocalDir $waterName
    if (-not (Test-Path $waterPath)) {
        try {
            Save-MapImage (Get-WaterUrl $box $baseW $baseH) $waterPath 90   # drawn on request, so it can take a while
            Get-ChildItem -Path $LocalDir -Filter 'map-water-*.png' | Where-Object { $_.Name -ne $waterName } | Remove-Item -Force -ErrorAction SilentlyContinue
        } catch { Write-Host "Rivers and lakes skipped: $($_.Exception.Message)" }
    }

    # Marsh and creeks, once per place, on the same grid; best-effort too.
    $hydro = @{}
    foreach ($hk in 'waterfine', 'marsh', 'streams') {
        $version = if ($hk -eq 'streams') { 'v2-' } else { '' }   # v2: year-round streams in the green channel
        $name = 'map-{0}-{1}{2}.png' -f $hk, $version, (($box | ForEach-Object { $_.ToString('0.0', $inv) }) -join '_')
        $path = Join-Path $LocalDir $name
        if (-not (Test-Path $path)) {
            try {
                $url = switch ($hk) { 'waterfine' { Get-WaterFineUrl $box $baseW $baseH } 'marsh' { Get-MarshUrl $box $baseW $baseH } default { Get-StreamsUrl $box $baseW $baseH } }
                Save-MapImage $url $path 90
                Get-ChildItem -Path $LocalDir -Filter "map-$hk-*.png" | Where-Object { $_.Name -ne $name } | Remove-Item -Force -ErrorAction SilentlyContinue
            } catch { Write-Host "Map $hk skipped: $($_.Exception.Message)" }
        }
        if (Test-Path $path) { $hydro[$hk] = $name }
    }

    # Elevation for the relief, once per place.
    try { $dem = @(Update-Dem $box $mapDemZoom) }
    catch { Write-Host "Elevation skipped: $($_.Exception.Message)"; $dem = Get-MapEntries 'dem' }

    # Radar: one picture every 8 minutes over the last hour, at the mosaic's own 0.01-degree grid.
    try { $radar = Update-Frames 'radar' 'radar' 'weather_radar' 'conus_base_reflectivity_mosaic' 8 56 $box ([int][Math]::Round($lonSpan / 0.01)) ([int][Math]::Round($latSpan / 0.01)) 'image/png' 'png' }
    catch { Write-Host "Radar refresh skipped: $($_.Exception.Message)"; $radar = Get-MapEntries 'radar' }

    # Clouds: visible while the sun is well up, infrared otherwise; one picture every 10 minutes.
    $kind = if ((Get-SunElevation $cLat $cLon ([DateTime]::UtcNow)) -ge 8) { 'vis' } else { 'ir' }
    $layer = if ($kind -eq 'vis') { 'goes_visible_imagery' } else { 'goes_longwave_imagery' }
    try { $sat = Update-Frames "sat-$kind" "sat $kind" 'satellite' $layer 10 60 $box ([int][Math]::Round($lonSpan / 0.004)) ([int][Math]::Round($latSpan / 0.004)) 'image/jpeg' 'jpg' }
    catch { Write-Host "Satellite refresh skipped: $($_.Exception.Message)"; $sat = Get-MapEntries 'sat' }

    # Station temperatures and wind; the tide.
    try { $stations = Get-StationLines $box }
    catch { Write-Host "Station reports skipped: $($_.Exception.Message)"; $stations = Get-MapEntries 'station' }
    try { $alerts = Get-AlertLines $box $center[0] $center[1] }
    catch { Write-Host "Alerts skipped: $($_.Exception.Message)"; $alerts = @(Get-MapEntries 'alert') + @(Get-MapEntries 'alertring') }
    try { $quakes = Get-QuakeLines $box }
    catch { Write-Host "Earthquakes skipped: $($_.Exception.Message)"; $quakes = Get-MapEntries 'quake' }
    try { $tides = Get-TideLines $center[0] $center[1] $boxText }
    catch { Write-Host "Tides skipped: $($_.Exception.Message)"; $tides = @(Get-MapEntries 'tidestation') + @(Get-MapEntries 'tide') }
    try { $detail = @(Update-Detail $cLat $cLon) }
    catch { Write-Host "Close-up skipped: $($_.Exception.Message)"; $detail = Get-MapEntries 'detail' }
    # elevation tiles of a zoom neither map uses any more (the first close-up's zoom 12)
    Get-ChildItem -Path $LocalDir -Filter 'dem-*.png' | Where-Object { $_.Name -notmatch "^dem-($mapDemZoom|$detailDemZoom)-" } |
        Remove-Item -Force -ErrorAction SilentlyContinue

    $lines = New-Object System.Collections.Generic.List[string]
    $lines.Add(('# weather map for the curtain, as of {0}' -f (Get-Date -Format 't')))
    $lines.Add("box $boxText")
    $lines.Add("base $baseName")
    if (Test-Path $waterPath) { $lines.Add("water $waterName") }
    foreach ($hk in 'waterfine', 'marsh', 'streams') { if ($hydro[$hk]) { $lines.Add("$hk $($hydro[$hk])") } }
    foreach ($l in @($dem) + @($detail) + @($radar) + @($sat) + @($stations) + @($tides) + @($alerts) + @($quakes)) { if ($l) { $lines.Add($l) } }
    $census = if (@($detail) -match '^detail roads ') { '  {0}  Census' -f [char]0x00B7 } else { '' }
    $lines.Add(('credit NOAA  {0}  USGS{2}  {0}  {1} OpenStreetMap' -f [char]0x00B7, [char]0x00A9, $census))
    Write-Atomic $mapFile $lines

    # Only now that map.txt no longer lists them: remove pictures that dropped out of the last hour.
    $keep = @{}
    foreach ($l in $lines) { $keep[($l -split '\s+')[-1]] = $true }
    Get-ChildItem -Path $LocalDir -File | Where-Object { $_.Name -match $mapNamePattern -and -not $keep.ContainsKey($_.Name) } |
        Remove-Item -Force -ErrorAction SilentlyContinue
    $tideNote = if (@($tides).Count -gt 1) { 'tide station ' + ((@($tides)[0] -split '\s+')[1]) } else { 'no tide station near' }
    Write-Host ("Wrote {0} ({1} radar, {2} {3} satellite picture(s), {4} station report(s), {5}, relief {6})." -f $mapFile, @($radar).Count, @($sat).Count, $kind,
                @($stations).Count, $tideNote, $(if (@($dem).Count -gt 0) { 'yes' } else { 'no' }))
}

function Test-CurtainAlive {
    if ($CurtainPid -le 0) { return $false }
    return [bool](Get-Process -Id $CurtainPid -ErrorAction SilentlyContinue)
}

if (-not $Watch) {
    $ok = Update-Forecast
    if (-not $NoMap) { try { Update-Map } catch { Write-Host "Map skipped: $($_.Exception.Message)" } }
    if ($ok) { exit 0 } else { exit 1 }
}

# Watch: the forecast now and every RefreshMinutes, the map now and every MapMinutes, while the curtain is up;
# leave when it closes.
$nextForecast = Get-Date
$nextMap = Get-Date
while ($true) {
    if ((Get-Date) -ge $nextForecast) {
        [void](Update-Forecast)
        $nextForecast = (Get-Date).AddMinutes([Math]::Max(5, $RefreshMinutes))
    }
    if (-not $NoMap -and (Get-Date) -ge $nextMap) {
        try { Update-Map } catch { Write-Host "Map skipped: $($_.Exception.Message)" }
        $nextMap = (Get-Date).AddMinutes([Math]::Max(2, $MapMinutes))
    }
    if (-not (Test-CurtainAlive)) { exit 0 }
    Start-Sleep -Seconds 15
}
