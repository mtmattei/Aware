<#
.SYNOPSIS
  Captures an Uno Skia desktop window with PrintWindow(PW_RENDERFULLCONTENT).

.DESCRIPTION
  gdigrab and desktop-region capture both record whatever pixels sit on screen,
  which fails for an occluded or off-screen window. PrintWindow with
  PW_RENDERFULLCONTENT (0x2) asks the window to render itself, so the capture is
  occlusion-proof and works for the GL-composited Skia swapchain.

  Add-Type notes for .NET 10 / PowerShell 7: no -UsingNamespace (Add-Type already
  emits the InteropServices using, so passing it is CS0105), and no System.Drawing
  types inside the C# string (Bitmap/Graphics sit behind type-forwards Add-Type
  cannot resolve from source). The GDI+ work happens in PowerShell instead.
#>
param(
    [Parameter(Mandatory = $true)][int]$ProcessId,
    [Parameter(Mandatory = $true)][string]$OutputPath
)

$ErrorActionPreference = 'Stop'

Add-Type -AssemblyName System.Drawing

if (-not ('Native.Win' -as [type])) {
    Add-Type -Namespace Native -Name Win -MemberDefinition @'
[System.Runtime.InteropServices.DllImport("user32.dll")]
public static extern bool PrintWindow(System.IntPtr hwnd, System.IntPtr hdc, uint flags);

[System.Runtime.InteropServices.DllImport("user32.dll")]
public static extern bool GetClientRect(System.IntPtr hwnd, out RECT rect);

[System.Runtime.InteropServices.DllImport("user32.dll")]
public static extern bool GetWindowRect(System.IntPtr hwnd, out RECT rect);

[System.Runtime.InteropServices.DllImport("user32.dll")]
public static extern bool SetForegroundWindow(System.IntPtr hwnd);

[System.Runtime.InteropServices.DllImport("user32.dll")]
public static extern bool IsWindowVisible(System.IntPtr hwnd);

public struct RECT { public int Left, Top, Right, Bottom; }
'@
}

$process = Get-Process -Id $ProcessId
$hwnd = $process.MainWindowHandle

if ($hwnd -eq [System.IntPtr]::Zero) {
    throw "Process $ProcessId has no main window handle yet."
}

$rect = New-Object Native.Win+RECT
[void][Native.Win]::GetWindowRect($hwnd, [ref]$rect)

$width = $rect.Right - $rect.Left
$height = $rect.Bottom - $rect.Top

if ($width -le 0 -or $height -le 0) {
    throw "Window rect is empty ($width x $height)."
}

$bitmap = New-Object System.Drawing.Bitmap $width, $height
$graphics = [System.Drawing.Graphics]::FromImage($bitmap)
$hdc = $graphics.GetHdc()

try {
    # 0x2 = PW_RENDERFULLCONTENT
    $ok = [Native.Win]::PrintWindow($hwnd, $hdc, 2)
}
finally {
    $graphics.ReleaseHdc($hdc)
}

$graphics.Dispose()

if (-not $ok) {
    $bitmap.Dispose()
    throw "PrintWindow failed for handle $hwnd."
}

$directory = Split-Path -Parent $OutputPath
if ($directory -and -not (Test-Path $directory)) {
    New-Item -ItemType Directory -Force -Path $directory | Out-Null
}

$bitmap.Save($OutputPath, [System.Drawing.Imaging.ImageFormat]::Png)
$bitmap.Dispose()

Write-Output "Captured $width x $height to $OutputPath"
