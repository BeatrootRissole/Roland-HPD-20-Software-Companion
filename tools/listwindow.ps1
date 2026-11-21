param([int]$ProcId)
Add-Type @"
using System;
using System.Text;
using System.Runtime.InteropServices;
public class WinList {
  public delegate bool EnumProc(IntPtr h, IntPtr l);
  [DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc cb, IntPtr l);
  [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
  [DllImport("user32.dll")] public static extern int GetWindowText(IntPtr h, StringBuilder s, int n);
  [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
  [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
  [DllImport("user32.dll")] public static extern bool GetClientRect(IntPtr h, out RECT r);
  [StructLayout(LayoutKind.Sequential)] public struct RECT { public int L,T,R,B; }
  public static string All(uint pid) {
    var sb = new StringBuilder();
    EnumWindows(delegate(IntPtr hw, IntPtr l) {
      uint p; GetWindowThreadProcessId(hw, out p);
      if (p != pid) return true;
      var t = new StringBuilder(256); GetWindowText(hw, t, 256);
      RECT r; GetWindowRect(hw, out r);
      RECT c; GetClientRect(hw, out c);
      sb.AppendLine(string.Format("hwnd={0} vis={1} '{2}' win={3}x{4} client={5}x{6}",
        hw, IsWindowVisible(hw), t, r.R-r.L, r.B-r.T, c.R-c.L, c.B-c.T));
      return true;
    }, IntPtr.Zero);
    return sb.ToString();
  }
}
"@
[WinList]::All([uint32]$ProcId)
