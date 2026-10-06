param(
  [Parameter(Mandatory=$true)][int]$Target,
  [Parameter(Mandatory=$true)][string]$Ico
)
Add-Type -AssemblyName System.Drawing

Add-Type @"
using System;
using System.Runtime.InteropServices;
using System.Text;
public class Ico {
  public delegate bool EnumProc(IntPtr h, IntPtr l);
  [DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc cb, IntPtr l);
  [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
  [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
  [DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern int GetClassNameW(IntPtr h, StringBuilder s, int n);
  [DllImport("user32.dll")] public static extern IntPtr SendMessageW(IntPtr h, uint msg, IntPtr w, IntPtr l);
  [DllImport("user32.dll", EntryPoint="GetClassLongPtrW")] public static extern IntPtr GetClassLongPtr(IntPtr h, int index);
  public static IntPtr FindForm(uint target) {
    IntPtr found = IntPtr.Zero;
    EnumWindows(new EnumProc(delegate(IntPtr h, IntPtr l) {
      uint pid; GetWindowThreadProcessId(h, out pid);
      if (pid != target || !IsWindowVisible(h)) return true;
      StringBuilder c = new StringBuilder(256); GetClassNameW(h, c, 256);
      if (c.ToString().IndexOf("WindowsForms10.Window.", StringComparison.Ordinal) < 0) return true;
      found = h; return false;
    }), IntPtr.Zero);
    return found;
  }
}
"@

function Pixels([System.Drawing.Bitmap]$b) {
  $r = New-Object System.Drawing.Rectangle 0, 0, $b.Width, $b.Height
  $d = $b.LockBits($r, [System.Drawing.Imaging.ImageLockMode]::ReadOnly, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
  $buf = New-Object byte[] ($d.Stride * $b.Height)
  [System.Runtime.InteropServices.Marshal]::Copy($d.Scan0, $buf, 0, $buf.Length)
  $b.UnlockBits($d)
  $sha = [Security.Cryptography.SHA256]::Create()
  return ($sha.ComputeHash($buf) | ForEach-Object { $_.ToString('x2') }) -join ''
}

$h = [Ico]::FindForm($Target)
if ($h -eq [IntPtr]::Zero) { Write-Output "form not found"; exit 1 }

$hSmall = [Ico]::SendMessageW($h, 0x007F, [IntPtr]0, [IntPtr]0)   # WM_GETICON / ICON_SMALL
if ($hSmall -eq [IntPtr]::Zero) {
  $hSmall = [Ico]::GetClassLongPtr($h, -34)                        # GCLP_HICONSM
  Write-Output "WM_GETICON empty, used class icon"
}
if ($hSmall -eq [IntPtr]::Zero) { Write-Output "window has NO small icon"; exit 0 }

$win = [System.Drawing.Icon]::FromHandle($hSmall).ToBitmap()
Write-Output ("window small icon: " + $win.Width + "x" + $win.Height + "  hash=" + (Pixels $win).Substring(0,16))

$fs = [IO.File]::OpenRead($Ico)
$mine = (New-Object System.Drawing.Icon($fs, (New-Object System.Drawing.Size($win.Width, $win.Height)))).ToBitmap()
$fs.Close()
Write-Output ("app.ico frame:     " + $mine.Width + "x" + $mine.Height + "  hash=" + (Pixels $mine).Substring(0,16))

$generic = [System.Drawing.SystemIcons]::Application.ToBitmap()
$g2 = New-Object System.Drawing.Bitmap($generic, $win.Width, $win.Height)
Write-Output ("generic app icon:  " + $g2.Width + "x" + $g2.Height + "  hash=" + (Pixels $g2).Substring(0,16))

Write-Output ("MATCH app.ico: " + ((Pixels $win) -eq (Pixels $mine)))
Write-Output ("MATCH generic: " + ((Pixels $win) -eq (Pixels $g2)))
