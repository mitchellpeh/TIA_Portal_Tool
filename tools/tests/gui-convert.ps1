# Runs Convert Only through the GUI (fills in the SLC export and output folder, clicks Convert Only), saves a
# screenshot of the SLC window and its log, and checks that the conversion report opened afterwards (in Calc or
# Excel). Only the spreadsheet processes this run started are closed again.
#
# Usage: powershell -ExecutionPolicy Bypass -File tools\tests\gui-convert.ps1 -Slc <export.SLC> [-OutDir <dir>] [-Exe <exe>] [-Wait <seconds>]
param([Parameter(Mandatory)][string]$Slc, [string]$OutDir, [string]$Exe, [int]$Wait = 12)
$repo = (Resolve-Path "$PSScriptRoot\..\..").Path
if (-not $Exe) { $Exe = "$repo\TiaPortalTool\bin\Debug\net48\TiaPortalTool.exe" }
if (-not $OutDir) { $OutDir = "$repo\tools\tests\out\gui_" + [IO.Path]::GetFileNameWithoutExtension($Slc) }
. "$PSScriptRoot\gui-common.ps1"

$spreadsheetApps = "soffice*", "scalc*", "EXCEL*"
$before = @(Get-Process $spreadsheetApps -ErrorAction SilentlyContinue | ForEach-Object Id)
$app = Start-App $Exe
try {
  $launcher = Find-Window $app "*"
  Invoke-Button $launcher "SlcConversionButton"
  $slcWindow = Find-Window $app "SLC 500*"
  Set-Text $slcWindow "SlcPathTextBox" $Slc
  Set-Text $slcWindow "OutputPathTextBox" $OutDir
  Invoke-Button $slcWindow "ConvertButton"
  Start-Sleep -Seconds $Wait
  New-Item -ItemType Directory -Force $OutDir | Out-Null
  Save-WindowShot $slcWindow "$OutDir\gui.png"
  "Screenshot: $OutDir\gui.png"
}
finally { $app | Stop-Process -ErrorAction SilentlyContinue }

Start-Sleep -Seconds 3
$opened = @(Get-Process $spreadsheetApps -ErrorAction SilentlyContinue | Where-Object { $before -notcontains $_.Id })
if ($opened.Count -gt 0) {
  "Report opened: OK ($(($opened | ForEach-Object ProcessName) -join ', '))"
  $opened | Stop-Process -Force -ErrorAction SilentlyContinue
}
else {
  "Report opened: NO - Convert Only should open SLC_Conversion_Report.xlsx when it finishes"
}
