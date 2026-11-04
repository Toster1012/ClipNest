param([string]$ProjectRoot = (Join-Path $PSScriptRoot '..\src\ClipNest'))
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing
$sourcePath = Join-Path $ProjectRoot 'Assets\Branding\ClipNest.png'
$destinationPath = Join-Path $ProjectRoot 'Assets\Branding\ClipNest.ico'
$sourceImage = [System.Drawing.Image]::FromFile($sourcePath)
$iconSizes = @(16, 24, 32, 48, 64, 128, 256)
$frames = @()
try {
    foreach ($iconSize in $iconSizes) {
        $bitmap = [System.Drawing.Bitmap]::new($sourceImage, [System.Drawing.Size]::new($iconSize, $iconSize))
        $memory = [System.IO.MemoryStream]::new()
        try {
            $bitmap.Save($memory, [System.Drawing.Imaging.ImageFormat]::Png)
            $frames += ,($memory.ToArray())
        }
        finally { $memory.Dispose(); $bitmap.Dispose() }
    }
}
finally { $sourceImage.Dispose() }
$output = [System.IO.File]::Create($destinationPath)
$writer = [System.IO.BinaryWriter]::new($output)
try {
    $writer.Write([uint16]0)
    $writer.Write([uint16]1)
    $writer.Write([uint16]$frames.Count)
    $offset = 6 + 16 * $frames.Count
    for ($index = 0; $index -lt $frames.Count; $index++) {
        $dimension = $iconSizes[$index] % 256
        $writer.Write([byte]$dimension)
        $writer.Write([byte]$dimension)
        $writer.Write([byte]0)
        $writer.Write([byte]0)
        $writer.Write([uint16]1)
        $writer.Write([uint16]32)
        $writer.Write([uint32]$frames[$index].Length)
        $writer.Write([uint32]$offset)
        $offset += $frames[$index].Length
    }
    foreach ($frame in $frames) { $writer.Write([byte[]]$frame) }
}
finally { $writer.Dispose() }
