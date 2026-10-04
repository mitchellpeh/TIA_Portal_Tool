# Shared UI Automation helpers for the gui-*.ps1 scripts (dot-source this file).
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes, System.Drawing, System.Windows.Forms
Add-Type @"
using System; using System.Runtime.InteropServices;
public static class GuiTestNative {
  [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
  [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
  public struct RECT { public int L, T, R, B; } }
"@
$script:AutomationRoot = [System.Windows.Automation.AutomationElement]::RootElement

function Start-App($exe) { Start-Process $exe -PassThru }

# Top-level window of the app whose title matches (wildcards allowed); waits up to 20 s for it.
function Find-Window($app, $title) {
  $byProcess = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ProcessIdProperty, $app.Id)
  for ($i = 0; $i -lt 60; $i++) {
    foreach ($w in $script:AutomationRoot.FindAll([System.Windows.Automation.TreeScope]::Children, $byProcess)) {
      if ($w.Current.Name -like $title) { return $w }
    }
    Start-Sleep -Milliseconds 330
  }
  throw "window '$title' not found"
}

function Find-Element($window, $automationId) {
  $byId = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::AutomationIdProperty, $automationId)
  $element = $window.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $byId)
  if (-not $element) { throw "element '$automationId' not found" }
  $element
}

# Clicks a button by its x:Name and gives the UI a moment to react.
function Invoke-Button($window, $automationId) {
  (Find-Element $window $automationId).GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
  Start-Sleep -Milliseconds 900
}

function Set-Text($window, $automationId, $text) {
  (Find-Element $window $automationId).GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).SetValue($text)
}

function Save-WindowShot($window, $file) {
  $handle = [IntPtr]$window.Current.NativeWindowHandle
  [GuiTestNative]::SetForegroundWindow($handle) | Out-Null
  Start-Sleep -Milliseconds 500
  $r = New-Object GuiTestNative+RECT
  [GuiTestNative]::GetWindowRect($handle, [ref]$r) | Out-Null
  $bitmap = New-Object System.Drawing.Bitmap ($r.R - $r.L), ($r.B - $r.T)
  [System.Drawing.Graphics]::FromImage($bitmap).CopyFromScreen($r.L, $r.T, 0, 0, $bitmap.Size)
  $bitmap.Save($file)
  $bitmap.Dispose()
}

function Report-Size($window, $label) {
  $area = [System.Windows.Forms.Screen]::PrimaryScreen.WorkingArea
  $r = $window.Current.BoundingRectangle
  $fits = if ($r.Top -ge $area.Top -and $r.Bottom -le $area.Bottom) { "fits" } else { "DOES NOT FIT" }
  "${label}: top $($r.Top) bottom $($r.Bottom) height $($r.Height) - $fits"
}
