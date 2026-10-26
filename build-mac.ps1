# Publishes the macOS app for Apple Silicon and Intel and packs each into a zipped .app.
# Output: dist\ClaudeUsageMonitor-v<version>-macos-arm64.zip / -x64.zip, each with a .sha256.
$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$project = Join-Path $root 'mac\ClaudeUsageMonitor.Mac.csproj'
$version = ([xml](Get-Content $project -Raw)).Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1
$dist = Join-Path $root 'dist'
$python = 'C:\Users\USER\AppData\Local\Programs\Python\Python314\python.exe'
if (-not (Test-Path $python)) { $python = 'python' }

foreach ($arch in 'arm64', 'x64') {
    $publish = Join-Path $dist "mac-publish-$arch"
    if (Test-Path $publish) { Remove-Item $publish -Recurse -Force }
    dotnet publish $project -c Release -r "osx-$arch" --self-contained true -p:UseAppHost=true -p:DebugType=none -o $publish
    if ($LASTEXITCODE -ne 0) { throw "dotnet publish osx-$arch failed ($LASTEXITCODE)" }
    & $python -I (Join-Path $root 'tools\package_mac.py') $publish $arch $version $dist $root
    if ($LASTEXITCODE -ne 0) { throw "packaging osx-$arch failed ($LASTEXITCODE)" }
}
