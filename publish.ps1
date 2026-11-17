param([string]$Version = "")

$ErrorActionPreference = "Stop"
$root = $PSScriptRoot
$proj = Join-Path $root "Moirai.App\Moirai.App.csproj"
$outDir = Join-Path $root "Moirai.App\bin\portable\win-x64"

$verArg = @()
if ($Version -ne "") { $verArg = @("-p:Version=$Version") }

Write-Host "Publishing portable self-contained build (loose DLLs)..." -ForegroundColor Cyan
if (Test-Path $outDir) { Remove-Item $outDir -Recurse -Force }
dotnet publish $proj -p:PublishProfile=portable-win-x64 @verArg
if ($LASTEXITCODE -ne 0) { throw "publish failed" }

Get-ChildItem $outDir -Filter *.pdb -Recurse | Remove-Item -Force

$exe = Join-Path $outDir "Moirai.App.exe"
$ver = (Get-Item $exe).VersionInfo.ProductVersion
if (-not $ver) { $ver = "1.0.0" }
$ver = ($ver -split '\+')[0]

$zip = Join-Path $root "MOIRAI-$ver-portable-win-x64.zip"
if (Test-Path $zip) { Remove-Item $zip -Force }
Compress-Archive -Path (Join-Path $outDir '*') -DestinationPath $zip
Write-Host "Done." -ForegroundColor Green
Write-Host "  Folder: $outDir"
Write-Host "  Zip:    $zip"
Write-Host ""
Write-Host "On the target PC: extract the zip (right-click the zip > Properties > Unblock first if Windows tagged it), then run Moirai.App.exe. Edit update-config.json next to the exe to point at your GitHub / GitLab."
