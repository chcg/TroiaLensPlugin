<#
  Builds the web UI and the plugin, then lays out the Notepad++ plugin folder:

    out\TroiaLens\TroiaLens.dll
    out\TroiaLens\Microsoft.Web.WebView2.Core.dll, Microsoft.Web.WebView2.WinForms.dll, WebView2Loader.dll
    out\TroiaLens\web\...            (web\dist)
    out\TroiaLens-<version>-x64.zip

  -SkipWeb reuses the existing web\dist.
#>
param([switch]$SkipWeb)

$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$web = Join-Path $root 'web'
$proj = Join-Path $root 'src\TroiaLens\TroiaLens.csproj'
$bin = Join-Path $root 'src\TroiaLens\bin\Release'
$out = Join-Path $root 'out'
$pkg = Join-Path $out 'TroiaLens'

if (-not $SkipWeb) {
    Push-Location $web
    try {
        if (-not (Test-Path node_modules)) { npm ci; if ($LASTEXITCODE) { throw 'npm ci başarısız' } }
        npm run build
        if ($LASTEXITCODE) { throw 'Web derlemesi başarısız' }
    }
    finally { Pop-Location }
}
if (-not (Test-Path (Join-Path $web 'dist\index.html'))) { throw 'web\dist bulunamadı; -SkipWeb olmadan çalıştırın.' }

dotnet build $proj -c Release --nologo
if ($LASTEXITCODE) { throw 'Eklenti derlemesi başarısız' }

if (Test-Path $pkg) { Remove-Item -Recurse -Force $pkg }
New-Item -ItemType Directory $pkg | Out-Null
foreach ($f in 'TroiaLens.dll', 'Microsoft.Web.WebView2.Core.dll', 'Microsoft.Web.WebView2.WinForms.dll') {
    Copy-Item (Join-Path $bin $f) $pkg
}
Copy-Item (Join-Path $bin 'runtimes\win-x64\native\WebView2Loader.dll') $pkg
Copy-Item -Recurse (Join-Path $web 'dist') (Join-Path $pkg 'web')
# The sample trace only serves the browser preview
Remove-Item (Join-Path $pkg 'web\sample-trace.txt') -ErrorAction SilentlyContinue

$version = (Get-Item (Join-Path $pkg 'TroiaLens.dll')).VersionInfo.ProductVersion -replace '\+.*$', ''
$zip = Join-Path $out "TroiaLens-$version-x64.zip"
if (Test-Path $zip) { Remove-Item $zip }
Compress-Archive -Path $pkg -DestinationPath $zip

Write-Host ''
Write-Host "Eklenti klasörü: $pkg"
Write-Host "Zip:             $zip"
