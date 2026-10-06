param([Parameter(Mandatory=$true)][string]$Ico)
Add-Type -AssemblyName System.Drawing
$b = [IO.File]::ReadAllBytes($Ico)
$n = [BitConverter]::ToUInt16($b, 4)
Write-Output ("frames in directory: " + $n)
for ($i = 0; $i -lt $n; $i++) {
  $o = 6 + 16 * $i
  $w = $b[$o]; if ($w -eq 0) { $w = 256 }
  Write-Output ("  " + $w + "x" + $w + "  " + [BitConverter]::ToInt32($b, $o + 8) + " bytes")
}
Write-Output "load check via System.Drawing.Icon:"
foreach ($sz in 16, 32, 48, 256) {
  $fs = [IO.File]::OpenRead($Ico)
  try {
    $ic = New-Object System.Drawing.Icon($fs, (New-Object System.Drawing.Size($sz, $sz)))
    Write-Output ("  asked " + $sz + " -> got " + $ic.Width + "x" + $ic.Height)
    $ic.Dispose()
  } catch {
    Write-Output ("  asked " + $sz + " -> FAILED: " + $_.Exception.Message)
  } finally { $fs.Close() }
}
