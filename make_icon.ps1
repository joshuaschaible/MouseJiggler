Add-Type -AssemblyName System.Drawing

$ErrorActionPreference = 'Stop'

function New-IconBitmap {
    param([int]$Size)

    $bmp = New-Object System.Drawing.Bitmap($Size, $Size, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $g.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
    $g.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
    $g.TextRenderingHint = [System.Drawing.Text.TextRenderingHint]::AntiAliasGridFit

    # Scale helper
    $s = $Size / 256.0

    # --- Background: rounded square with blue gradient ---
    $bgRect = New-Object System.Drawing.RectangleF([single]0, [single]0, [single]$Size, [single]$Size)
    $radius = [Math]::Max(1, [int](44 * $s))

    $path = New-Object System.Drawing.Drawing2D.GraphicsPath
    $d = $radius * 2
    $path.AddArc($bgRect.X, $bgRect.Y, $d, $d, 180, 90)
    $path.AddArc($bgRect.Right - $d, $bgRect.Y, $d, $d, 270, 90)
    $path.AddArc($bgRect.Right - $d, $bgRect.Bottom - $d, $d, $d, 0, 90)
    $path.AddArc($bgRect.X, $bgRect.Bottom - $d, $d, $d, 90, 90)
    $path.CloseFigure()

    $c1 = [System.Drawing.Color]::FromArgb(255, 36, 142, 255)
    $c2 = [System.Drawing.Color]::FromArgb(255, 0,  86, 179)
    $grad = New-Object System.Drawing.Drawing2D.LinearGradientBrush($bgRect, $c1, $c2, 90)
    $g.FillPath($grad, $path)
    $grad.Dispose()

    # Subtle inner highlight at the top
    $hiRect = New-Object System.Drawing.RectangleF([single]0, [single]0, [single]$Size, [single]($Size * 0.45))
    $hiColor1 = [System.Drawing.Color]::FromArgb(60, 255, 255, 255)
    $hiColor2 = [System.Drawing.Color]::FromArgb(0,  255, 255, 255)
    $hiGrad = New-Object System.Drawing.Drawing2D.LinearGradientBrush($hiRect, $hiColor1, $hiColor2, 90)
    $g.SetClip($path)
    $g.FillRectangle($hiGrad, $hiRect)
    $g.ResetClip()
    $hiGrad.Dispose()

    # --- Motion arcs on the left and right ---
    # Hand-drawn squiggle that suggests horizontal jiggle motion
    $arcPenWidth = [Math]::Max(1, [single](10 * $s))
    $arcColor = [System.Drawing.Color]::FromArgb(220, 255, 255, 255)
    $arcPen = New-Object System.Drawing.Pen($arcColor, $arcPenWidth)
    $arcPen.StartCap = [System.Drawing.Drawing2D.LineCap]::Round
    $arcPen.EndCap   = [System.Drawing.Drawing2D.LineCap]::Round

    # Left arc (concave to the right)
    $leftRect = New-Object System.Drawing.RectangleF([single](18 * $s), [single](100 * $s), [single](40 * $s), [single](56 * $s))
    $g.DrawArc($arcPen, $leftRect, 270, 180)

    # Right arc (concave to the left)
    $rightRect = New-Object System.Drawing.RectangleF([single](198 * $s), [single](100 * $s), [single](40 * $s), [single](56 * $s))
    $g.DrawArc($arcPen, $rightRect, 90, 180)

    # Small arrowheads on the arc tips
    $tipBrush = New-Object System.Drawing.SolidBrush($arcColor)
    $tipSize = [single](16 * $s)

    # Left arrowhead pointing left
    $leftTip = @(
        (New-Object System.Drawing.PointF([single](14 * $s), [single](128 * $s))),
        (New-Object System.Drawing.PointF([single](34 * $s), [single](118 * $s))),
        (New-Object System.Drawing.PointF([single](34 * $s), [single](138 * $s)))
    )
    $g.FillPolygon($tipBrush, $leftTip)

    # Right arrowhead pointing right
    $rightTip = @(
        (New-Object System.Drawing.PointF([single](242 * $s), [single](128 * $s))),
        (New-Object System.Drawing.PointF([single](222 * $s), [single](118 * $s))),
        (New-Object System.Drawing.PointF([single](222 * $s), [single](138 * $s)))
    )
    $g.FillPolygon($tipBrush, $rightTip)

    $tipBrush.Dispose()
    $arcPen.Dispose()

    # --- Mouse body (centered) ---
    # Mouse outline: rounded "egg" shape, white fill
    $mouseW = [single](96 * $s)
    $mouseH = [single](140 * $s)
    $mouseX = ($Size - $mouseW) / 2
    $mouseY = ($Size - $mouseH) / 2 + [single](2 * $s)

    $mousePath = New-Object System.Drawing.Drawing2D.GraphicsPath
    # Build symmetrical egg via two arcs
    $mousePath.AddArc($mouseX, $mouseY, $mouseW, $mouseW, 180, 180)              # top semicircle
    $mousePath.AddArc($mouseX, $mouseY + $mouseH - $mouseW * 1.05, $mouseW, $mouseW * 1.05, 0, 180)  # bottom oval cap
    $mousePath.CloseFigure()

    $mouseFill = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb(255, 255, 255, 255))
    $g.FillPath($mouseFill, $mousePath)
    $mouseFill.Dispose()

    # Mouse outline stroke (very subtle)
    $strokePen = New-Object System.Drawing.Pen([System.Drawing.Color]::FromArgb(60, 0, 0, 0), [single](2 * $s))
    $g.DrawPath($strokePen, $mousePath)
    $strokePen.Dispose()

    # Center divider line (split between left/right buttons)
    $dividerPen = New-Object System.Drawing.Pen([System.Drawing.Color]::FromArgb(80, 0, 80, 160), [single](3 * $s))
    $dividerPen.StartCap = [System.Drawing.Drawing2D.LineCap]::Round
    $dividerPen.EndCap   = [System.Drawing.Drawing2D.LineCap]::Round
    $g.DrawLine($dividerPen,
        ($mouseX + $mouseW / 2), ($mouseY + [single](14 * $s)),
        ($mouseX + $mouseW / 2), ($mouseY + [single](56 * $s)))
    $dividerPen.Dispose()

    # Scroll wheel
    $wheelW = [single](14 * $s)
    $wheelH = [single](22 * $s)
    $wheelX = $mouseX + ($mouseW - $wheelW) / 2
    $wheelY = $mouseY + [single](22 * $s)
    $wheelBrush = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb(255, 0, 86, 179))
    $g.FillEllipse($wheelBrush, $wheelX, $wheelY, $wheelW, $wheelH)
    $wheelBrush.Dispose()

    # Cable nub at the top
    $cableBrush = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb(255, 230, 230, 230))
    $cableW = [single](10 * $s)
    $cableH = [single](8 * $s)
    $cableX = $mouseX + ($mouseW - $cableW) / 2
    $cableY = $mouseY - $cableH / 2
    $g.FillEllipse($cableBrush, $cableX, $cableY, $cableW, $cableH)
    $cableBrush.Dispose()

    $g.Dispose()
    return $bmp
}

