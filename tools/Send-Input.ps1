<#
.SYNOPSIS
  Synthesizes pointer input against an Uno Skia desktop window.

.DESCRIPTION
  SetCursorPos moves are invisible to an Uno Skia app: press and release arrive,
  but the pointer never appears to move, so a scripted drag reads as a press with
  zero travel. Movement has to be injected through the input stream with
  mouse_event(MOUSEEVENTF_MOVE | MOUSEEVENTF_ABSOLUTE, ...) in 0..65535 units.

  Coordinates are given in window-client space and converted here.

.EXAMPLE
  . .\tools\Send-Input.ps1
  Send-AwareClick -ProcessId 123 -X 529 -Y 423
  Send-AwareDrag  -ProcessId 123 -X1 500 -Y1 400 -X2 640 -Y2 430
#>

Add-Type -AssemblyName System.Windows.Forms

if (-not ('Native.Input' -as [type])) {
    Add-Type -Namespace Native -Name Input -MemberDefinition @'
[System.Runtime.InteropServices.DllImport("user32.dll")]
public static extern void mouse_event(uint flags, int dx, int dy, uint data, System.UIntPtr extra);

[System.Runtime.InteropServices.DllImport("user32.dll")]
public static extern bool GetWindowRect(System.IntPtr hwnd, out RECT rect);

[System.Runtime.InteropServices.DllImport("user32.dll")]
public static extern bool ClientToScreen(System.IntPtr hwnd, ref POINT point);

[System.Runtime.InteropServices.DllImport("user32.dll")]
public static extern bool SetForegroundWindow(System.IntPtr hwnd);

public struct RECT { public int Left, Top, Right, Bottom; }
public struct POINT { public int X, Y; }
'@
}

$script:MOUSEEVENTF_MOVE = 0x0001
$script:MOUSEEVENTF_LEFTDOWN = 0x0002
$script:MOUSEEVENTF_LEFTUP = 0x0004
$script:MOUSEEVENTF_ABSOLUTE = 0x8000

function Get-AwareWindow {
    param([int]$ProcessId)
    $process = Get-Process -Id $ProcessId
    if ($process.MainWindowHandle -eq [System.IntPtr]::Zero) {
        throw "Process $ProcessId has no main window."
    }
    $process.MainWindowHandle
}

function ConvertTo-ScreenPoint {
    param([System.IntPtr]$Hwnd, [int]$X, [int]$Y)
    $point = New-Object Native.Input+POINT
    $point.X = $X
    $point.Y = $Y
    [void][Native.Input]::ClientToScreen($Hwnd, [ref]$point)
    $point
}

function Move-AwarePointer {
    param([System.IntPtr]$Hwnd, [int]$X, [int]$Y)

    $screen = ConvertTo-ScreenPoint -Hwnd $Hwnd -X $X -Y $Y
    $bounds = [System.Windows.Forms.SystemInformation]::VirtualScreen

    $dx = [int](($screen.X - $bounds.Left) * 65535 / $bounds.Width)
    $dy = [int](($screen.Y - $bounds.Top) * 65535 / $bounds.Height)

    [Native.Input]::mouse_event(
        $script:MOUSEEVENTF_MOVE -bor $script:MOUSEEVENTF_ABSOLUTE,
        $dx, $dy, 0, [System.UIntPtr]::Zero)
}

function Send-AwareClick {
    param([int]$ProcessId, [int]$X, [int]$Y)

    $hwnd = Get-AwareWindow -ProcessId $ProcessId
    [void][Native.Input]::SetForegroundWindow($hwnd)
    Start-Sleep -Milliseconds 220

    Move-AwarePointer -Hwnd $hwnd -X $X -Y $Y
    Start-Sleep -Milliseconds 140
    [Native.Input]::mouse_event($script:MOUSEEVENTF_LEFTDOWN, 0, 0, 0, [System.UIntPtr]::Zero)
    Start-Sleep -Milliseconds 70
    [Native.Input]::mouse_event($script:MOUSEEVENTF_LEFTUP, 0, 0, 0, [System.UIntPtr]::Zero)
    Start-Sleep -Milliseconds 260
}

function Send-AwareDrag {
    param([int]$ProcessId, [int]$X1, [int]$Y1, [int]$X2, [int]$Y2, [int]$Steps = 18)

    $hwnd = Get-AwareWindow -ProcessId $ProcessId
    [void][Native.Input]::SetForegroundWindow($hwnd)
    Start-Sleep -Milliseconds 220

    Move-AwarePointer -Hwnd $hwnd -X $X1 -Y $Y1
    Start-Sleep -Milliseconds 120
    [Native.Input]::mouse_event($script:MOUSEEVENTF_LEFTDOWN, 0, 0, 0, [System.UIntPtr]::Zero)
    Start-Sleep -Milliseconds 80

    for ($i = 1; $i -le $Steps; $i++) {
        $x = [int]($X1 + ($X2 - $X1) * $i / $Steps)
        $y = [int]($Y1 + ($Y2 - $Y1) * $i / $Steps)
        Move-AwarePointer -Hwnd $hwnd -X $x -Y $y
        Start-Sleep -Milliseconds 16
    }

    Start-Sleep -Milliseconds 60
    [Native.Input]::mouse_event($script:MOUSEEVENTF_LEFTUP, 0, 0, 0, [System.UIntPtr]::Zero)
    Start-Sleep -Milliseconds 300
}
