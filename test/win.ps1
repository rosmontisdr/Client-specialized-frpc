param(
  [Parameter(Mandatory=$true)][int]$Target,
  [string]$Action = "list",
  [string]$Match = ""
)

# NOTE: keep this file pure ASCII -- Chinese inside the embedded C# below
# gets mis-decoded by Add-Type's inline compiler (no BOM -> ANSI codepage).

Add-Type @"
using System;
using System.Runtime.InteropServices;
using System.Text;

public class Win {
  public delegate bool EnumProc(IntPtr h, IntPtr l);
  [DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc cb, IntPtr l);
  [DllImport("user32.dll")] public static extern bool EnumChildWindows(IntPtr parent, EnumProc cb, IntPtr l);
  [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
  [DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern int GetWindowTextW(IntPtr h, StringBuilder s, int n);
  [DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern int GetClassNameW(IntPtr h, StringBuilder s, int n);
  [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
  [DllImport("user32.dll")] public static extern bool PostMessageW(IntPtr h, uint msg, IntPtr w, IntPtr l);
  [DllImport("user32.dll")] public static extern IntPtr SendMessageW(IntPtr h, uint msg, IntPtr w, IntPtr l);
  [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);

  [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }

  public static IntPtr Find(uint target, string match, bool visibleOnly) {
    IntPtr found = IntPtr.Zero;
    EnumWindows(new EnumProc(delegate(IntPtr h, IntPtr l) {
      uint pid; GetWindowThreadProcessId(h, out pid);
      if (pid != target) return true;
      if (visibleOnly && !IsWindowVisible(h)) return true;
      StringBuilder t = new StringBuilder(512); GetWindowTextW(h, t, 512);
      if (match.Length > 0 && t.ToString().IndexOf(match) < 0) return true;
      found = h; return false;
    }), IntPtr.Zero);
    return found;
  }

  // only visible WinForms forms -- skips hidden GDI+/Broadcast helper windows
  public static IntPtr Form(uint target, string match) {
    IntPtr found = IntPtr.Zero;
    EnumWindows(new EnumProc(delegate(IntPtr h, IntPtr l) {
      uint pid; GetWindowThreadProcessId(h, out pid);
      if (pid != target) return true;
      if (!IsWindowVisible(h)) return true;
      StringBuilder c = new StringBuilder(256); GetClassNameW(h, c, 256);
      if (c.ToString().IndexOf("WindowsForms10.Window.", StringComparison.Ordinal) < 0) return true;
      StringBuilder t = new StringBuilder(512); GetWindowTextW(h, t, 512);
      if (match.Length > 0 && t.ToString().IndexOf(match) < 0) return true;
      found = h; return false;
    }), IntPtr.Zero);
    return found;
  }

  public static string List(uint target) {
    StringBuilder sb = new StringBuilder();
    EnumWindows(new EnumProc(delegate(IntPtr h, IntPtr l) {
      uint pid; GetWindowThreadProcessId(h, out pid);
      if (pid != target) return true;
      StringBuilder t = new StringBuilder(512); GetWindowTextW(h, t, 512);
      StringBuilder c = new StringBuilder(256); GetClassNameW(h, c, 256);
      sb.AppendLine(h.ToInt64().ToString() + " cls=" + c.ToString() + " visible=" + IsWindowVisible(h) + " title=[" + t.ToString() + "]");
      return true;
    }), IntPtr.Zero);
    return sb.ToString();
  }

  public static string Children(IntPtr parent) {
    StringBuilder sb = new StringBuilder();
    EnumChildWindows(parent, new EnumProc(delegate(IntPtr h, IntPtr l) {
      StringBuilder c = new StringBuilder(256); GetClassNameW(h, c, 256);
      long len = SendMessageW(h, 0x000E, IntPtr.Zero, IntPtr.Zero).ToInt64();
      sb.AppendLine("child " + c.ToString() + " textLen=" + len);
      return true;
    }), IntPtr.Zero);
    return sb.ToString();
  }

  public static string Edits(IntPtr parent) {
    StringBuilder sb = new StringBuilder();
    int i = 0;
    EnumChildWindows(parent, new EnumProc(delegate(IntPtr h, IntPtr l) {
      StringBuilder c = new StringBuilder(256); GetClassNameW(h, c, 256);
      string cls = c.ToString();
      if (cls.IndexOf("EDIT", StringComparison.OrdinalIgnoreCase) < 0) return true;
      long len = SendMessageW(h, 0x000E, IntPtr.Zero, IntPtr.Zero).ToInt64();   // WM_GETTEXTLENGTH
      long lines = SendMessageW(h, 0x00BA, IntPtr.Zero, IntPtr.Zero).ToInt64(); // EM_GETLINECOUNT
      string kind = cls.IndexOf("RICHEDIT", StringComparison.OrdinalIgnoreCase) >= 0 ? "rich" : "plain";
      sb.AppendLine("edit#" + (i++) + " kind=" + kind + " chars=" + len + " lines=" + lines);
      return true;
    }), IntPtr.Zero);
    return sb.ToString();
  }

  public static string ButtonList(IntPtr parent) {
    StringBuilder sb = new StringBuilder();
    EnumChildWindows(parent, new EnumProc(delegate(IntPtr h, IntPtr l) {
      StringBuilder c = new StringBuilder(256); GetClassNameW(h, c, 256);
      if (c.ToString().IndexOf("BUTTON", StringComparison.OrdinalIgnoreCase) < 0) return true;
      RECT r; GetWindowRect(h, out r);
      long len = SendMessageW(h, 0x000E, IntPtr.Zero, IntPtr.Zero).ToInt64();
      sb.AppendLine("button left=" + r.Left + " top=" + r.Top + " w=" + (r.Right - r.Left) + " textLen=" + len);
      return true;
    }), IntPtr.Zero);
    return sb.ToString();
  }

  static void Click(IntPtr h) { SendMessageW(h, 0x00F5, IntPtr.Zero, IntPtr.Zero); }   // BM_CLICK

  // bottom-bar buttons are 92 wide; section delete buttons are 64 wide
  public static string ClickRightmostWide(IntPtr parent) {
    IntPtr best = IntPtr.Zero; int bestLeft = int.MinValue;
    EnumChildWindows(parent, new EnumProc(delegate(IntPtr h, IntPtr l) {
      StringBuilder c = new StringBuilder(256); GetClassNameW(h, c, 256);
      if (c.ToString().IndexOf("BUTTON", StringComparison.OrdinalIgnoreCase) < 0) return true;
      RECT r; if (!GetWindowRect(h, out r)) return true;
      if (r.Right - r.Left < 80) return true;
      if (r.Left > bestLeft) { bestLeft = r.Left; best = h; }
      return true;
    }), IntPtr.Zero);
    if (best == IntPtr.Zero) return "no-button";
    Click(best);
    return "clicked-left=" + bestLeft;
  }

  public static string ClickLeftmost(IntPtr parent) {
    IntPtr best = IntPtr.Zero; int bestLeft = int.MaxValue;
    EnumChildWindows(parent, new EnumProc(delegate(IntPtr h, IntPtr l) {
      StringBuilder c = new StringBuilder(256); GetClassNameW(h, c, 256);
      if (c.ToString().IndexOf("BUTTON", StringComparison.OrdinalIgnoreCase) < 0) return true;
      RECT r; if (!GetWindowRect(h, out r)) return true;
      if (r.Left < bestLeft) { bestLeft = r.Left; best = h; }
      return true;
    }), IntPtr.Zero);
    if (best == IntPtr.Zero) return "no-button";
    Click(best);
    return "clicked-left=" + bestLeft;
  }

  public static string ClickWidth(IntPtr parent, int width) {
    IntPtr found = IntPtr.Zero;
    EnumChildWindows(parent, new EnumProc(delegate(IntPtr h, IntPtr l) {
      StringBuilder c = new StringBuilder(256); GetClassNameW(h, c, 256);
      if (c.ToString().IndexOf("BUTTON", StringComparison.OrdinalIgnoreCase) < 0) return true;
      RECT r; if (!GetWindowRect(h, out r)) return true;
      if (r.Right - r.Left != width) return true;
      found = h; return false;
    }), IntPtr.Zero);
    if (found == IntPtr.Zero) return "no-button-width-" + width;
    Click(found);
    return "clicked-width-" + width;
  }
}
"@

switch ($Action) {
  "list"      { Write-Output ([Win]::List($Target)) }
  "close" {
    $h = [Win]::Find($Target, $Match, $true)
    if ($h -eq [IntPtr]::Zero) { Write-Output "not-found" }
    else { [Win]::PostMessageW($h, 0x0010, [IntPtr]::Zero, [IntPtr]::Zero) | Out-Null; Write-Output ("closed " + $h.ToInt64()) }
  }
  "visible" {
    $h = [Win]::Find($Target, $Match, $true)
    Write-Output ($(if ($h -eq [IntPtr]::Zero) { "hidden" } else { "visible " + $h.ToInt64() }))
  }
  "children"  { Write-Output ([Win]::Children([Win]::Form($Target, $Match))) }
  "edits"     { Write-Output ([Win]::Edits([Win]::Form($Target, $Match))) }
  "buttons"   { Write-Output ([Win]::ButtonList([Win]::Form($Target, $Match))) }
  "clickok"   { Write-Output ([Win]::ClickRightmostWide([Win]::Form($Target, $Match))) }
  "clickleft" { Write-Output ([Win]::ClickLeftmost([Win]::Form($Target, $Match))) }
  "clickw"    { Write-Output ([Win]::ClickWidth([Win]::Form($Target, "frpc"), [int]$Match)) }
}
