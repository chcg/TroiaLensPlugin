<#
  Copies out\TroiaLens into the Notepad++ plugins folder. Asks for administrator rights when needed.
  Close Notepad++ first: it locks the plugin DLL while running.

    .\install.ps1                         # C:\Program Files\Notepad++
    .\install.ps1 -NppDir D:\Apps\Npp     # another (e.g. portable) installation
#>
param([string]$NppDir = (Join-Path $env:ProgramFiles 'Notepad++'))

$ErrorActionPreference = 'Stop'
$src = Join-Path $PSScriptRoot 'out\TroiaLens'
if (-not (Test-Path (Join-Path $src 'TroiaLens.dll'))) { throw 'Önce .\build.ps1 çalıştırın.' }
if (-not (Test-Path (Join-Path $NppDir 'notepad++.exe'))) { throw "Notepad++ bulunamadı: $NppDir" }

$running = Get-Process notepad++ -ErrorAction SilentlyContinue | Where-Object { $_.Path -and $_.Path.StartsWith($NppDir, [StringComparison]::OrdinalIgnoreCase) }
if ($running) { throw 'Notepad++ açık. Kapatıp tekrar deneyin.' }

$dest = Join-Path $NppDir 'plugins\TroiaLens'
$copy = "if (Test-Path '$dest') { Remove-Item -Recurse -Force '$dest' }; Copy-Item -Recurse '$src' '$dest'"

$canWrite = $true
try {
    $probe = Join-Path $NppDir 'plugins\.troialens_write_test'
    [IO.File]::WriteAllText($probe, '')
    Remove-Item $probe
}
catch { $canWrite = $false }

if ($canWrite) {
    Invoke-Expression $copy
}
else {
    Write-Host 'Yönetici izni isteniyor...'
    $p = Start-Process powershell -Verb RunAs -Wait -PassThru -ArgumentList @('-NoProfile', '-Command', $copy)
    if ($p.ExitCode -ne 0) { throw "Kopyalama başarısız ($($p.ExitCode))" }
}

Write-Host "Kuruldu: $dest"
