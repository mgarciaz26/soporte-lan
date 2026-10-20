# Genera agent/app.ico con varios tamaños, dibujando un cuadrado redondeado
# verde y las iniciales del producto. Solo hay que ejecutarlo si se quiere
# cambiar el icono.
$ErrorActionPreference = "Stop"
Add-Type -AssemblyName System.Drawing

$output = Join-Path (Split-Path $PSScriptRoot -Parent) "app.ico"
$sizes = @(16, 20, 24, 32, 40, 48, 64, 128, 256)
$frames = @()

foreach ($size in $sizes) {
    $bitmap = New-Object System.Drawing.Bitmap($size, $size)
    $graphics = [System.Drawing.Graphics]::FromImage($bitmap)
    $graphics.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $graphics.TextRenderingHint = [System.Drawing.Text.TextRenderingHint]::AntiAliasGridFit
    $graphics.Clear([System.Drawing.Color]::Transparent)

    $radius = [single]($size * 0.22)
    $diameter = [single]($radius * 2)
    $rect = New-Object System.Drawing.RectangleF(0, 0, $size, $size)
    $path = New-Object System.Drawing.Drawing2D.GraphicsPath
    $path.AddArc(0, 0, $diameter, $diameter, 180, 90)
    $path.AddArc($size - $diameter, 0, $diameter, $diameter, 270, 90)
    $path.AddArc($size - $diameter, $size - $diameter, $diameter, $diameter, 0, 90)
    $path.AddArc(0, $size - $diameter, $diameter, $diameter, 90, 90)
    $path.CloseFigure()

    $brush = New-Object System.Drawing.Drawing2D.LinearGradientBrush(
        $rect,
        [System.Drawing.Color]::FromArgb(255, 61, 220, 151),
        [System.Drawing.Color]::FromArgb(255, 18, 122, 92),
        45)
    $graphics.FillPath($brush, $path)

    $label = if ($size -lt 24) { "S" } else { "SL" }
    $fontSize = if ($size -lt 24) { $size * 0.62 } else { $size * 0.42 }
    $font = New-Object System.Drawing.Font(
        "Segoe UI", [single]$fontSize, [System.Drawing.FontStyle]::Bold,
        [System.Drawing.GraphicsUnit]::Pixel)
    $format = New-Object System.Drawing.StringFormat
    $format.Alignment = [System.Drawing.StringAlignment]::Center
    $format.LineAlignment = [System.Drawing.StringAlignment]::Center
    $textRect = New-Object System.Drawing.RectangleF(0, [single]($size * -0.03), $size, $size)
    $graphics.DrawString($label, $font, [System.Drawing.Brushes]::White, $textRect, $format)

    $stream = New-Object System.IO.MemoryStream
    $binary = New-Object System.IO.BinaryWriter($stream)

    # Marco en formato DIB (BMP) clásico: máxima compatibilidad con Windows.
    $rect = New-Object System.Drawing.Rectangle(0, 0, $size, $size)
    $locked = $bitmap.LockBits($rect, [System.Drawing.Imaging.ImageLockMode]::ReadOnly, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $stride = $locked.Stride
    $raw = New-Object byte[] ($stride * $size)
    [System.Runtime.InteropServices.Marshal]::Copy($locked.Scan0, $raw, 0, $raw.Length)
    $bitmap.UnlockBits($locked)

    $binary.Write([uint32]40)
    $binary.Write([int32]$size)
    $binary.Write([int32]($size * 2))
    $binary.Write([uint16]1)
    $binary.Write([uint16]32)
    $binary.Write([uint32]0)
    $binary.Write([uint32]($size * $size * 4))
    $binary.Write([int32]0)
    $binary.Write([int32]0)
    $binary.Write([uint32]0)
    $binary.Write([uint32]0)

    for ($y = 0; $y -lt $size; $y++) {
        $source = ($size - 1 - $y) * $stride
        $binary.Write($raw, $source, $size * 4)
    }

    $maskStride = [int]([math]::Ceiling($size / 32.0) * 4)
    $binary.Write((New-Object byte[] ($maskStride * $size)))

    $frames += [pscustomobject]@{ Size = $size; Data = $stream.ToArray() }

    $binary.Dispose()
    $stream.Dispose()
    $font.Dispose()
    $brush.Dispose()
    $path.Dispose()
    $graphics.Dispose()
    $bitmap.Dispose()
}

$file = New-Object System.IO.MemoryStream
$writer = New-Object System.IO.BinaryWriter($file)
$writer.Write([uint16]0)
$writer.Write([uint16]1)
$writer.Write([uint16]$frames.Count)

$offset = 6 + 16 * $frames.Count
foreach ($frame in $frames) {
    $dimension = if ($frame.Size -ge 256) { 0 } else { $frame.Size }
    $writer.Write([byte]$dimension)
    $writer.Write([byte]$dimension)
    $writer.Write([byte]0)
    $writer.Write([byte]0)
    $writer.Write([uint16]1)
    $writer.Write([uint16]32)
    $writer.Write([uint32]$frame.Data.Length)
    $writer.Write([uint32]$offset)
    $offset += $frame.Data.Length
}

foreach ($frame in $frames) { $writer.Write($frame.Data) }
$writer.Flush()
[System.IO.File]::WriteAllBytes($output, $file.ToArray())
$writer.Dispose()
$file.Dispose()

Write-Host "Icono generado: $output ($((Get-Item $output).Length) bytes, $($frames.Count) tamaños)" -ForegroundColor Green
