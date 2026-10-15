<#
  Builds the plugin and publishes out\TroiaLens-<version>-x64.zip to the GitHub Releases page of this repo.

  The version comes from <Version> in src\TroiaLens\TroiaLens.csproj; the release tag is v<version>.
  If that release already exists, its zip is replaced and the tag is moved to the current commit.

  Needs the GitHub CLI (gh), signed in with "gh auth login". The working tree must be clean and pushed,
  so the zip matches a commit on GitHub.
#>
$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
Set-Location $root

# True when gh exits with 0. Its output is dropped; with 'Stop', Windows PowerShell would turn redirected stderr into an error
function Test-Gh([string[]]$ghArgs) {
    $ErrorActionPreference = 'Continue'
    & gh @ghArgs *> $null
    return $LASTEXITCODE -eq 0
}

if (-not (Get-Command gh -ErrorAction SilentlyContinue)) { throw 'GitHub CLI (gh) bulunamadı: https://cli.github.com' }
if (-not (Test-Gh 'auth', 'status')) { throw 'GitHub CLI oturumu yok. Önce "gh auth login" çalıştırın.' }

if (git status --porcelain) { throw 'Commit edilmemiş değişiklikler var. Önce commit edin.' }
git fetch --quiet origin
$head = git rev-parse HEAD
$branch = git rev-parse --abbrev-ref HEAD
if (-not (git branch -r --contains $head | Where-Object { $_.Trim() -eq "origin/$branch" })) {
    throw "Bu commit GitHub'da yok. Önce push edin."
}

& (Join-Path $root 'build.ps1')

$dll = Join-Path $root 'out\TroiaLens\TroiaLens.dll'
$version = (Get-Item $dll).VersionInfo.ProductVersion -replace '\+.*$', ''
$zip = Join-Path $root "out\TroiaLens-$version-x64.zip"
$tag = "v$version"
if (-not (Test-Path $zip)) { throw "Zip bulunamadı: $zip" }

if (Test-Gh 'release', 'view', $tag) {
    $answer = Read-Host "$tag zaten yayında. Zip değiştirilsin ve etiket bu commit'e taşınsın mı? (e/h)"
    if ($answer -notmatch '^[eEyY]') { Write-Host 'Vazgeçildi.'; exit 0 }
    git tag -f $tag $head
    git push -f origin "refs/tags/$tag"
    if ($LASTEXITCODE) { throw 'Etiket gönderilemedi' }
    gh release upload $tag $zip --clobber
    if ($LASTEXITCODE) { throw 'Zip yüklenemedi' }
}
else {
    gh release create $tag $zip --target $head --title "TroiaLens $version" --generate-notes
    if ($LASTEXITCODE) { throw 'Release oluşturulamadı' }
    git fetch --quiet --tags origin
}

Write-Host ''
Write-Host "Yayınlandı: $tag"
gh release view $tag --json url --jq .url
