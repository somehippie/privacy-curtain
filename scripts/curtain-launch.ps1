<#
.SYNOPSIS
  Desktop-friendly launcher for the privacy curtain: asks how many hours, then turns it on.

.DESCRIPTION
  Meant to sit behind a desktop shortcut. It opens a small dialog asking how many hours the
  curtain should stay up (prefilled 12, any value 1-24), then starts the curtain with no console
  window by calling curtain-on.ps1 -Hours <n>. Cancel the dialog and nothing happens.

  Like curtain-on, it refuses to start when no unlock PIN is set, since there would be no way
  to unlock at the keyboard. No admin rights are needed and nothing is installed.

  The dialog also has a box, "Open Claude Code with Remote Control". Ticked, it starts a Claude
  Code Remote Control session (named "privacy-curtain") just before the curtain goes up, so this
  PC stays reachable from another device while the screen is dark, for example to run
  curtain-off.ps1 remotely: a Windows Terminal window running claude --remote-control, which the
  curtain then covers. The box starts cleared when a
  Claude Code session on this PC already has Remote Control on (however it was turned on), so
  launching the curtain again does not pile up windows; the line under it says which session.
  Without Claude Code installed the box is cleared and greyed out. Starting it is best-effort: a
  problem never stops the curtain.

  All paths are resolved from $PSScriptRoot, so this script carries no machine-specific paths.

.PARAMETER Preview
  Draw the dialog, as it would open now, into this PNG instead of showing it, and stop there.
  Nothing is started. For checking its layout.
#>
[CmdletBinding()]
param(
    [string]$Preview
)

$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'curtain-common.ps1')

Add-Type -AssemblyName System.Windows.Forms
Add-Type -AssemblyName System.Drawing

