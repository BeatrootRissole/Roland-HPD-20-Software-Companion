param([int]$ProcId, [string]$Out, [string]$Title = 'HPD-20 Companion')
Add-Type -AssemblyName System.Drawing
Add-Type @"
using System;
using System.Text;
using System.Runtime.InteropServices;
public class WinShot {
  public delegate bool EnumProc(IntPtr h, IntPtr l);
  [DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc cb, IntPtr l);
  [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
  [DllImport("user32.dll")] public static extern int GetWindowText(IntPtr h, StringBuilder s, int n);
  [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
  [DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr h, IntPtr hdc, uint flags);
  [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
  [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr h, int cmd);
  [StructLayout(LayoutKind.Sequential)] public struct RECT { public int L,T,R,B; }

  public static IntPtr Find(uint pid, string title) {
    IntPtr best = IntPtr.Zero; int bestArea = 0;
    EnumWindows(delegate(IntPtr hw, IntPtr l) {
      uint p; GetWindowThreadProcessId(hw, out p);
      if (p != pid) return true;
      var t = new StringBuilder(256); GetWindowText(hw, t, 256);
      if (t.ToString() != title) return true;
      RECT r; GetWindowRect(hw, out r);
      int area = (r.R-r.L) * (r.B-r.T);
      if (area > bestArea) { bestArea = area; best = hw; }
      return true;
    }, IntPtr.Zero);
    return best;
  }
}
"@
$hwnd = [WinShot]::Find([uint32]$ProcId, $Title)
if ($hwnd -eq [IntPtr]::Zero) { throw "window '$Title' not found for pid $ProcId" }
[WinShot]::ShowWindow($hwnd, 9) | Out-Null
Start-Sleep -Milliseconds 300
[WinShot]::SetForegroundWindow($hwnd) | Out-Null
Start-Sleep -Milliseconds 700
$r = New-Object WinShot+RECT
[WinShot]::GetWindowRect($hwnd, [ref]$r) | Out-Null
$w = $r.R - $r.L; $h = $r.B - $r.T
if ($w -lt 100 -or $h -lt 100) { throw "window size looks wrong: ${w}x${h}" }
$bmp = New-Object System.Drawing.Bitmap($w, $h)
$g = [System.Drawing.Graphics]::FromImage($bmp)
$hdc = $g.GetHdc()
[WinShot]::PrintWindow($hwnd, $hdc, 2) | Out-Null
$g.ReleaseHdc($hdc); $g.Dispose()
$bmp.Save($Out, [System.Drawing.Imaging.ImageFormat]::Png)
$bmp.Dispose()
"captured ${w}x${h} -> $Out"
