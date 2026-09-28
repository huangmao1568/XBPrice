param(
	[string]$src = "magnifier.png",
	[string]$outDir = ".\Assets\Tiles"
)

if (-not (Test-Path $src)) {
	Write-Error "Source file $src not found. Place the magnifier image at that path or specify -src." 
	exit 1
}

# 找到 magick 可执行文件的路径：优先使用 Get-Command，否则在 Program Files 下搜索
$magPath = $null
try { $magPath = (Get-Command magick -ErrorAction SilentlyContinue).Source } catch { }
if (-not $magPath) {
	$candidates = Get-ChildItem -Path 'C:\Program Files','C:\Program Files (x86)' -Filter 'magick.exe' -Recurse -ErrorAction SilentlyContinue | Select-Object -First 1
	if ($candidates) { $magPath = $candidates.FullName }
}
if (-not $magPath) {
	Write-Error "ImageMagick 'magick' not found. Install ImageMagick (https://imagemagick.org) and ensure 'magick' is on PATH."
	exit 1
}

New-Item -ItemType Directory -Force -Path $outDir | Out-Null

$clean = Join-Path $outDir "_clean.png"
# Convert white background to transparent (adjust -fuzz as needed)
& $magPath $src -fuzz 10% -transparent white $clean

function Resize($w,$h,$name) {
	$path = Join-Path $outDir $name
	& $magPath $clean -resize "${w}x${h}>" -background none -gravity center -extent ${w}x${h} $path
	Write-Output "Generated $path"
}

Resize 44 44 "Square44x44.png"
Resize 71 71 "SmallTile.png"
Resize 150 150 "Square150x150.png"
Resize 310 150 "Wide310x150.png"
Resize 310 310 "Square310x310.png"
Resize 50 50 "StoreLogo.png"
Resize 30 30 "SmallLogo.png"
Resize 620 300 "SplashScreen.png"

Remove-Item $clean -Force -ErrorAction SilentlyContinue

Write-Output "Done. Output directory: $outDir"
