<#
.SYNOPSIS
  Orange real-PC checklist. Walks you through the drag tests on your own Windows 10/11 PC and writes a report.

.DESCRIPTION
  Run in a normal (not elevated) PowerShell window:
      powershell -ExecutionPolicy Bypass -File .\Run-OrangeChecklist.ps1
  What it does:
    1. Finds Orange (installed or portable; pass -OrangeExe to choose) and turns on its local debug log.
    2. Creates a test folder on your Desktop with sample files it generates itself.
    3. Asks you to do each drag by hand, then checks for the new file and that the original is unchanged.
    4. Writes checklist-report-*.txt (results + Windows version + Orange's debug log) into the test folder.
  Nothing is uploaded. You can delete the test folder afterwards. The debug log stays on until you turn it off
  in Orange > Settings > Troubleshooting (or run this script with -DisableLogAfter).
#>
param(
  [string]$OrangeExe,
  [switch]$DisableLogAfter
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing, System.Windows.Forms

function Say($text, $color = 'Gray') { Write-Host $text -ForegroundColor $color }
function Hash($path) { (Get-FileHash -Algorithm SHA256 -LiteralPath $path).Hash }

# ---------- find Orange
if (-not $OrangeExe) {
  $candidates = @("$env:LOCALAPPDATA\Programs\Orange\Orange.exe", (Join-Path $PSScriptRoot 'Orange.exe'), (Join-Path (Get-Location) 'Orange.exe'))
  $OrangeExe = $candidates | Where-Object { Test-Path $_ } | Select-Object -First 1
}
if (-not $OrangeExe -or -not (Test-Path $OrangeExe)) { Say "Couldn't find Orange.exe. Run again with -OrangeExe 'C:\path\to\Orange.exe'." Red; exit 2 }
$orangeVersion = (Get-Item $OrangeExe).VersionInfo.ProductVersion

# ---------- settings: turn on the debug log, read the wheel keys
$settingsDir = Join-Path $env:LOCALAPPDATA 'Orange'
$settingsFile = Join-Path $settingsDir 'settings.json'
New-Item -ItemType Directory -Force $settingsDir | Out-Null
$settings = if (Test-Path $settingsFile) { Get-Content $settingsFile -Raw | ConvertFrom-Json } else { [pscustomobject]@{} }
$settings | Add-Member -NotePropertyName DebugLog -NotePropertyValue $true -Force
$settings | ConvertTo-Json -Depth 20 | Set-Content -Encoding UTF8 $settingsFile
function KeyName($k, $default) {
  switch ("$k") { 'AltShift' {'Alt+Shift'} 'ShiftDouble' {'Shift twice'} 'ShiftCtrl' {'Shift+Ctrl'} '' {$default} default {"$k"} }
}
$convertKey = KeyName $settings.ConvertKey 'Shift'
$toolsKey   = KeyName $settings.ToolsKey 'Alt+Shift'
$actionsKey = KeyName $settings.ActionsKey 'Ctrl'

# Restart Orange so it picks up the log setting.
Get-Process Orange -ErrorAction SilentlyContinue | Where-Object { $_.Path -eq (Resolve-Path $OrangeExe).Path } | Stop-Process -Force
Start-Sleep -Milliseconds 500
Start-Process $OrangeExe -ArgumentList '--background'
Start-Sleep -Seconds 3

# ---------- test folder and samples
$stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
$dir = Join-Path ([Environment]::GetFolderPath('Desktop')) "Orange-Checklist-$stamp"
New-Item -ItemType Directory -Force $dir | Out-Null
function New-Sample($name, $format) {
  $bmp = New-Object System.Drawing.Bitmap 320, 200
  $g = [System.Drawing.Graphics]::FromImage($bmp)
  $brush = New-Object System.Drawing.Drawing2D.LinearGradientBrush((New-Object System.Drawing.Rectangle 0,0,320,200), [System.Drawing.Color]::FromArgb(255,160,30), [System.Drawing.Color]::FromArgb(255,78,80), 30)
  $g.FillRectangle($brush, 0, 0, 320, 200)
  $g.DrawString('Orange test', (New-Object System.Drawing.Font 'Segoe UI', 24), [System.Drawing.Brushes]::White, 40, 70)
  $g.Dispose()
  $path = Join-Path $dir $name
  $bmp.Save($path, $format); $bmp.Dispose()
  return $path
}
$png1 = New-Sample 'convert-me.png' ([System.Drawing.Imaging.ImageFormat]::Png)
$png2 = New-Sample 'tool-me.png' ([System.Drawing.Imaging.ImageFormat]::Png)
$jpg  = New-Sample 'act-on-me.jpg' ([System.Drawing.Imaging.ImageFormat]::Jpeg)
$png3 = New-Sample 'desktop-drag.png' ([System.Drawing.Imaging.ImageFormat]::Png)
$desktopSample = Join-Path ([Environment]::GetFolderPath('Desktop')) 'orange-desktop-test.png'
Copy-Item $png3 $desktopSample -Force

$results = New-Object System.Collections.Generic.List[object]
function Run-Step($title, $instructions, $original, [scriptblock]$check) {
  Say ""
  Say "== $title" Cyan
  Say $instructions White
  $before = if ($original) { Hash $original } else { $null }
  $existing = @(Get-ChildItem -LiteralPath (Split-Path $original) | ForEach-Object FullName)
  $answer = Read-Host "Press Enter when done (type s to skip)"
  if ($answer -eq 's') { $results.Add([pscustomobject]@{ Step = $title; Result = 'SKIPPED'; Detail = '' }); return }
  Start-Sleep -Seconds 2
  $new = @(Get-ChildItem -LiteralPath (Split-Path $original) | Where-Object { $existing -notcontains $_.FullName })
  $originalOk = (Test-Path $original) -and ((Hash $original) -eq $before)
  $ok, $detail = & $check $new
  if (-not $originalOk) { $ok = $false; $detail = "ORIGINAL CHANGED OR MOVED. $detail" }
  $results.Add([pscustomobject]@{ Step = $title; Result = $(if ($ok) {'PASS'} else {'FAIL'}); Detail = $detail })
  Say ($(if ($ok) {"PASS  $detail"} else {"FAIL  $detail"})) $(if ($ok) {'Green'} else {'Red'})
}

Start-Process explorer.exe $dir
Say "A File Explorer window opened on $dir." White
Say "Keys from your settings: Convert = $convertKey, Tools = $toolsKey, Actions = $actionsKey." White

Run-Step "1. Convert from File Explorer (PNG -> WebP)" `
  "Start dragging convert-me.png, keep holding the mouse button, tap $convertKey. Drop on the WebP wedge." $png1 `
  { param($new) $w = $new | Where-Object Extension -eq '.webp'; if ($w) { $true, "made $($w[0].Name)" } else { $false, "no .webp appeared beside convert-me.png" } }

Run-Step "2. Convert from the desktop (PNG -> JPG)" `
  "On the desktop, drag orange-desktop-test.png, tap $convertKey, drop on JPG." $desktopSample `
  { param($new) $j = $new | Where-Object { $_.Extension -in '.jpg','.jpeg' -and $_.Name -like 'orange-desktop-test*' }; if ($j) { $true, "made $($j[0].Name)" } else { $false, "no JPG beside orange-desktop-test.png on the desktop" } }

Run-Step "3. Tools wheel (compress)" `
  "Drag tool-me.png, tap $toolsKey, drop on Compress. If a settings panel opens, keep the defaults and press Apply/Run." $png2 `
  { param($new) $n = $new | Where-Object Name -like 'tool-me*'; if ($n) { $true, "made $($n[0].Name)" } else { $false, "no new tool-me* file (note: Compress refuses to save a bigger file; check Orange's message)" } }

Run-Step "4. Actions wheel (zip)" `
  "Drag act-on-me.jpg, tap $actionsKey, drop on Zip." $jpg `
  { param($new) $z = $new | Where-Object Extension -eq '.zip'; if ($z) { $true, "made $($z[0].Name)" } else { $false, "no .zip appeared" } }

Run-Step "5. Cancel" `
  "Drag convert-me.png, tap $convertKey, then press Esc (or drop outside the wheel). Nothing should be created." $png1 `
  { param($new) if ($new.Count -eq 0) { $true, "nothing created" } else { $false, "unexpected: $($new.Name -join ', ')" } }

# ---------- report
$os = Get-CimInstance Win32_OperatingSystem
$report = Join-Path $dir "checklist-report-$stamp.txt"
$lines = @("Orange checklist report $stamp",
  "Orange: $orangeVersion ($OrangeExe)",
  "Windows: $($os.Caption) $($os.Version) build $($os.BuildNumber)",
  "Scale: $([System.Windows.Forms.Screen]::PrimaryScreen.Bounds.Width) px wide primary screen", "",
  "Results:") + ($results | ForEach-Object { "  [$($_.Result)] $($_.Step)  $($_.Detail)" })
$logDir = Join-Path $settingsDir 'logs'
$todayLog = Get-ChildItem $logDir -Filter 'orange-*.log' -ErrorAction SilentlyContinue | Sort-Object LastWriteTime -Descending | Select-Object -First 1
if ($todayLog) { $lines += @("", "---- Orange debug log ($($todayLog.Name)) ----") + (Get-Content $todayLog.FullName -Tail 400) }
$lines | Set-Content -Encoding UTF8 $report
Remove-Item $desktopSample -ErrorAction SilentlyContinue
Get-ChildItem ([Environment]::GetFolderPath('Desktop')) -Filter 'orange-desktop-test*' | Remove-Item -ErrorAction SilentlyContinue

if ($DisableLogAfter) {
  $settings.DebugLog = $false
  $settings | ConvertTo-Json -Depth 20 | Set-Content -Encoding UTF8 $settingsFile
}
$passed = @($results | Where-Object Result -eq 'PASS').Count
$failed = @($results | Where-Object Result -eq 'FAIL').Count
Say ""
Say "Done: $passed passed, $failed failed. Report: $report" $(if ($failed) {'Yellow'} else {'Green'})
Say "Send the report file to the developer. It holds file names and key events only, no file contents."
Start-Process explorer.exe "/select,`"$report`""
if ($failed) { exit 1 } else { exit 0 }
