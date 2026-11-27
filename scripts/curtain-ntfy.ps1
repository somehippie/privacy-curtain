<#
.SYNOPSIS
  Phone alerts for the curtain through ntfy (https://ntfy.sh): free, no account, no install on the PC.

.DESCRIPTION
  One-time setup:
    1. Run:  .\scripts\curtain-ntfy.ps1 -Setup
       It adds an "ntfy" section with a long random topic to config.json (creating config.json from
       config.example.json if needed) and prints the topic.
    2. Install the ntfy app on your phone (Android or iOS) and subscribe to that topic on ntfy.sh.
    3. Run:  .\scripts\curtain-ntfy.ps1 -Test     Your phone should show a test message.

  From then on, every curtain sends its alerts here: when config.json has an ntfy topic, the curtain uses
  this script as its notify hook (unless --notify-hook names another). The curtain hands each alert over
  as {"message": "..."} on standard input. What reaches the phone:
    - "Someone tried the PC": blocked key presses or clicks, then a minute with no unlock. High priority.
    - The Windows security screen, Task Manager or an administrator window behind the curtain. High priority.
    - The all-clear when the curtain closes, with the totals. Low priority (silent).

  Anyone who knows the topic can read its messages, so keep it long and random, and private. Messages
  carry counts only, never which keys were pressed. Turn alerts off with "enabled": false in the ntfy
  section, or remove the section. Optional: "server" for a self-hosted ntfy (default https://ntfy.sh).

  Never throws and never blocks the curtain: it gives up on the network after 10 seconds, and records each
  send (or failure) in local\ntfy.log.

.PARAMETER Setup
  Add an ntfy section with a new random topic to config.json (keeps an existing topic).

.PARAMETER Test
  Send a test message.

.PARAMETER DryRun
  Build the message and print its JSON (topic hidden); send nothing.

.PARAMETER Message
  The alert text, instead of reading the curtain's payload from standard input (for trying it out).
#>
[CmdletBinding()]
param(
    [string]$Event = 'curtain_alert',
    [switch]$Setup,
    [switch]$Test,
    [switch]$DryRun,
    [string]$Message = ''
)

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
$root = Split-Path -Parent $PSScriptRoot
$configPath = Join-Path $root 'config.json'
$logPath = Join-Path (Join-Path $root 'local') 'ntfy.log'
$utf8 = New-Object System.Text.UTF8Encoding($false)

function Write-NtfyLog([string]$Text) {
    try {
        $dir = Split-Path -Parent $logPath
        if (-not (Test-Path $dir)) { return }
        if ((Test-Path $logPath) -and (Get-Item $logPath).Length -gt 65536) {
            $keep = @(Get-Content $logPath | Select-Object -Last 200)
            [IO.File]::WriteAllLines($logPath, $keep, $utf8)
        }
        [IO.File]::AppendAllText($logPath, (Get-Date).ToString('yyyy-MM-dd HH:mm:ss') + "`t" + $Text + "`r`n", $utf8)
    } catch { }
}

function Read-Config {
    if (-not (Test-Path $configPath)) { return $null }
    return [IO.File]::ReadAllText($configPath) | ConvertFrom-Json
}

if ($Setup) {
    $cfg = Read-Config
    if (-not $cfg) {
        $example = Join-Path $root 'config.example.json'
        if (Test-Path $example) { $cfg = [IO.File]::ReadAllText($example) | ConvertFrom-Json }
        else { $cfg = New-Object PSObject }
    }
    if (-not $cfg.PSObject.Properties['ntfy'] -or -not $cfg.ntfy) {
        $cfg | Add-Member -NotePropertyName ntfy -NotePropertyValue ([pscustomobject]@{ server = 'https://ntfy.sh'; topic = '' }) -Force
    }
    if (-not $cfg.ntfy.PSObject.Properties['topic']) { $cfg.ntfy | Add-Member -NotePropertyName topic -NotePropertyValue '' }
    if ([string]::IsNullOrWhiteSpace([string]$cfg.ntfy.topic)) {
        $chars = 'abcdefghijkmnpqrstuvwxyz23456789'
        $bytes = New-Object byte[] 24
        $rng = [Security.Cryptography.RNGCryptoServiceProvider]::Create()
        $rng.GetBytes($bytes); $rng.Dispose()
        $cfg.ntfy.topic = 'curtain-' + (-join ($bytes | ForEach-Object { $chars[$_ % $chars.Length] }))
        # PowerShell 5.1 writes ' < > & as \u escapes; turn them back so the file stays readable.
        $json = ($cfg | ConvertTo-Json -Depth 5) -replace '\\u0027', "'" -replace '\\u003c', '<' -replace '\\u003e', '>' -replace '\\u0026', '&'
        [IO.File]::WriteAllText($configPath, $json, $utf8)
        Write-Host "Added an ntfy topic to $configPath."
    } else {
        Write-Host "config.json already has an ntfy topic; kept it."
    }
    $server = 'https://ntfy.sh'
    if ($cfg.ntfy.PSObject.Properties['server'] -and $cfg.ntfy.server) { $server = ([string]$cfg.ntfy.server).TrimEnd('/') }
    Write-Host ''
    Write-Host "Topic : $($cfg.ntfy.topic)"
    Write-Host "Server: $server"
    Write-Host ''
    Write-Host 'Next: install the ntfy app on your phone, tap +, and subscribe to that topic (same server).'
    Write-Host 'Then run  .\scripts\curtain-ntfy.ps1 -Test  and a test message should arrive.'
    Write-Host 'Keep the topic private: anyone who knows it can read the alerts.'
    exit 0
}

try {
    $cfg = Read-Config
    if (-not $cfg -or -not $cfg.PSObject.Properties['ntfy'] -or -not $cfg.ntfy) {
        if ($Test -or $DryRun) { Write-Host 'No ntfy section in config.json. Run .\scripts\curtain-ntfy.ps1 -Setup first.' }
        exit 0
    }
    $n = $cfg.ntfy
    if ($n.PSObject.Properties['enabled'] -and $n.enabled -eq $false) { exit 0 }
    $topic = [string]$n.topic
    if ([string]::IsNullOrWhiteSpace($topic)) { exit 0 }
    $server = 'https://ntfy.sh'
    if ($n.PSObject.Properties['server'] -and $n.server) { $server = ([string]$n.server).TrimEnd('/') }

    if ($Test) {
        $Message = 'Curtain test: phone alerts are on. This is a test from your PC; nothing is wrong.'
    } elseif (-not $Message -and [Console]::IsInputRedirected) {
        $raw = [Console]::In.ReadToEnd()
        if ($raw) { try { $Message = [string](($raw | ConvertFrom-Json).message) } catch { } }
    }
    if ([string]::IsNullOrWhiteSpace($Message)) { exit 0 }

    $title = 'Privacy Curtain'; $text = $Message; $priority = 4; $tags = @('warning')
    if ($Message -match '^Curtain test:\s*(.*)$') {
        $text = $Matches[1]; $priority = 3; $tags = @('white_check_mark')
    } elseif ($Message -match '^Curtain all clear:\s*(.*)$') {
        $title = 'Privacy Curtain closed'; $text = $Matches[1]; $priority = 2; $tags = @('white_check_mark')
    } elseif ($Message -match '^Curtain:\s*someone tried to use the PC:\s*(.*)$') {
        $title = 'Someone tried the PC'; $text = $Matches[1]; $tags = @('keyboard')
    } elseif ($Message -match '^Curtain:\s*(.*)$') {
        $text = $Matches[1]
    }
    if ($text.Length -gt 0) { $text = $text.Substring(0, 1).ToUpperInvariant() + $text.Substring(1) }

    $payload = [ordered]@{ topic = $topic; title = $title; message = $text; priority = $priority; tags = $tags }
    $json = $payload | ConvertTo-Json -Compress
    if ($DryRun) { Write-Output ($json -replace '"topic":"[^"]*"', '"topic":"<hidden>"'); exit 0 }

    [Net.ServicePointManager]::SecurityProtocol = [Net.ServicePointManager]::SecurityProtocol -bor [Net.SecurityProtocolType]::Tls12
    $reply = Invoke-RestMethod -Method Post -Uri $server -Body $utf8.GetBytes($json) `
        -ContentType 'application/json; charset=utf-8' -TimeoutSec 10
    Write-NtfyLog ("sent: " + $title + " (priority " + $priority + ", id " + $reply.id + ")")
    if ($Test) { Write-Host 'Sent. Check your phone.' }
} catch {
    Write-NtfyLog ("FAILED: " + $_.Exception.Message)
    if ($Test) { Write-Host ("Could not send: " + $_.Exception.Message) }
}
exit 0
