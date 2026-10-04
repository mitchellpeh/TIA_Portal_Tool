# GUI smoke test: starts the app, walks launcher -> SLC window -> Main menu -> Import / Export -> Main menu with
# UI Automation, and saves a screenshot of each window plus each window's position against the screen's work
# area (the windows must fit an 864-px-high laptop screen).
#
# Usage: powershell -ExecutionPolicy Bypass -File tools\tests\gui-check.ps1 [-Exe <TiaPortalTool.exe>] [-OutDir <dir>]
#   Defaults: the Debug build, tools\tests\out\gui.
param([string]$Exe, [string]$OutDir)
$repo = (Resolve-Path "$PSScriptRoot\..\..").Path
if (-not $Exe) { $Exe = "$repo\TiaPortalTool\bin\Debug\net48\TiaPortalTool.exe" }
if (-not $OutDir) { $OutDir = "$repo\tools\tests\out\gui" }
New-Item -ItemType Directory -Force $OutDir | Out-Null
. "$PSScriptRoot\gui-common.ps1"

$app = Start-App $Exe
try {
  $area = [System.Windows.Forms.Screen]::PrimaryScreen.WorkingArea
  "work area: $($area.Width) x $($area.Height) px"
  $launcher = Find-Window $app "*"
  Save-WindowShot $launcher "$OutDir\launcher.png"
  Report-Size $launcher "Launcher"

  Invoke-Button $launcher "SlcConversionButton"
  $slc = Find-Window $app "SLC 500*"
  Save-WindowShot $slc "$OutDir\slc.png"
  Report-Size $slc "SLC window"

  Invoke-Button $slc "BackButton"
  $launcher = Find-Window $app "*"
  Invoke-Button $launcher "ImportExportButton"
  $main = Find-Window $app "TIA Portal Import*"
  Save-WindowShot $main "$OutDir\import_export.png"
  Report-Size $main "Import / Export window"

  Invoke-Button $main "BackButton"
  Find-Window $app "*" | Out-Null
  "Main menu buttons work. Screenshots in $OutDir"
}
finally { $app | Stop-Process -ErrorAction SilentlyContinue }
