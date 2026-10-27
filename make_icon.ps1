Add-Type -AssemblyName System.Drawing

$ErrorActionPreference = 'Stop'

$sourcePath = Join-Path $PSScriptRoot 'MouseJiggler_icon.png'
$icoPath = Join-Path $PSScriptRoot 'MouseJiggler.ico'
$previewPath = Join-Path $PSScriptRoot 'MouseJiggler_preview.png'

if (-not (Test-Path $sourcePath)) {
    throw "Icon source not found: $sourcePath"
}

function New-ResizedPngBytes {
    param(
        [System.Drawing.Image]$Source,
        [int]$Size
    )

    $bitmap = New-Object System.Drawing.Bitmap($Size, $Size, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $graphics = [System.Drawing.Graphics]::FromImage($bitmap)

    try {
        $graphics.CompositingMode = [System.Drawing.Drawing2D.CompositingMode]::SourceCopy
        $graphics.CompositingQuality = [System.Drawing.Drawing2D.CompositingQuality]::HighQuality
        $graphics.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
        $graphics.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
        $graphics.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::HighQuality
        $graphics.DrawImage($Source, 0, 0, $Size, $Size)

        $stream = New-Object System.IO.MemoryStream
        try {
            $bitmap.Save($stream, [System.Drawing.Imaging.ImageFormat]::Png)
            # Prevent PowerShell from unrolling the byte array into individual objects.
            return ,$stream.ToArray()
        }
        finally {
            $stream.Dispose()
        }
    }
    finally {
        $graphics.Dispose()
        $bitmap.Dispose()
    }
}

function Save-Ico {
    param(
        [System.Drawing.Image]$Source,
        [int[]]$Sizes,
        [string]$Path
    )

    $images = @()
    foreach ($size in $Sizes) {
        $images += [pscustomobject]@{
            Size = $size
            Bytes = New-ResizedPngBytes -Source $Source -Size $size
        }
    }

    $output = New-Object System.IO.MemoryStream
    $writer = New-Object System.IO.BinaryWriter($output)

    try {
        # ICONDIR
        $writer.Write([uint16]0)
        $writer.Write([uint16]1)
        $writer.Write([uint16]$images.Count)

        $dataOffset = 6 + (16 * $images.Count)

        # ICONDIRENTRY headers
        foreach ($image in $images) {
            $dimension = if ($image.Size -ge 256) { 0 } else { $image.Size }
            $writer.Write([byte]$dimension)
            $writer.Write([byte]$dimension)
            $writer.Write([byte]0)
            $writer.Write([byte]0)
            $writer.Write([uint16]1)
            $writer.Write([uint16]32)
            $writer.Write([uint32]$image.Bytes.Length)
            $writer.Write([uint32]$dataOffset)
            $dataOffset += $image.Bytes.Length
        }

        foreach ($image in $images) {
            $writer.Write($image.Bytes)
        }

        [System.IO.File]::WriteAllBytes($Path, $output.ToArray())
    }
    finally {
        $writer.Dispose()
        $output.Dispose()
    }
}

$source = [System.Drawing.Image]::FromFile($sourcePath)
try {
    # Include the 24 px layer used by Windows in several taskbar and notification views.
    Save-Ico -Source $source -Sizes @(16, 24, 32, 48, 64, 128, 256) -Path $icoPath

    $previewBytes = New-ResizedPngBytes -Source $source -Size 256
    [System.IO.File]::WriteAllBytes($previewPath, $previewBytes)
}
finally {
    $source.Dispose()
}

Write-Host "Wrote $icoPath ($((Get-Item $icoPath).Length) bytes)"
Write-Host "Wrote $previewPath"
