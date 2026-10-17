# Measures the League client against the Glamour panel, for matching the panel's look by numbers instead of by eye.
# Renders both windows with PrintWindow (works while covered by other windows) into tools/logs/shots/, prints their rects in physical px,
# and lists the gold-framed buttons in the bottom 44 px of each (x, rows, height, frame colour). At a 1280x720 client, physical px = the
# panel's css px. -Sample x,y[,x,y...] prints pixel colours of the client render.
#   powershell -File tools/client-shot.ps1
#   powershell -File tools/client-shot.ps1 -Sample 1249,700,1247,687
param([string]$Sample = '')
Add-Type -AssemblyName System.Drawing
Add-Type @"
using System; using System.Collections.Generic; using System.Runtime.InteropServices;
public static class W {
  public delegate bool EnumProc(IntPtr h, IntPtr l);
  [DllImport("user32.dll")] public static extern IntPtr FindWindow(string c, string n);
  [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
  [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
  [DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr h, IntPtr dc, uint f);
  [DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc p, IntPtr l);
  [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
  [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
  public struct RECT { public int L, T, R, B; }
  public static List<IntPtr> Of(uint pid) { var l = new List<IntPtr>(); EnumWindows((h, _) => { uint p; GetWindowThreadProcessId(h, out p); if (p == pid && IsWindowVisible(h)) l.Add(h); return true; }, IntPtr.Zero); return l; }
}
"@
[W]::SetProcessDPIAware() | Out-Null
$dir = Join-Path $PSScriptRoot 'logs\shots'; New-Item -ItemType Directory -Force $dir | Out-Null

function Rect($h) { $r = New-Object W+RECT; [W]::GetWindowRect($h, [ref]$r) | Out-Null; $r }
function Shot($h, $name) {
  $r = Rect $h; $w = $r.R - $r.L; $hh = $r.B - $r.T
  $bmp = New-Object Drawing.Bitmap $w, $hh; $g = [Drawing.Graphics]::FromImage($bmp); $dc = $g.GetHdc()
  [W]::PrintWindow($h, $dc, 2) | Out-Null; $g.ReleaseHdc($dc); $g.Dispose()
  $file = Join-Path $dir "$name.png"; $bmp.Save($file)
  Write-Host "$name ${w}x$hh at $($r.L),$($r.T) -> $file"
  return $bmp
}
function Hex($c) { '#{0:X2}{1:X2}{2:X2}' -f $c.R, $c.G, $c.B }
# A frame column: a vertical run of >= 18 gold pixels in the bottom 44 rows.
function Frames($b, $x0) {
  $top = $b.Height - 44
  for ($x = $x0; $x -lt $b.Width; $x++) {
    $run = 0; $best = 0; $s = 0; $bs = 0
    for ($y = $top; $y -lt $b.Height; $y++) {
      $c = $b.GetPixel($x, $y)
      if ($c.R -gt 90 -and $c.G -gt 65 -and ($c.R - $c.B) -gt 35) { if ($run -eq 0) { $s = $y }; $run++; if ($run -gt $best) { $best = $run; $bs = $s } } else { $run = 0 }
    }
    if ($best -ge 18) { "  frame column x=$x rows $bs..$($bs + $best - 1) (h $best) $(Hex $b.GetPixel($x, [int]($bs + $best / 2)))" }
  }
}

$client = [W]::FindWindow('RCLIENT', [NullString]::Value)
if ($client -eq [IntPtr]::Zero) { throw 'League client window not found' }
$cb = Shot $client 'client'
Frames $cb ([Math]::Max(0, $cb.Width - 300))
$pts = @($Sample -split ',' | Where-Object { $_ } | ForEach-Object { [int]$_ })
for ($i = 0; $i + 1 -lt $pts.Count; $i += 2) { "  client pixel $($pts[$i]),$($pts[$i + 1]) = $(Hex $cb.GetPixel($pts[$i], $pts[$i + 1]))" }

$glamour = Get-Process Glamour -ErrorAction SilentlyContinue | Select-Object -First 1
if (-not $glamour) { 'Glamour is not running: no panel to measure'; return }
$n = 0
foreach ($h in [W]::Of([uint32]$glamour.Id)) {
  $r = Rect $h
  # The panel is the window glued to the client's edge (the main window is elsewhere).
  $cr = Rect $client
  if (($r.T -eq $cr.T) -and ($r.L -eq $cr.R -or $r.R -eq $cr.L)) { $pb = Shot $h "panel$n"; Frames $pb 0; $n++ }
}
if ($n -eq 0) { 'No panel window found beside the client' }