function Save-Ico {
    param(
        [int[]]$Sizes,
        [string]$Path
    )

    $images = @()
    foreach ($sz in $Sizes) {
        $bmp = New-IconBitmap -Size $sz
        $ms = New-Object System.IO.MemoryStream
        $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
        $images += [pscustomobject]@{ Size = $sz; Bytes = $ms.ToArray() }
        $ms.Dispose()
        $bmp.Dispose()
    }

    $out = New-Object System.IO.MemoryStream
    $bw  = New-Object System.IO.BinaryWriter($out)

    # ICONDIR
    $bw.Write([uint16]0)                  # reserved
    $bw.Write([uint16]1)                  # type: 1 = icon
    $bw.Write([uint16]$images.Count)      # image count

    # ICONDIRENTRY headers
    $headerSize  = 6
    $entrySize   = 16
    $dataOffset  = $headerSize + ($entrySize * $images.Count)

    foreach ($img in $images) {
        $w = if ($img.Size -ge 256) { 0 } else { $img.Size }   # 0 means 256
        $h = if ($img.Size -ge 256) { 0 } else { $img.Size }
        $bw.Write([byte]$w)               # width
        $bw.Write([byte]$h)               # height
        $bw.Write([byte]0)                # color count
        $bw.Write([byte]0)                # reserved
        $bw.Write([uint16]1)              # color planes
        $bw.Write([uint16]32)             # bits per pixel
        $bw.Write([uint32]$img.Bytes.Length)  # size of image data
        $bw.Write([uint32]$dataOffset)        # offset to image data
        $dataOffset += $img.Bytes.Length
    }

    foreach ($img in $images) {
        $bw.Write($img.Bytes)
    }

    [System.IO.File]::WriteAllBytes($Path, $out.ToArray())
    $bw.Dispose()
    $out.Dispose()
}

$icoPath = Join-Path $PSScriptRoot 'MouseJiggler.ico'
Save-Ico -Sizes @(16, 32, 48, 64, 128, 256) -Path $icoPath
Write-Host "Wrote $icoPath ($((Get-Item $icoPath).Length) bytes)"

# Also write a 256px PNG preview for quick visual review
$preview = New-IconBitmap -Size 256
$previewPath = Join-Path $PSScriptRoot 'MouseJiggler_preview.png'
$preview.Save($previewPath, [System.Drawing.Imaging.ImageFormat]::Png)
$preview.Dispose()
Write-Host "Wrote $previewPath"
