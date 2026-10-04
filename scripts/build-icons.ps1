# Package the approved artwork into multi-resolution Windows icons.
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName PresentationCore, WindowsBase
$assets = Join-Path $PSScriptRoot '../src/ModSync/Assets'
foreach ($theme in @('dark', 'light')) {
    $source = [System.Windows.Media.Imaging.BitmapImage]::new([Uri](Join-Path $assets "modsync-$theme.png"))
    $frames = @()
    $sizes = @(16, 20, 24, 32, 40, 48, 64, 128, 256)
    foreach ($size in $sizes) {
        $visual = [System.Windows.Media.DrawingVisual]::new()
        [System.Windows.Media.RenderOptions]::SetBitmapScalingMode($visual, [System.Windows.Media.BitmapScalingMode]::HighQuality)
        $context = $visual.RenderOpen()
        $context.DrawImage($source, [System.Windows.Rect]::new(0, 0, $size, $size))
        $context.Close()
        $bitmap = [System.Windows.Media.Imaging.RenderTargetBitmap]::new($size, $size, 96, 96, [System.Windows.Media.PixelFormats]::Pbgra32)
        $bitmap.Render($visual)
        $encoder = [System.Windows.Media.Imaging.PngBitmapEncoder]::new()
        $encoder.Frames.Add([System.Windows.Media.Imaging.BitmapFrame]::Create($bitmap))
        $memory = [System.IO.MemoryStream]::new()
        $encoder.Save($memory)
        $frames += ,$memory.ToArray()
        $memory.Dispose()
    }
    $stream = [System.IO.File]::Create((Join-Path $assets "modsync-$theme.ico"))
    $writer = [System.IO.BinaryWriter]::new($stream)
    try {
        $writer.Write([uint16]0); $writer.Write([uint16]1); $writer.Write([uint16]$sizes.Count)
        $offset = 6 + 16 * $sizes.Count
        for ($i = 0; $i -lt $sizes.Count; $i++) {
            $dimension = if ($sizes[$i] -eq 256) { 0 } else { $sizes[$i] }
            $writer.Write([byte]$dimension); $writer.Write([byte]$dimension)
            $writer.Write([byte]0); $writer.Write([byte]0)
            $writer.Write([uint16]1); $writer.Write([uint16]32)
            $writer.Write([uint32]$frames[$i].Length); $writer.Write([uint32]$offset)
            $offset += $frames[$i].Length
        }
        foreach ($frame in $frames) { $writer.Write([byte[]]$frame) }
    } finally { $writer.Dispose(); $stream.Dispose() }
}
