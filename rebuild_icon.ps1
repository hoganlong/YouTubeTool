Add-Type -AssemblyName System.Drawing

$srcPath = "D:\Projects\YouTubeTool\YouTubeTool.ico"
$dstPath = $srcPath

# Load source (single 128x128 entry)
$src = [System.Drawing.Icon]::new($srcPath, 128, 128)
$srcBmp = $src.ToBitmap()
$src.Dispose()

# Target sizes for taskbar / title bar / shell at various DPIs
$sizes = @(16, 20, 24, 32, 40, 48, 64, 96, 128, 256)

# Pre-encode each size as PNG
$encoded = @()
foreach ($size in $sizes) {
    $bmp = New-Object System.Drawing.Bitmap $size, $size, ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
    $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::HighQuality
    $g.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
    $g.CompositingQuality = [System.Drawing.Drawing2D.CompositingQuality]::HighQuality
    $g.Clear([System.Drawing.Color]::Transparent)
    $g.DrawImage($srcBmp, (New-Object System.Drawing.Rectangle 0, 0, $size, $size))
    $g.Dispose()

    $ms = New-Object System.IO.MemoryStream
    $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    $bmp.Dispose()

    $encoded += [PSCustomObject]@{ Size = $size; Data = $ms.ToArray() }
}
$srcBmp.Dispose()

# Build ICO file
$out = New-Object System.IO.MemoryStream
$bw = New-Object System.IO.BinaryWriter $out

# ICONDIR
$bw.Write([UInt16]0)              # reserved
$bw.Write([UInt16]1)              # type = ICO
$bw.Write([UInt16]$sizes.Count)   # image count

# ICONDIRENTRY (16 bytes each)
$offset = 6 + 16 * $sizes.Count
foreach ($e in $encoded) {
    $w = if ($e.Size -ge 256) { 0 } else { $e.Size }
    $h = if ($e.Size -ge 256) { 0 } else { $e.Size }
    $bw.Write([byte]$w)
    $bw.Write([byte]$h)
    $bw.Write([byte]0)            # palette count
    $bw.Write([byte]0)            # reserved
    $bw.Write([UInt16]1)          # color planes
    $bw.Write([UInt16]32)         # bits per pixel
    $bw.Write([UInt32]$e.Data.Length)
    $bw.Write([UInt32]$offset)
    $offset += $e.Data.Length
}

# Image payloads
foreach ($e in $encoded) {
    $bw.Write($e.Data)
}

$bw.Flush()
[System.IO.File]::WriteAllBytes($dstPath, $out.ToArray())
$bw.Dispose()

Write-Output ("Wrote " + $dstPath + " with " + $sizes.Count + " sizes (" + ($sizes -join ", ") + "); total " + (Get-Item $dstPath).Length + " bytes")
