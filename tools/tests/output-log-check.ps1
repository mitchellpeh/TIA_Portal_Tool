# Checks the Output pane without TIA Portal: hosts OutputLog from an app build in a test window, feeds it the lines
# a real run writes (firewall not approved, TIA starting, then progress, warnings, errors, success) and screenshots
# it while it pulses and after the log moves on.
#
# Usage: powershell -ExecutionPolicy Bypass -File tools\tests\output-log-check.ps1 [-Exe <TiaPortalTool.exe>] [-OutDir <dir>]
param([string]$Exe, [string]$OutDir)
$repo = (Resolve-Path "$PSScriptRoot\..\..").Path
if (-not $Exe) { $Exe = "$repo\TiaPortalTool\bin\Debug\net48\TiaPortalTool.exe" }
if (-not $OutDir) { $OutDir = "$repo\tools\tests\out\output-log" }
New-Item -ItemType Directory -Force $OutDir | Out-Null
Add-Type -AssemblyName PresentationFramework, PresentationCore, WindowsBase, System.Drawing
$app = [System.Reflection.Assembly]::LoadFrom($Exe)
$outputLogType = $app.GetType("TiaPortalTool.OutputLog", $true)
$levelType = $app.GetType("TiaPortalTool.LogLevel", $true)

function Pump([int]$ms) {
  $until = [DateTime]::Now.AddMilliseconds($ms)
  while ([DateTime]::Now -lt $until) {
    $frame = New-Object System.Windows.Threading.DispatcherFrame
    [System.Windows.Threading.Dispatcher]::CurrentDispatcher.BeginInvoke([System.Windows.Threading.DispatcherPriority]::Background,
      [Action]{ $frame.Continue = $false }) | Out-Null
    [System.Windows.Threading.Dispatcher]::PushFrame($frame)
    Start-Sleep -Milliseconds 15
  }
}
function Shot($window, $file) {
  $source = [System.Windows.PresentationSource]::FromVisual($window)
  $scale = $source.CompositionTarget.TransformToDevice.M11
  $x = [int]($window.Left * $scale); $y = [int]($window.Top * $scale)
  $w = [int]($window.ActualWidth * $scale); $h = [int]($window.ActualHeight * $scale)
  $bitmap = New-Object System.Drawing.Bitmap $w, $h
  [System.Drawing.Graphics]::FromImage($bitmap).CopyFromScreen($x, $y, 0, 0, $bitmap.Size)
  $bitmap.Save($file); $bitmap.Dispose()
}
function Hex($text) { (New-Object System.Windows.Media.BrushConverter).ConvertFromString($text) }

# Same look as the windows' Output card (CardStyle) and log box.
$window = New-Object System.Windows.Window -Property @{ Title = "Output log check"; Width = 900; Height = 420; Background = (Hex "#0f172a"); WindowStartupLocation = "CenterScreen" }
# OutputLog colours its lines and the pulse from the theme resources; give the bare window the Modern theme.
foreach ($theme in "Palette.Modern.xaml", "Shapes.Modern.xaml") {
  $stream = [IO.File]::OpenRead("$repo\TiaPortalTool\Themes\$theme")
  try { $window.Resources.MergedDictionaries.Add([System.Windows.Markup.XamlReader]::Load($stream)) } finally { $stream.Dispose() }
}
$card = New-Object System.Windows.Controls.Border -Property @{ Background = (Hex "#111827"); BorderBrush = (Hex "#334155"); BorderThickness = 1; CornerRadius = 10; Padding = "16,12,16,16"; Margin = 20 }
$box = New-Object System.Windows.Controls.RichTextBox -Property @{ IsReadOnly = $true; Background = (Hex "#020617"); Foreground = (Hex "#cbd5e1"); BorderBrush = (Hex "#1e293b"); Padding = 6 }
$card.Child = $box; $window.Content = $card
$log = $outputLogType.GetConstructors()[0].Invoke([object[]]@($window.psobject.BaseObject, $box.psobject.BaseObject, $card.psobject.BaseObject))
$attention = New-Object System.Collections.ArrayList
$log.add_AttentionChanged([Action[string]]{ param($reason) [void]$attention.Add($(if ($reason) { $reason } else { "<stopped>" })) })
$window.Show(); Pump 500

$log.Append("Converting the SLC program...")
$log.Append("Ladder: 980 of 1000 rung(s) converted; 20 need manual conversion.")
$log.Append("3 item(s) need attention:")
$log.Append("  - #M1:12.1000 is in a module's M0/M1/G file.", [Enum]::Parse($levelType, "Warning"))
$log.Append("Checking whether TIA Portal's Openness firewall has approved this app...")
$log.Append("Warning: this copy of the app hasn't been approved in TIA Portal's Openness firewall yet. TIA Portal will ask whether to allow this app (the Openness access prompt).")
$log.Append("Starting TIA Portal in the background (Openness 20.0.0.0)...")
Pump 350; Shot $window "$OutDir\1_pulsing_a.png"
Pump 700; Shot $window "$OutDir\2_pulsing_b.png"
"pulsing while TIA starts: $($log.NeedsAttention)"

$log.Append("Creating project TestProject (TIA Portal V20)...")
$log.Append("PLC program compile: state = Warning, errors = 0, warnings = 6")
$log.Append("  Mismatch: SYSTEM rung 12: outputs differ")
$log.Append("RESULT: SUCCESS, saved=True")
$log.AppendResult([Enum]::Parse($app.GetType("TiaPortalTool.RunResult", $true), "Success"), "converted, imported, compiled and verified in 06:41; opening the report.")
Pump 400; Shot $window "$OutDir\3_moved_on.png"
"pulsing after the log moved on: $($log.NeedsAttention)"
"attention events: $($attention -join ' | ')"
$window.Close()
