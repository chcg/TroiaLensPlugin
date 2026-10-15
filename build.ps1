<#
  Builds the web UI and the plugin, then lays out the Notepad++ plugin folder:

    out\TroiaLens\TroiaLens.dll
    out\TroiaLens\Microsoft.Web.WebView2.Core.dll, Microsoft.Web.WebView2.WinForms.dll, WebView2Loader.dll
    out\TroiaLens\web\...            (web\dist)
    out\TroiaLens-<version>-x64.zip
    out\TroiaLens-<version>-x64.msi       (plugin only; finds the Notepad++ folder itself)
    out\TroiaLens-Setup-<version>.exe     (single package: .NET 4.8, WebView2, Node.js when missing + the MSI)

  -SkipWeb reuses the existing web\dist. -SkipInstaller skips the MSI and the setup exe.
#>
param([switch]$SkipWeb, [switch]$SkipInstaller)

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

if (-not $SkipInstaller) {
    # MSI versions are numeric only
    $msiVersion = $version -replace '-.*$', ''
    # The MSI picks up out\TroiaLens with a wildcard, so incremental builds could miss changed files
    dotnet build (Join-Path $root 'installer\Bundle\TroiaLens.Bundle.wixproj') -c Release --nologo --no-incremental "-p:ProductVersion=$msiVersion"
    if ($LASTEXITCODE) { throw 'Kurulum paketi derlemesi başarısız' }
    $msi = Join-Path $out "TroiaLens-$msiVersion-x64.msi"
    $setup = Join-Path $out "TroiaLens-Setup-$msiVersion.exe"
    Copy-Item (Join-Path $root "installer\Package\bin\Release\TroiaLens-$msiVersion-x64.msi") $msi -Force
    Copy-Item (Join-Path $root "installer\Bundle\bin\Release\TroiaLens-Setup-$msiVersion.exe") $setup -Force
}

Write-Host ''
Write-Host "Eklenti klasörü: $pkg"
Write-Host "Zip:             $zip"
if (-not $SkipInstaller) {
    Write-Host "MSI:             $msi"
    Write-Host "Kurulum:         $setup"
}
