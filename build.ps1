# Publishes a self-contained single-file exe and zips it with the docs.
# Output: dist\ClaudeUsageMonitor-v<version>-win-x64.zip and a .sha256 next to it.
$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$project = Join-Path $root 'src\ClaudeUsageMonitor.csproj'
$version = ([xml](Get-Content $project -Raw)).Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1
$dist = Join-Path $root 'dist'
$publish = Join-Path $dist "publish-$version"

if (Test-Path $publish) { Remove-Item $publish -Recurse -Force }
dotnet publish $project -c Release -r win-x64 --self-contained true `
    -p:PublishSingleFile=true -p:EnableCompressionInSingleFile=true `
    -p:IncludeNativeLibrariesForSelfExtract=true -p:DebugType=none -o $publish
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed ($LASTEXITCODE)" }

Copy-Item (Join-Path $root 'README.md'), (Join-Path $root 'THIRD_PARTY_NOTICES.md') $publish
# README images, so the copy inside the zip is illustrated too.
$shots = Join-Path $publish 'docs\screenshots'
New-Item -ItemType Directory -Force $shots | Out-Null
Copy-Item (Join-Path $root 'docs\screenshots\*.png') $shots

$zip = Join-Path $dist "ClaudeUsageMonitor-v$version-win-x64.zip"
Compress-Archive -Path (Join-Path $publish '*') -DestinationPath $zip -Force
$hash = (Get-FileHash $zip -Algorithm SHA256).Hash
"$hash  $(Split-Path $zip -Leaf)" | Set-Content -Encoding ascii "$zip.sha256"
Write-Host "Built $zip"
Write-Host "SHA-256 $hash"
