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

# PrintWindow can return TRUE and still hand back an empty frame for this Skia
# GL window — validated 2026-08-07, where it silently produced blank captures of
# a window that was rendering correctly on screen, and cost an hour of chasing a
# non-existent "blank page" regression. A success return is not evidence of
# pixels, so check, and fall back to a real screen grab.
function Test-Blank {
    param([System.Drawing.Bitmap]$Image)

    $seen = @{}
    for ($x = 4; $x -lt $Image.Width - 4; $x += 37) {
        for ($y = 4; $y -lt $Image.Height - 4; $y += 37) {
            $seen[$Image.GetPixel($x, $y).ToArgb()] = $true
            if ($seen.Count -gt 3) { return $false }
        }
    }
    return $true
}

if (Test-Blank -Image $bitmap) {
    Write-Warning "PrintWindow returned an empty frame; falling back to a screen grab (occlusion matters)."

    $bitmap.Dispose()

    [void][Native.Win]::SetForegroundWindow($hwnd)
    Start-Sleep -Milliseconds 500

    $wr = New-Object Native.Win+RECT
    [void][Native.Win]::GetWindowRect($hwnd, [ref]$wr)

    $bitmap = New-Object System.Drawing.Bitmap ($wr.Right - $wr.Left), ($wr.Bottom - $wr.Top)
    $screen = [System.Drawing.Graphics]::FromImage($bitmap)
    $screen.CopyFromScreen(
        $wr.Left, $wr.Top, 0, 0,
        (New-Object System.Drawing.Size ($wr.Right - $wr.Left), ($wr.Bottom - $wr.Top)))
    $screen.Dispose()

    $width = $bitmap.Width
    $height = $bitmap.Height
}

$directory = Split-Path -Parent $OutputPath
if ($directory -and -not (Test-Path $directory)) {
    New-Item -ItemType Directory -Force -Path $directory | Out-Null
}

$bitmap.Save($OutputPath, [System.Drawing.Imaging.ImageFormat]::Png)
$bitmap.Dispose()

Write-Output "Captured $width x $height to $OutputPath"
