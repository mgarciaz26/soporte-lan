# Genera docs/img/agente.png: captura la tarjeta del PIN y sustituye el nombre
# del equipo y la IP por datos de ejemplo, para no publicar datos reales.
#
#   powershell -File tools\captura-agente.ps1

$ErrorActionPreference = "Stop"
Add-Type -AssemblyName System.Drawing, System.Windows.Forms

Add-Type -Namespace Captura -Name Win -MemberDefinition @'
[DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern IntPtr FindWindow(string cls, string win);
[DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr hwnd, out RECT rect);
[DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr hwnd, IntPtr hdc, uint flags);
[DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
public struct RECT { public int Left; public int Top; public int Right; public int Bottom; }
'@

# Sin esto, GetWindowRect devuelve coordenadas lógicas y la captura sale recortada.
[Captura.Win]::SetProcessDPIAware() | Out-Null

$root = Split-Path $PSScriptRoot -Parent
$output = Join-Path $root "docs\img\agente.png"
$exe = Join-Path $root "agent\bin\Release\net9.0-windows\win-x64\publish\soporte-agent.exe"

Get-Process soporte-agent -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Milliseconds 400

$agent = Start-Process -FilePath $exe -ArgumentList "--preview" -PassThru
Start-Sleep -Milliseconds 1800

$handle = [Captura.Win]::FindWindow("SoporteLanPinWindow", "Soporte LAN")
if ($handle -eq [IntPtr]::Zero) {
    Stop-Process -Id $agent.Id -Force
    throw "No se encontró la tarjeta del PIN."
}

$rect = New-Object Captura.Win+RECT
[Captura.Win]::GetWindowRect($handle, [ref]$rect) | Out-Null
$width = $rect.Right - $rect.Left
$height = $rect.Bottom - $rect.Top
$scale = $width / 380.0

$card = New-Object System.Drawing.Bitmap($width, $height, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
$hdc = $null
$graphics = [System.Drawing.Graphics]::FromImage($card)

try {
    $hdc = $graphics.GetHdc()
    if (-not [Captura.Win]::PrintWindow($handle, $hdc, 2)) { throw "PrintWindow falló." }
}
finally {
    if ($hdc) { $graphics.ReleaseHdc($hdc) }
}

# Sustituye las líneas con datos reales por datos de ejemplo.
$background = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb(255, 14, 18, 26))
$graphics.FillRectangle($background, [single](14 * $scale), [single](30 * $scale), [single](352 * $scale), [single](52 * $scale))

$graphics.TextRenderingHint = [System.Drawing.Text.TextRenderingHint]::ClearTypeGridFit
$nameFont = New-Object System.Drawing.Font("Segoe UI Semibold", [single](16 * $scale), [System.Drawing.FontStyle]::Regular, [System.Drawing.GraphicsUnit]::Pixel)
$metaFont = New-Object System.Drawing.Font("Segoe UI", [single](12 * $scale), [System.Drawing.FontStyle]::Regular, [System.Drawing.GraphicsUnit]::Pixel)

$nameColor = [System.Drawing.Color]::FromArgb(255, 230, 237, 243)
$metaColor = [System.Drawing.Color]::FromArgb(255, 138, 153, 173)

$flags = [System.Windows.Forms.TextFormatFlags]::NoPadding
[System.Windows.Forms.TextRenderer]::DrawText($graphics, "PC-RECEPCION", $nameFont,
    (New-Object System.Drawing.Point([int](18 * $scale), [int](32 * $scale))), $nameColor, $flags)
[System.Windows.Forms.TextRenderer]::DrawText($graphics, "IP 192.168.1.42  -  puerto 50000", $metaFont,
    (New-Object System.Drawing.Point([int](18 * $scale), [int](56 * $scale))), $metaColor, $flags)

# Lienzo transparente con sombra suave y esquinas redondeadas.
$margin = 34
$canvas = New-Object System.Drawing.Bitmap(($width + $margin * 2), ($height + $margin * 2), [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
$target = [System.Drawing.Graphics]::FromImage($canvas)
$target.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
$target.Clear([System.Drawing.Color]::Transparent)

function New-RoundedPath([single]$x, [single]$y, [single]$w, [single]$h, [single]$r) {
    $path = New-Object System.Drawing.Drawing2D.GraphicsPath
    $d = $r * 2
    $path.AddArc($x, $y, $d, $d, 180, 90)
    $path.AddArc($x + $w - $d, $y, $d, $d, 270, 90)
    $path.AddArc($x + $w - $d, $y + $h - $d, $d, $d, 0, 90)
    $path.AddArc($x, $y + $h - $d, $d, $d, 90, 90)
    $path.CloseFigure()
    return $path
}

$radius = 9 * $scale
for ($step = 10; $step -ge 1; $step--) {
    $alpha = 6
    $shadow = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb($alpha, 0, 0, 0))
    $path = New-RoundedPath ($margin - $step) ($margin - $step + 3) ($width + $step * 2) ($height + $step * 2) ($radius + $step)
    $target.FillPath($shadow, $path)
    $shadow.Dispose(); $path.Dispose()
}

$clip = New-RoundedPath $margin $margin $width $height $radius
$target.SetClip($clip)
$target.DrawImage($card, $margin, $margin, $width, $height)
$target.ResetClip()

New-Item -ItemType Directory -Force -Path (Split-Path $output -Parent) | Out-Null
$canvas.Save($output, [System.Drawing.Imaging.ImageFormat]::Png)

$clip.Dispose(); $target.Dispose(); $canvas.Dispose()
$background.Dispose(); $nameFont.Dispose(); $metaFont.Dispose()
$graphics.Dispose(); $card.Dispose()
Stop-Process -Id $agent.Id -Force -ErrorAction SilentlyContinue

Write-Host "Captura: $output" -ForegroundColor Green
