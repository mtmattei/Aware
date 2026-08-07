<#
  Measures where a synthesized pointer move actually lands, in the same
  window-client space the harness aims in. If aim and landing disagree, the
  fault is coordinate math (DPI space), not input delivery.
#>
param([int]$ProcessId, [int]$X = 168, [int]$Y = 210)

Add-Type -AssemblyName System.Windows.Forms

if (-not ('Diag.Input' -as [type])) {
    Add-Type -Namespace Diag -Name Input -MemberDefinition @'
[System.Runtime.InteropServices.DllImport("user32.dll")]
public static extern void mouse_event(uint flags, int dx, int dy, uint data, System.UIntPtr extra);

[System.Runtime.InteropServices.DllImport("user32.dll")]
public static extern bool ClientToScreen(System.IntPtr hwnd, ref POINT point);

[System.Runtime.InteropServices.DllImport("user32.dll")]
public static extern bool ScreenToClient(System.IntPtr hwnd, ref POINT point);

[System.Runtime.InteropServices.DllImport("user32.dll")]
public static extern bool GetCursorPos(out POINT point);

[System.Runtime.InteropServices.DllImport("user32.dll")]
public static extern bool GetWindowRect(System.IntPtr hwnd, out RECT rect);

[System.Runtime.InteropServices.DllImport("user32.dll")]
public static extern bool GetClientRect(System.IntPtr hwnd, out RECT rect);

[System.Runtime.InteropServices.DllImport("user32.dll")]
public static extern System.IntPtr GetForegroundWindow();

[System.Runtime.InteropServices.DllImport("user32.dll")]
public static extern bool SetForegroundWindow(System.IntPtr hwnd);

[System.Runtime.InteropServices.DllImport("user32.dll")]
public static extern uint GetDpiForWindow(System.IntPtr hwnd);

[System.Runtime.InteropServices.DllImport("user32.dll")]
public static extern int GetSystemMetrics(int index);

public struct RECT { public int Left, Top, Right, Bottom; }
public struct POINT { public int X, Y; }
'@
}

$MOVE = 0x0001
$ABSOLUTE = 0x8000

$hwnd = (Get-Process -Id $ProcessId).MainWindowHandle
if ($hwnd -eq 0) { throw "No main window on process $ProcessId." }

[void][Diag.Input]::SetForegroundWindow($hwnd)
Start-Sleep -Milliseconds 400

# --- what each API thinks the world looks like ---------------------------
$wr = New-Object Diag.Input+RECT
[void][Diag.Input]::GetWindowRect($hwnd, [ref]$wr)
$cr = New-Object Diag.Input+RECT
[void][Diag.Input]::GetClientRect($hwnd, [ref]$cr)
$vs = [System.Windows.Forms.SystemInformation]::VirtualScreen

"process DPI aware   : $([System.Windows.Forms.Application]::OpenForms.Count -ge 0)"
"window DPI          : $([Diag.Input]::GetDpiForWindow($hwnd))"
"GetWindowRect       : $($wr.Left),$($wr.Top) .. $($wr.Right),$($wr.Bottom)"
"GetClientRect       : $($cr.Right) x $($cr.Bottom)"
"VirtualScreen (WinF): $($vs.Left),$($vs.Top) $($vs.Width) x $($vs.Height)"
"SM_CXVIRTUALSCREEN  : $([Diag.Input]::GetSystemMetrics(78)) x $([Diag.Input]::GetSystemMetrics(79))"
"foreground == app   : $([Diag.Input]::GetForegroundWindow() -eq $hwnd)"
""

# --- aim ------------------------------------------------------------------
$aim = New-Object Diag.Input+POINT
$aim.X = $X; $aim.Y = $Y
[void][Diag.Input]::ClientToScreen($hwnd, [ref]$aim)
"aim  client ($X,$Y) -> screen ($($aim.X),$($aim.Y))"

$dx = [int](($aim.X - $vs.Left) * 65535 / $vs.Width)
$dy = [int](($aim.Y - $vs.Top) * 65535 / $vs.Height)
"normalized          : $dx,$dy"

[Diag.Input]::mouse_event($MOVE -bor $ABSOLUTE, $dx, $dy, 0, [System.UIntPtr]::Zero)
Start-Sleep -Milliseconds 250

# --- land -----------------------------------------------------------------
$land = New-Object Diag.Input+POINT
[void][Diag.Input]::GetCursorPos([ref]$land)
"land screen         : $($land.X),$($land.Y)"

$landClient = $land
[void][Diag.Input]::ScreenToClient($hwnd, [ref]$landClient)
"land client         : $($landClient.X),$($landClient.Y)"
""
"ERROR screen        : $($land.X - $aim.X),$($land.Y - $aim.Y)"
"ERROR client        : $($landClient.X - $X),$($landClient.Y - $Y)"