# Sharp text on a scaled display: the dialog is sized from its font, so Windows need not stretch it.
# DialogForm can also be shown without taking the focus, which -Preview uses.
try {
    Add-Type -ReferencedAssemblies System.Windows.Forms, System.Drawing -TypeDefinition @'
using System.Runtime.InteropServices;
using System.Windows.Forms;
namespace CurtainLaunch
{
    public static class Dpi
    {
        [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
    }
    public class DialogForm : Form
    {
        public bool Quiet;
        protected override bool ShowWithoutActivation { get { return Quiet; } }
    }
}
'@
    [void][CurtainLaunch.Dpi]::SetProcessDPIAware()
} catch { }
[System.Windows.Forms.Application]::EnableVisualStyles()

function Show-Note([string]$Text, [string]$Icon = 'Information') {
    [System.Windows.Forms.MessageBox]::Show(
        $Text, 'Privacy Curtain',
        [System.Windows.Forms.MessageBoxButtons]::OK,
        [System.Windows.Forms.MessageBoxIcon]::$Icon) | Out-Null
}

function Show-LaunchDialog([string]$Hours, $Advice, [string]$PreviewFile) {
    # The dialog: the hours, the Remote Control box with the line that explains how it starts, and the buttons.
    # Every size comes from the font, so it scales with the display. Returns the hours and the box's state on
    # "Raise curtain" (with hours from 1 to 24), or $null on Cancel. With PreviewFile it only draws the dialog,
    # laid out as it would open, into that PNG.
    $result = @{ Ok = $false; Hours = 0.0; RemoteControl = $false }
    $font = [System.Drawing.SystemFonts]::MessageBoxFont
    $em = $font.Height
    $form = if ('CurtainLaunch.DialogForm' -as [type]) { New-Object CurtainLaunch.DialogForm } else { New-Object System.Windows.Forms.Form }
    $form.Text = 'Privacy Curtain'
    $form.Font = $font
    $form.AutoScaleMode = [System.Windows.Forms.AutoScaleMode]::None
    $form.FormBorderStyle = [System.Windows.Forms.FormBorderStyle]::FixedDialog
    $form.MaximizeBox = $false
    $form.MinimizeBox = $false
    $form.StartPosition = [System.Windows.Forms.FormStartPosition]::CenterScreen
    $form.TopMost = $true
    $form.AutoSize = $true
    $form.AutoSizeMode = [System.Windows.Forms.AutoSizeMode]::GrowAndShrink
    $pad = [int]($em * 0.9)
    $form.Padding = New-Object System.Windows.Forms.Padding $pad

    $grid = New-Object System.Windows.Forms.TableLayoutPanel
    $grid.AutoSize = $true
    $grid.AutoSizeMode = [System.Windows.Forms.AutoSizeMode]::GrowAndShrink
    $grid.ColumnCount = 1
    $grid.Location = New-Object System.Drawing.Point $pad, $pad
    $grid.Margin = New-Object System.Windows.Forms.Padding 0
    $form.Controls.Add($grid)
    $wrap = [int]($em * 24)

    $row = New-Object System.Windows.Forms.FlowLayoutPanel
    $row.AutoSize = $true
    $row.WrapContents = $false
    $row.Margin = New-Object System.Windows.Forms.Padding 0
    $before = New-Object System.Windows.Forms.Label
    $before.Text = 'Close the curtain automatically after'
    $before.AutoSize = $true
    $box = New-Object System.Windows.Forms.TextBox
    $box.Text = $Hours
    $box.Width = [int]($em * 2.4)
    $box.TextAlign = [System.Windows.Forms.HorizontalAlignment]::Center
    $after = New-Object System.Windows.Forms.Label
    $after.Text = 'hours (1 to 24)'
    $after.AutoSize = $true
    foreach ($l in $before, $after) { $l.Margin = New-Object System.Windows.Forms.Padding 0, ([int]($em * 0.3)), 0, 0 }
    $box.Margin = New-Object System.Windows.Forms.Padding ([int]($em * 0.3)), 0, ([int]($em * 0.3)), 0
    $row.Controls.AddRange(@($before, $box, $after))
    $grid.Controls.Add($row)

    $bad = New-Object System.Windows.Forms.Label
    $bad.Text = 'Enter a number of hours from 1 to 24.'
    $bad.ForeColor = [System.Drawing.Color]::Firebrick
    $bad.AutoSize = $true
    $bad.Visible = $false
    $bad.Margin = New-Object System.Windows.Forms.Padding 0, ([int]($em * 0.3)), 0, 0
    $grid.Controls.Add($bad)

    $rc = New-Object System.Windows.Forms.CheckBox
    $rc.Text = 'Open Claude Code with Remote Control'
    $rc.AutoSize = $true
    $rc.Checked = [bool]$Advice.Check
    $rc.Enabled = [bool]$Advice.Enabled
    $rc.Margin = New-Object System.Windows.Forms.Padding 0, ([int]($em * 1.1)), 0, 0
    $grid.Controls.Add($rc)

    $note = New-Object System.Windows.Forms.Label
    $note.Text = $Advice.Note
    $note.ForeColor = [System.Drawing.SystemColors]::GrayText
    $note.AutoSize = $true
    $note.MaximumSize = New-Object System.Drawing.Size ($wrap - [int]($em * 1.2)), 0
    $note.Margin = New-Object System.Windows.Forms.Padding ([int]($em * 1.2)), ([int]($em * 0.15)), 0, 0
    $grid.Controls.Add($note)

    $buttons = New-Object System.Windows.Forms.FlowLayoutPanel
    $buttons.AutoSize = $true
    $buttons.FlowDirection = [System.Windows.Forms.FlowDirection]::RightToLeft
    $buttons.WrapContents = $false
    $buttons.Anchor = [System.Windows.Forms.AnchorStyles]::Right
    $buttons.Margin = New-Object System.Windows.Forms.Padding 0, ([int]($em * 1.3)), 0, 0
    $cancel = New-Object System.Windows.Forms.Button
    $cancel.Text = 'Cancel'
    $cancel.DialogResult = [System.Windows.Forms.DialogResult]::Cancel
    $raise = New-Object System.Windows.Forms.Button
    $raise.Text = 'Raise curtain'
    foreach ($b in $cancel, $raise) {
        $b.AutoSize = $true
        $b.MinimumSize = New-Object System.Drawing.Size ([int]($em * 5.5)), 0
        $b.Padding = New-Object System.Windows.Forms.Padding ([int]($em * 0.4)), ([int]($em * 0.15)), ([int]($em * 0.4)), ([int]($em * 0.15))
    }
    $cancel.Margin = New-Object System.Windows.Forms.Padding ([int]($em * 0.5)), 0, 0, 0
    $raise.Margin = New-Object System.Windows.Forms.Padding 0
    $buttons.Controls.AddRange(@($cancel, $raise))
    $grid.Controls.Add($buttons)
    $form.AcceptButton = $raise
    $form.CancelButton = $cancel

    $raise.add_Click({
        $h = 0.0
        $ok = [double]::TryParse($box.Text.Trim(), [System.Globalization.NumberStyles]::Float,
                                 [System.Globalization.CultureInfo]::InvariantCulture, [ref]$h) -and $h -ge 1 -and $h -le 24
        if (-not $ok) {
            $bad.Visible = $true
            $box.Focus() | Out-Null
            $box.SelectAll()
            return
        }
        $result.Ok = $true
        $result.Hours = $h
        $result.RemoteControl = $rc.Checked
        $form.DialogResult = [System.Windows.Forms.DialogResult]::OK
        $form.Close()
    })

    try {
        if ($PreviewFile) {
            # Shown far off screen without taking the focus, just long enough to be drawn.
            if ($form -is [CurtainLaunch.DialogForm]) { $form.Quiet = $true }
            $form.TopMost = $false
            $form.ShowInTaskbar = $false
            $form.StartPosition = [System.Windows.Forms.FormStartPosition]::Manual
            $form.Location = New-Object System.Drawing.Point -32000, -32000
            $form.Show()
            $form.Refresh()
            $size = $form.Size
            $bmp = New-Object System.Drawing.Bitmap $size.Width, $size.Height
            try {
                $form.DrawToBitmap($bmp, (New-Object System.Drawing.Rectangle 0, 0, $size.Width, $size.Height))
                $bmp.Save($PreviewFile, [System.Drawing.Imaging.ImageFormat]::Png)
            } finally { $bmp.Dispose() }
            $form.Close()
            return $size
        }
        # The handlers above run while this function waits here, so they see its controls.
        $form.add_Shown({
            $form.Activate()
            $box.Focus() | Out-Null
            $box.SelectAll()
        })
        [void]$form.ShowDialog()
    } finally {
        $form.Dispose()
    }
    if ($result.Ok) { return $result }
    return $null
}

# Same rule as curtain-on: no PIN, no start.
if (-not $Preview -and -not (Test-Path (Join-Path $script:CurtainLocal 'pin.hash'))) {
    Show-Note "No unlock PIN is set yet, so the curtain will not start (there would be no way to unlock it at the keyboard).`n`nAt the PC, run curtain-setpin.ps1 first, then try again." 'Warning'
    return
}

# Where Claude Code is, and whether a session here already has Remote Control on.
$claude = (Get-Command 'claude' -ErrorAction SilentlyContinue).Source
if (-not $claude) { $claude = Join-Path $env:USERPROFILE '.local\bin\claude.exe' }
$claudeFound = Test-Path $claude
$sessions = @()
try { $sessions = @(Get-ClaudeSession) } catch { }
$advice = Get-RemoteControlAdvice $sessions $claudeFound

if ($Preview) {
    $size = Show-LaunchDialog '12' $advice $Preview
    Write-Host ("Drew the dialog ({0} x {1}) into {2}. The Remote Control box starts {3}: {4}" -f $size.Width, $size.Height, $Preview,
                $(if ($advice.Check) { 'ticked' } else { 'cleared' }), $advice.Note)
    return
}
$result = Show-LaunchDialog '12' $advice
if (-not $result) { return }
$hours = $result.Hours

# Start Claude Code Remote Control first, so this PC is reachable (e.g. to run curtain-off.ps1
# remotely) by the time the screen goes dark: a Windows Terminal window running
# claude --remote-control <name>. The curtain covers it. Best-effort: never blocks the curtain.
$repoRoot = $script:CurtainRoot   # the repository root (the folder above scripts\)
$wt = Join-Path $env:LOCALAPPDATA 'Microsoft\WindowsApps\wt.exe'
if ($result.RemoteControl) {
    try {
        if ((Test-Path $wt) -and $claudeFound) {
            # One argument string. Start-Process joins an array with spaces and adds no quotes, so the
            # title must carry its own quotes: unquoted, wt took
            # only "Privacy" as the title and tried to run "Curtain Remote ..." as the command (0x80070002).
            $wtArgs = "-d `"$repoRoot`" --title `"Privacy Curtain Remote`" `"$claude`" --remote-control privacy-curtain"
            Start-Process -FilePath $wt -ArgumentList $wtArgs | Out-Null
        } elseif ($claudeFound) {
            # No Windows Terminal: fall back to claude in its own minimized console.
            Start-Process -FilePath $claude -WorkingDirectory $repoRoot -WindowStyle Minimized `
                -ArgumentList @('--remote-control', 'privacy-curtain') | Out-Null
        } else {
            Show-Note "Remote Control was not started: the 'claude' CLI could not be found. The curtain will still go up." 'Warning'
        }
    } catch {
        Show-Note "Remote Control could not be started: $($_.Exception.Message). The curtain will still go up." 'Warning'
    }
}

# Start the curtain in a hidden PowerShell so no console window appears.
$curtainOn = Join-Path $PSScriptRoot 'curtain-on.ps1'
$hoursArg  = $hours.ToString([System.Globalization.CultureInfo]::InvariantCulture)
$proc = Start-Process -FilePath 'powershell.exe' -WindowStyle Hidden -PassThru -Wait -ArgumentList @(
    '-NoProfile', '-ExecutionPolicy', 'Bypass', '-WindowStyle', 'Hidden',
    '-File', "`"$curtainOn`"", '-Hours', $hoursArg)

switch ($proc.ExitCode) {
    0 { }  # Up and running; the curtain is now visible, so stay quiet.
    3 { Show-Note 'The curtain is already on.' 'Information' }
    default {
        Show-Note "The curtain could not start (code $($proc.ExitCode)).`n`nFor details, run curtain-log.ps1 at the PC." 'Error'
    }
}
