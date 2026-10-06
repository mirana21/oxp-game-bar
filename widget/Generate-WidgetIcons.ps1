$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName PresentationCore, WindowsBase
$assets = Join-Path $PSScriptRoot 'Oxp3PowerWidget\Assets'
$icons = Join-Path $assets 'Icons'
[IO.Directory]::CreateDirectory($icons) | Out-Null
[xml]$svg = [IO.File]::ReadAllText((Join-Path $assets 'PowerIcon.svg'))
$shape = [Windows.Media.Geometry]::Parse($svg.svg.path.d)
function Write-PowerIcon([string]$Path, [int]$Width, [int]$Height, [int]$Size, [Windows.Media.Color]$Color) {
    $visual = [Windows.Media.DrawingVisual]::new()
    $drawing = $visual.RenderOpen()
    $drawing.PushTransform([Windows.Media.TranslateTransform]::new(($Width-$Size)/2, ($Height-$Size)/2))
    $drawing.PushTransform([Windows.Media.ScaleTransform]::new($Size/16, $Size/16))
    $drawing.DrawGeometry([Windows.Media.SolidColorBrush]::new($Color), $null, $shape)
    $drawing.Close()
    $bitmap = [Windows.Media.Imaging.RenderTargetBitmap]::new($Width, $Height, 96, 96, [Windows.Media.PixelFormats]::Pbgra32)
    $bitmap.Render($visual)
    $encoder = [Windows.Media.Imaging.PngBitmapEncoder]::new()
    $encoder.Frames.Add([Windows.Media.Imaging.BitmapFrame]::Create($bitmap))
    $stream = [IO.File]::Create($Path)
    try { $encoder.Save($stream) } finally { $stream.Dispose() }
}
foreach ($size in @(16,20,24,32,44,256)) {
    Write-PowerIcon (Join-Path $icons "icon.targetsize-$size.png") $size $size $size ([Windows.Media.Colors]::White)
    Write-PowerIcon (Join-Path $icons "icon.light.targetsize-$size.png") $size $size $size ([Windows.Media.Colors]::Black)
}
Write-PowerIcon (Join-Path $assets 'Square44x44Logo.png') 44 44 40 ([Windows.Media.Colors]::White)
Write-PowerIcon (Join-Path $assets 'Square150x150Logo.png') 150 150 120 ([Windows.Media.Colors]::White)
Write-PowerIcon (Join-Path $assets 'StoreLogo.png') 50 50 44 ([Windows.Media.Colors]::White)
Write-PowerIcon (Join-Path $assets 'SplashScreen.png') 620 300 120 ([Windows.Media.Colors]::White)
Write-Host 'Generated transparent monochrome icons from PowerIcon.svg for both Game Bar themes.'
