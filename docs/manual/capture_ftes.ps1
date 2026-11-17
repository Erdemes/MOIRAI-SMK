param(
  [string]$Out = "C:\Users\erdem\RiderProjects\MOIRAI\docs\manual\img\_ftes.png",
  [switch]$DoDrag,
  [string]$Nav = "FTEs",
  [string]$CalHeader = "Leave Calendar",
  [string]$HelpText = "Drag a leave band to move it; drag its left/right edge to change the start/end. The live dates show while dragging and are saved on release.",
  [int]$StartIdx = 8, [int]$BandW = 8, [int]$Row = 0)

Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes, System.Windows.Forms, System.Drawing
Add-Type @"
using System;using System.Runtime.InteropServices;
public class U{[DllImport("user32.dll")]public static extern bool SetCursorPos(int x,int y);[DllImport("user32.dll")]public static extern void mouse_event(uint f,uint dx,uint dy,uint d,int e);[DllImport("user32.dll")]public static extern bool ShowWindow(IntPtr h,int n);[DllImport("user32.dll")]public static extern bool SetForegroundWindow(IntPtr h);public const uint LD=0x02,LU=0x04;}
"@
$exe="C:\Users\erdem\RiderProjects\MOIRAI\Moirai.App\bin\Debug\net10.0\Moirai.App.exe"
$p=Start-Process $exe -PassThru; Start-Sleep 7
$root=[System.Windows.Automation.AutomationElement]::RootElement
function Find($n){$root.FindFirst('Descendants',(New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::NameProperty,$n)))}
function Click($e){if(-not $e){return};$r=$e.Current.BoundingRectangle;[U]::SetCursorPos([int]($r.X+$r.Width/2),[int]($r.Y+$r.Height/2));Start-Sleep -m 200;[U]::mouse_event([U]::LD,0,0,0,0);[U]::mouse_event([U]::LU,0,0,0,0);Start-Sleep -m 600}
$mh=$p.MainWindowHandle; [U]::ShowWindow($mh,3)|Out-Null; [U]::SetForegroundWindow($mh)|Out-Null; Start-Sleep 2
for ($t=0; $t -lt 4 -and -not (Find $CalHeader); $t++) { Click (Find $Nav); Start-Sleep 1 }
if (-not (Find $CalHeader)) { Write-Host "WARN: not on $Nav page" } else { Write-Host "on $Nav page" }
Start-Sleep 1

if ($DoDrag) {
  $lc = Find $CalHeader; $help = Find $HelpText
  $lcr = $lc.Current.BoundingRectangle; $hr = $help.Current.BoundingRectangle
  $DayW=26; $LabelW=120; $AxisH=40; $RowH=26
  $ctrlX = $lcr.X; $ctrlTop = $hr.Y + $hr.Height + 8
  $cx = [int]($ctrlX + $LabelW + ($StartIdx + $BandW/2.0)*$DayW)
  $cy = [int]($ctrlTop + $AxisH + $Row*$RowH + $RowH/2)
  $tx = [int]($cx + 6*$DayW)
  "band center screen: $cx,$cy  -> $tx,$cy"
  [U]::SetCursorPos($cx,$cy); Start-Sleep -m 300
  [U]::mouse_event([U]::LD,0,0,0,0); Start-Sleep -m 200
  for ($i=1; $i -le 10; $i++) { [U]::SetCursorPos([int]($cx+($tx-$cx)*$i/10),$cy); Start-Sleep -m 60 }
  Start-Sleep -m 400
}

$w=[System.Windows.Automation.AutomationElement]::FromHandle($mh).Current.BoundingRectangle
$bmp=New-Object System.Drawing.Bitmap([int]$w.Width,[int]$w.Height); $g=[System.Drawing.Graphics]::FromImage($bmp)
$g.CopyFromScreen([int]$w.X,[int]$w.Y,0,0,$bmp.Size)
$bmp.Save($Out,[System.Drawing.Imaging.ImageFormat]::Png); $g.Dispose();$bmp.Dispose()

if ($DoDrag) { [U]::mouse_event([U]::LU,0,0,0,0); Start-Sleep -m 400 }
"saved $Out"
Start-Sleep 1
Stop-Process -Id $p.Id -Force