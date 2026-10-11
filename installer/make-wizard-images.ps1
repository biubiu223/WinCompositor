# Draws the installer's wizard images from the app icon, in the app's dark colors:
#   images\wizard-*.bmp  the tall picture on the welcome and finish pages (164 x 314 at 100 %)
#   images\small-*.bmp   the icon in the top-right corner of the other pages (55 x 55 at 100 %), with transparency
# Each at 100 %, 150 % and 200 % so the wizard stays sharp on high-DPI screens.
# Run again only if the icon or the look changes; the .bmp files are kept with the installer script.
Add-Type -AssemblyName System.Drawing, PresentationCore, WindowsBase

$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$icon = Join-Path $root "..\src\Compositor.App\Assets\Compositor.ico"
$out = Join-Path $root "images"
New-Item -ItemType Directory -Force $out | Out-Null

# The icon's largest frame (256 px) as a GDI+ bitmap, via WPF (GDI+ alone picks a small frame from .ico files).
$decoder = [System.Windows.Media.Imaging.BitmapDecoder]::Create([Uri](Resolve-Path $icon).Path, 'None', 'OnLoad')
$frame = $decoder.Frames | Sort-Object PixelWidth -Descending | Select-Object -First 1
$encoder = New-Object System.Windows.Media.Imaging.PngBitmapEncoder
$encoder.Frames.Add([System.Windows.Media.Imaging.BitmapFrame]::Create($frame))
$stream = New-Object System.IO.MemoryStream
$encoder.Save($stream)
$stream.Position = 0
$iconBitmap = [System.Drawing.Bitmap]::FromStream($stream)

function New-Canvas([int]$w, [int]$h) {
    $bmp = New-Object System.Drawing.Bitmap($w, $h, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = 'AntiAlias'
    $g.InterpolationMode = 'HighQualityBicubic'
    $g.PixelOffsetMode = 'HighQuality'
    $g.TextRenderingHint = 'AntiAliasGridFit'
    return $bmp, $g
}

foreach ($scale in 1.0, 1.5, 2.0) {
    $tag = [int]($scale * 100)

    # Welcome / finish page picture: a quiet dark gradient (PanelColor to WindowColor), the icon with a faint warm
    # glow taken from its sunset, the name, and "Compositor for Windows" (where the name comes from).
    $w = [int][math]::Round(164 * $scale); $h = [int][math]::Round(314 * $scale)
    $bmp, $g = New-Canvas $w $h
    $rect = New-Object System.Drawing.Rectangle(0, 0, $w, $h)
    $bg = New-Object System.Drawing.Drawing2D.LinearGradientBrush($rect, [System.Drawing.Color]::FromArgb(255, 0x2A, 0x2A, 0x2A), [System.Drawing.Color]::FromArgb(255, 0x1A, 0x1A, 0x1A), 90.0)
    $g.FillRectangle($bg, $rect)
    $iconSize = [int][math]::Round(84 * $scale)
    $cx = $w / 2.0; $cy = $h * 0.36
    $glowPath = New-Object System.Drawing.Drawing2D.GraphicsPath
    $glowR = $iconSize * 1.15
    $glowPath.AddEllipse([float]($cx - $glowR), [float]($cy - $glowR), [float]($glowR * 2), [float]($glowR * 2))
    $glow = New-Object System.Drawing.Drawing2D.PathGradientBrush($glowPath)
    $glow.CenterColor = [System.Drawing.Color]::FromArgb(46, 0xF2, 0x8C, 0x5A)
    $glow.SurroundColors = @([System.Drawing.Color]::FromArgb(0, 0xF2, 0x8C, 0x5A))
    $g.FillPath($glow, $glowPath)
    $g.DrawImage($iconBitmap, [int]($cx - $iconSize / 2), [int]($cy - $iconSize / 2), $iconSize, $iconSize)
    $center = New-Object System.Drawing.StringFormat
    $center.Alignment = 'Center'
    $nameFont = New-Object System.Drawing.Font("Segoe UI Semibold", [float](15 * $scale), [System.Drawing.FontStyle]::Regular, [System.Drawing.GraphicsUnit]::Pixel)
    $subFont = New-Object System.Drawing.Font("Segoe UI", [float](10.5 * $scale), [System.Drawing.FontStyle]::Regular, [System.Drawing.GraphicsUnit]::Pixel)
    $nameY = $cy + $iconSize / 2 + 16 * $scale
    $g.DrawString("WinCompositor", $nameFont, (New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb(255, 0xE6, 0xE6, 0xE6))), (New-Object System.Drawing.RectangleF(0, [float]$nameY, $w, [float](24 * $scale))), $center)
    $g.DrawString("Compositor for Windows", $subFont, (New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb(255, 0x9A, 0x9A, 0x9A))), (New-Object System.Drawing.RectangleF(0, [float]($nameY + 22 * $scale), $w, [float](18 * $scale))), $center)
    $g.Dispose()
    # Opaque 24-bit, the format every Windows version's wizard shows correctly.
    $flat = $bmp.Clone($rect, [System.Drawing.Imaging.PixelFormat]::Format24bppRgb)
    $flat.Save((Join-Path $out "wizard-$tag.bmp"), [System.Drawing.Imaging.ImageFormat]::Bmp)
    $flat.Dispose(); $bmp.Dispose()

    # Corner icon: just the icon, transparent around it (32-bit with alpha; the script says WizardImageAlphaFormat=defined).
    $s = [int][math]::Round(55 * $scale)
    $bmp, $g = New-Canvas $s $s
    $g.Clear([System.Drawing.Color]::Transparent)
    $pad = [int][math]::Round(3 * $scale)
    $g.DrawImage($iconBitmap, $pad, $pad, $s - 2 * $pad, $s - 2 * $pad)
    $g.Dispose()
    $bmp.Save((Join-Path $out "small-$tag.bmp"), [System.Drawing.Imaging.ImageFormat]::Bmp)
    $bmp.Dispose()
}
$iconBitmap.Dispose()
Get-ChildItem $out -Filter *.bmp | ForEach-Object { "{0}  {1:N0} KB" -f $_.Name, ($_.Length / 1KB) }
