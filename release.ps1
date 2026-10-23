<#
  Builds the plugin and publishes the zip, the MSI and the setup exe from out\ to the GitHub Releases page of this repo.

  The version comes from <Version> in src\TroiaLens\TroiaLens.csproj; the release tag is v<version>.
  If that release already exists, its files are replaced and the tag is moved to the current commit.

  Needs the GitHub CLI (gh), signed in with "gh auth login". The working tree must be clean and pushed,
  so the zip matches a commit on GitHub.

  Afterwards it offers to update the TroiaLens entry in Notepad++'s plugin list (notepad-plus-plus/nppPluginList):
  version, zip hash and zip URL go to src\pl.x64.json in a fork cloned next to this repo (..\nppPluginList), and a
  pull request is opened. While an earlier TroiaLens pull request is still open, its branch gets the new version.
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

# git in the plugin list clone; stops on failure
function Invoke-NppGit([string[]]$gitArgs) {
    git -C $npp @gitArgs
    if ($LASTEXITCODE) { throw "git $($gitArgs -join ' ') başarısız" }
}

function Update-NppPluginList([string]$version, [string]$tag, [string]$zip) {
    $upstream = 'notepad-plus-plus/nppPluginList'
    $script:npp = Join-Path (Split-Path $root) 'nppPluginList'
    $me = gh api user --jq .login
    if ($LASTEXITCODE) { throw 'GitHub kullanıcısı alınamadı' }

    if (-not (Test-Path (Join-Path $npp '.git'))) {
        Push-Location (Split-Path $root)
        try { gh repo fork $upstream --clone --remote; if ($LASTEXITCODE) { throw 'nppPluginList fork edilemedi' } }
        finally { Pop-Location }
    }
    if (git -C $npp status --porcelain --untracked-files=no) { throw "Commit edilmemiş değişiklikler var: $npp" }

    # The list takes the hash of the zip as GitHub serves it
    $name = Split-Path $zip -Leaf
    $release = gh api "repos/$me/TroiaLensPlugin/releases/tags/$tag" | ConvertFrom-Json
    $digest = ($release.assets | Where-Object name -eq $name).digest
    $hash = (Get-FileHash $zip -Algorithm SHA256).Hash.ToLower()
    if ($digest -ne "sha256:$hash") { throw "Yayındaki zip ile yerel zip aynı değil ($digest)" }
    $url = "https://github.com/$me/TroiaLensPlugin/releases/download/$tag/$name"

    # An open pull request gets the new version on its branch instead of a second pull request
    $open = gh pr list --repo $upstream --author $me --state open --search 'TroiaLens in:title' --json number,headRefName,url |
        ConvertFrom-Json | Select-Object -First 1
    Invoke-NppGit 'fetch', '--quiet', 'upstream'
    if ($open) {
        $branch = $open.headRefName
        Invoke-NppGit 'fetch', '--quiet', 'origin', $branch
        Invoke-NppGit 'switch', '--quiet', '-C', $branch, "origin/$branch"
    }
    else {
        $branch = "troialens-$version"
        Invoke-NppGit 'switch', '--quiet', '-C', $branch, 'upstream/master'
    }

    $file = Join-Path $npp 'src\pl.x64.json'
    $text = [IO.File]::ReadAllText($file)
    $entry = [regex]::Match($text, '\{[^{}]*"folder-name":\s*"TroiaLens"[^{}]*\}')
    if (-not $entry.Success) { throw "TroiaLens kaydı bulunamadı: $file" }
    $new = $entry.Value -replace '"version":\s*"[^"]*"', ('"version": "' + $version + '"') `
                        -replace '"id":\s*"[^"]*"', ('"id": "' + $hash + '"') `
                        -replace '"repository":\s*"[^"]*"', ('"repository": "' + $url + '"')
    if ($new -eq $entry.Value) { Write-Host 'nppPluginList kaydı zaten güncel.'; return }
    $text = $text.Remove($entry.Index, $entry.Length).Insert($entry.Index, $new)
    $null = $text | ConvertFrom-Json
    [IO.File]::WriteAllText($file, $text, (New-Object Text.UTF8Encoding $false))

    $title = if ($open) { "Add TroiaLens $version (x64)" } else { "Update TroiaLens to $version (x64)" }
    Invoke-NppGit 'commit', '--quiet', '-m', $title, '--', 'src/pl.x64.json'
    Invoke-NppGit 'push', '--quiet', '--force-with-lease', '-u', 'origin', $branch
    if ($open) {
        gh pr edit $open.number --repo $upstream --title $title
        if ($LASTEXITCODE) { throw 'Pull request başlığı güncellenemedi' }
        Write-Host "nppPluginList pull request'i güncellendi: $($open.url)"
        return
    }
    $body = @"
Updates TroiaLens to $version in the x64 plugin list.

- Release: https://github.com/$me/TroiaLensPlugin/releases/tag/$tag
- ZIP contains TroiaLens.dll at its root.
- ZIP SHA-256: $hash (matches the published asset digest).
- DLL file version: $version.
- Only src/pl.x64.json is changed.
"@
    gh pr create --repo $upstream --base master --head "${me}:$branch" --title $title --body $body
    if ($LASTEXITCODE) { throw 'Pull request açılamadı' }
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
$msiVersion = $version -replace '-.*$', ''
$assets = @($zip, (Join-Path $root "out\TroiaLens-$msiVersion-x64.msi"), (Join-Path $root "out\TroiaLens-Setup-$msiVersion.exe"))
$tag = "v$version"
foreach ($a in $assets) { if (-not (Test-Path $a)) { throw "Dosya bulunamadı: $a" } }

if (Test-Gh 'release', 'view', $tag) {
    # A rebuilt zip has a new hash; the plugin list entry for this version must follow, or Plugins Admin rejects it
    $answer = Read-Host "$tag zaten yayında. Dosyalar değiştirilsin ve etiket bu commit'e taşınsın mı? Zip'in özeti değişir; nppPluginList kaydının da güncellenmesi gerekir. (e/h)"
    if ($answer -notmatch '^[eEyY]') { Write-Host 'Vazgeçildi.'; exit 0 }
    git tag -f $tag $head
    git push -f origin "refs/tags/$tag"
    if ($LASTEXITCODE) { throw 'Etiket gönderilemedi' }
    gh release upload $tag @assets --clobber
    if ($LASTEXITCODE) { throw 'Dosyalar yüklenemedi' }
}
else {
    gh release create $tag @assets --target $head --title "TroiaLens $version" --generate-notes
    if ($LASTEXITCODE) { throw 'Release oluşturulamadı' }
    git fetch --quiet --tags origin
}

Write-Host ''
Write-Host "Yayınlandı: $tag"
gh release view $tag --json url --jq .url

Write-Host ''
$answer = Read-Host "Notepad++ eklenti listesi (nppPluginList) $version sürümüne güncellensin mi? (e/h)"
if ($answer -match '^[eEyY]') { Update-NppPluginList $version $tag $zip }
