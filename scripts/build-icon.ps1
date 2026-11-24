# Rebuild the Apple Reminders on Windows artwork and Windows icon sizes.
# Run on Windows; System.Drawing supplies the vector drawing and PNG encoding.
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing
$repository = Split-Path -Parent $PSScriptRoot
$appAssets = Join-Path $repository 'src/Reminders.WinUI/Assets'
$packageAssets = Join-Path $repository 'src/Reminders.Package/Assets'

function New-RoundedRectangle([single]$x, [single]$y, [single]$width, [single]$height, [single]$radius) {
    $path = [Drawing.Drawing2D.GraphicsPath]::new()
    $diameter = $radius * 2
    $path.AddArc($x, $y, $diameter, $diameter, 180, 90)
    $path.AddArc($x + $width - $diameter, $y, $diameter, $diameter, 270, 90)
    $path.AddArc($x + $width - $diameter, $y + $height - $diameter, $diameter, $diameter, 0, 90)
    $path.AddArc($x, $y + $height - $diameter, $diameter, $diameter, 90, 90)
    $path.CloseFigure()
    return $path
}

function New-TaskCardIcon([int]$size) {
    $canvas = [Drawing.Bitmap]::new(1024, 1024)
    $graphics = [Drawing.Graphics]::FromImage($canvas)
    $graphics.SmoothingMode = [Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $graphics.Clear([Drawing.Color]::Transparent)
    $graphics.ScaleTransform(2, 2)
    # Flat shapes with generous weights remain legible in the 16 px shell frame.
    $card = New-RoundedRectangle 24 24 464 464 88
    $cardBrush = [Drawing.SolidBrush]::new([Drawing.ColorTranslator]::FromHtml('#192B46'))
    $edge = [Drawing.Pen]::new([Drawing.ColorTranslator]::FromHtml('#3E5A7C'), 5)
    $graphics.FillPath($cardBrush, $card)
    $graphics.DrawPath($edge, $card)
    $start = [Drawing.PointF]::new(103, 237)
    $bend = [Drawing.PointF]::new(212, 351)
    $tip = [Drawing.PointF]::new(406, 149)
    $stroke = [Drawing.Pen]::new([Drawing.Color]::White, 68)
    $stroke.StartCap = $stroke.EndCap = [Drawing.Drawing2D.LineCap]::Round
    $stroke.LineJoin = [Drawing.Drawing2D.LineJoin]::Round
    $graphics.DrawLines($stroke, [Drawing.PointF[]]@($start, $bend, $tip))

    # Stripe the short arm perpendicular to its stroke, leaving the bend and long arm white.
    $shortArm = [Drawing.Drawing2D.GraphicsPath]::new()
    $shortArm.AddLine($start, $bend)
    $shortArm.Widen($stroke)
    $state = $graphics.Save()
    $graphics.SetClip($shortArm, [Drawing.Drawing2D.CombineMode]::Intersect)
    $graphics.TranslateTransform($start.X, $start.Y)
    $angle = [Math]::Atan2($bend.Y - $start.Y, $bend.X - $start.X)
    $graphics.RotateTransform([single]($angle * 180 / [Math]::PI))
    $cuts = @(-35, 22, 65, 110)
    $colors = @('#FF453A', '#FFD60A', '#409CFF')
    for ($band = 0; $band -lt $colors.Count; $band++) {
        $brush = [Drawing.SolidBrush]::new([Drawing.ColorTranslator]::FromHtml($colors[$band]))
        $graphics.FillRectangle($brush, $cuts[$band], -35, ($cuts[$band + 1] - $cuts[$band]), 70)
        $brush.Dispose()
    }
    $graphics.Restore($state)
    $shortArm.Dispose(); $stroke.Dispose()
    $card.Dispose(); $cardBrush.Dispose(); $edge.Dispose()
    $result = [Drawing.Bitmap]::new($size, $size)
    $output = [Drawing.Graphics]::FromImage($result)
    $output.InterpolationMode = [Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
    $output.DrawImage($canvas, 0, 0, $size, $size)
    $output.Dispose(); $graphics.Dispose(); $canvas.Dispose()
    return $result
}

$preview = New-TaskCardIcon 512
$preview.Save((Join-Path $appAssets 'icon-v2.png'), [Drawing.Imaging.ImageFormat]::Png)
$preview.Dispose()
foreach ($asset in @(@('StoreLogo.png',50), @('Square44x44Logo.png',44), @('Square150x150Logo.png',150))) {
    $bitmap = New-TaskCardIcon $asset[1]
    $bitmap.Save((Join-Path $packageAssets $asset[0]), [Drawing.Imaging.ImageFormat]::Png)
    $bitmap.Dispose()
}
# PNG-backed ICO frames preserve alpha and provide crisp small/large shell icons.
foreach ($size in @(16,20,24,30,32,36,40,48,60,64,72,80,96,256)) {
    $bitmap = New-TaskCardIcon $size
    foreach ($variant in @('', '_altform-unplated', '_altform-lightunplated')) {
        $bitmap.Save((Join-Path $packageAssets "Square44x44Logo.targetsize-$size$variant.png"), [Drawing.Imaging.ImageFormat]::Png)
    }
    $bitmap.Dispose()
}
foreach ($scale in @(200,400)) {
    foreach ($baseSize in @(44,150)) {
        $bitmap = New-TaskCardIcon ($baseSize * $scale / 100)
        $bitmap.Save((Join-Path $packageAssets "Square$($baseSize)x$($baseSize)Logo.scale-$scale.png"), [Drawing.Imaging.ImageFormat]::Png)
        $bitmap.Dispose()
    }
}
$sizes = @(16,20,24,32,40,48,64,96,128,256)
$frames = foreach ($size in $sizes) {
    $bitmap = New-TaskCardIcon $size
    $buffer = [IO.MemoryStream]::new()
    $bitmap.Save($buffer, [Drawing.Imaging.ImageFormat]::Png)
    ,$buffer.ToArray()
    $buffer.Dispose(); $bitmap.Dispose()
}
$stream = [IO.File]::Create((Join-Path $appAssets 'icon-v2.ico'))
$writer = [IO.BinaryWriter]::new($stream)
try {
    $writer.Write([uint16]0); $writer.Write([uint16]1); $writer.Write([uint16]$sizes.Count)
    $offset = 6 + 16 * $sizes.Count
    for ($index = 0; $index -lt $sizes.Count; $index++) {
        $dimension = if ($sizes[$index] -eq 256) { 0 } else { $sizes[$index] }
        $writer.Write([byte]$dimension); $writer.Write([byte]$dimension)
        $writer.Write([uint16]0); $writer.Write([uint16]1); $writer.Write([uint16]32)
        $writer.Write([uint32]$frames[$index].Length); $writer.Write([uint32]$offset)
        $offset += $frames[$index].Length
    }
    foreach ($frame in $frames) { $writer.Write([byte[]]$frame) }
} finally { $writer.Dispose(); $stream.Dispose() }
