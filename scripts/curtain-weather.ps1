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

# ------------------------------------------------------------------ the weather map
# Radar: NOAA's MRMS mosaic (every radar that covers the area, blended and cleaned of ground clutter), about
# every 4 minutes. Clouds: NOAA's GOES satellite pictures, every 5 minutes, visible by day and infrared at
# night. Both through NOAA nowCOAST. Land and water: NASA GIBS's map made from OpenStreetMap, fetched once.
# All keyless. The requests carry a box rounded to a tenth of a degree around the forecast grid, so the
# servers learn the area, never the address. The curtain reads the pictures from local\ (map.txt lists them).
$nowcoast = 'https://nowcoast.noaa.gov/geoserver'
$mapHalfLat = 0.6     # the box: 1.2 x 3 degrees (about 130 x 255 km at 40 degrees north), wider than the curtain shows
$mapHalfLon = 1.5
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

# Downloads a picture to a temporary name, checks it is one, then moves it into place.
function Save-MapImage([string]$Url, [string]$Path) {
    $tmp = "$Path.part"
    try {
        $r = Invoke-WebRequest -UseBasicParsing -Uri $Url -Headers @{ 'User-Agent' = $ua } -TimeoutSec 30 -OutFile $tmp -PassThru
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

# Elevation, once per place: AWS Terrain Tiles ("terrarium" PNGs; USGS 3DEP elevation in the US), zoom 9,
# the 15 or so tiles that cover the box, about 1.6 MB. Returns the map.txt line that lists them.
function Get-TileX([double]$Lon, [int]$Z) { return [int][Math]::Floor(($Lon + 180) / 360 * [Math]::Pow(2, $Z)) }
function Get-TileY([double]$Lat, [int]$Z) {
    $r = $Lat * [Math]::PI / 180
    return [int][Math]::Floor((1 - [Math]::Log([Math]::Tan($r) + 1 / [Math]::Cos($r)) / [Math]::PI) / 2 * [Math]::Pow(2, $Z))
}

function Update-Dem([double[]]$Box) {
    $z = 9
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
    Get-ChildItem -Path $LocalDir -Filter 'dem-*.png' | Where-Object { -not $names.ContainsKey($_.Name) } | Remove-Item -Force -ErrorAction SilentlyContinue
    return ('dem {0} {1} {2} {3} {4}' -f $z, $x0, $y0, $x1, $y1)
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

function Update-Map {
    try { [Net.ServicePointManager]::SecurityProtocol = [Net.ServicePointManager]::SecurityProtocol -bor [Net.SecurityProtocolType]::Tls12 } catch {}
    $center = Get-MapCenter
    if (-not $center) { Write-Host 'Map skipped: no location yet (it comes with the hourly forecast).'; return }
    $cLat = [Math]::Round($center[0], 1)
    $cLon = [Math]::Round($center[1], 1)
    [double[]]$box = @(($cLat - $mapHalfLat), ($cLon - $mapHalfLon), ($cLat + $mapHalfLat), ($cLon + $mapHalfLon))
    $boxText = ($box | ForEach-Object { $_.ToString('0.0', $inv) }) -join ' '
    $lonSpan = 2 * $mapHalfLon
    $latSpan = 2 * $mapHalfLat

    # Land and water, once per place.
    $baseName = 'map-base-{0}.png' -f (($box | ForEach-Object { $_.ToString('0.0', $inv) }) -join '_')
    $basePath = Join-Path $LocalDir $baseName
    if (-not (Test-Path $basePath)) {
        try {
            Save-MapImage (Get-MapUrl 'https://gibs.earthdata.nasa.gov/wms/epsg4326/best/wms.cgi' 'OSM_Land_Water_Map' $box ([int][Math]::Round($lonSpan / 0.0015)) ([int][Math]::Round($latSpan / 0.0015)) 'image/png' '') $basePath
        } catch {
            Write-Host "Map skipped: the land and water map did not download ($($_.Exception.Message))."
            return
        }
        Get-ChildItem -Path $LocalDir -Filter 'map-base-*.png' | Where-Object { $_.Name -ne $baseName } | Remove-Item -Force -ErrorAction SilentlyContinue
    }

    # Elevation for the relief, once per place.
    try { $dem = @(Update-Dem $box) }
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
    try { $tides = Get-TideLines $center[0] $center[1] $boxText }
    catch { Write-Host "Tides skipped: $($_.Exception.Message)"; $tides = @(Get-MapEntries 'tidestation') + @(Get-MapEntries 'tide') }

    $lines = New-Object System.Collections.Generic.List[string]
    $lines.Add(('# weather map for the curtain, as of {0}' -f (Get-Date -Format 't')))
    $lines.Add("box $boxText")
    $lines.Add("base $baseName")
    foreach ($l in @($dem) + @($radar) + @($sat) + @($stations) + @($tides)) { if ($l) { $lines.Add($l) } }
    $lines.Add(('credit NOAA  {0}  USGS  {0}  {1} OpenStreetMap' -f [char]0x00B7, [char]0x00A9))
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
