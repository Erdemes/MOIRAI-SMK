param([string]$Out = "C:\Users\erdem\RiderProjects\MOIRAI\docs\manual\img\12-earned-value.png")

Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes, System.Windows.Forms, System.Drawing
Add-Type @"
using System;
using System.Runtime.InteropServices;
public class U {
  [DllImport("user32.dll")] public static extern bool SetCursorPos(int x,int y);
  [DllImport("user32.dll")] public static extern void mouse_event(uint f,uint dx,uint dy,uint d,int e);
  [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr h,int n);
  [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
  public const uint LD=0x02, LU=0x04;
}
"@

$exe = "C:\Users\erdem\RiderProjects\MOIRAI\Moirai.App\bin\Debug\net10.0\Moirai.App.exe"
$proc = Start-Process $exe -PassThru
Start-Sleep -Seconds 7

$root = [System.Windows.Automation.AutomationElement]::RootElement
function Find($name) {
  $cond = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::NameProperty, $name)
  return $root.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $cond)
}
function FindContains($sub) {
  $all = $root.FindAll([System.Windows.Automation.TreeScope]::Descendants,
    (New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::IsEnabledProperty, $true)))
  foreach ($e in $all) { if ($e.Current.Name -and $e.Current.Name.Contains($sub)) { return $e } }
  return $null
}
function FindAllContains($sub) {
  $res = @()
  $all = $root.FindAll([System.Windows.Automation.TreeScope]::Descendants,
    (New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::IsEnabledProperty, $true)))
  foreach ($e in $all) { if ($e.Current.Name -and $e.Current.Name.Contains($sub)) { $res += $e } }
  return $res
}
function ClickEl($el) {
  if (-not $el) { Write-Host "  (element not found)"; return $false }
  $r = $el.Current.BoundingRectangle
  if ($r.Width -le 0) { Write-Host "  (no rect)"; return $false }
  $cx = [int]($r.X + $r.Width/2); $cy = [int]($r.Y + $r.Height/2)
  [U]::SetCursorPos($cx,$cy) | Out-Null; Start-Sleep -Milliseconds 200
  [U]::mouse_event([U]::LD,0,0,0,0); [U]::mouse_event([U]::LU,0,0,0,0)
  Start-Sleep -Milliseconds 600
  return $true
}
function InvokeEl($el) {
  if (-not $el) { Write-Host "  (element not found)"; return $false }
  $inv = $null
  if ($el.TryGetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern, [ref]$inv)) {
    $inv.Invoke(); Start-Sleep -Milliseconds 700; return $true
  }
  $exp = $null
  if ($el.TryGetCurrentPattern([System.Windows.Automation.ExpandCollapsePattern]::Pattern, [ref]$exp)) {
    if ($exp.Current.ExpandCollapseState -ne [System.Windows.Automation.ExpandCollapseState]::Expanded) { $exp.Expand() }
    Start-Sleep -Milliseconds 700; return $true
  }
  Write-Host "  (no invoke/expand pattern)"; return $false
}

$mh = $proc.MainWindowHandle
if ($mh -ne 0) { [U]::ShowWindow($mh, 3) | Out-Null; [U]::SetForegroundWindow($mh) | Out-Null }
Start-Sleep -Seconds 2

Write-Host "Navigate to Planning"; ClickEl (Find "Planning") | Out-Null; Start-Sleep -Seconds 1
Write-Host "Run scenario";       ClickEl (Find "Run scenario") | Out-Null; Start-Sleep -Seconds 6
Write-Host "Expand Results";     InvokeEl (Find "»") | Out-Null; Start-Sleep -Seconds 1
$btns = FindAllContains "Open in window"
Write-Host "Open-in-window buttons: $($btns.Count)"
if ($btns.Count -gt 0) { InvokeEl ($btns[$btns.Count-1]) | Out-Null }
Start-Sleep -Seconds 2

$cond = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::NameProperty, "Test points (burn-down)")
$win = $null
for ($t=0; $t -lt 6 -and -not $win; $t++) {
  $win = $root.FindFirst([System.Windows.Automation.TreeScope]::Children, $cond)
  if (-not $win) { Start-Sleep -Seconds 1 }
}
if (-not $win) { Write-Host "Window not found, capturing main window instead"; $win = [System.Windows.Automation.AutomationElement]::FromHandle($mh) }
$wr = $win.Current.BoundingRectangle
$wh = [IntPtr]$win.Current.NativeWindowHandle
if ($wh -ne 0) { [U]::ShowWindow($wh,3) | Out-Null; [U]::SetForegroundWindow($wh) | Out-Null; Start-Sleep -Milliseconds 800; $wr = $win.Current.BoundingRectangle }

$bmp = New-Object System.Drawing.Bitmap([int]$wr.Width, [int]$wr.Height)
$g = [System.Drawing.Graphics]::FromImage($bmp)
$g.CopyFromScreen([int]$wr.X, [int]$wr.Y, 0, 0, $bmp.Size)
$bmp.Save($Out, [System.Drawing.Imaging.ImageFormat]::Png)
$g.Dispose(); $bmp.Dispose()
Write-Host "Saved $Out  ($([int]$wr.Width)x$([int]$wr.Height))"

Stop-Process -Id $proc.Id -Force
