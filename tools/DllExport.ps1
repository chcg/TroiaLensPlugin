<#
  Adds native exports to a .NET Framework x64 assembly so Notepad++ can load it as a plugin.

  Every static method marked with [DllExport] (TroiaLens.Npp.DllExportAttribute) is exported
  under its own name. Steps: ildasm -> replace the attribute with ".export [n] as name" -> ilasm /x64.
#>
param(
    [Parameter(Mandatory = $true)][string]$Assembly
)

$ErrorActionPreference = 'Stop'

function Find-Ildasm {
    $sdk = Join-Path ${env:ProgramFiles(x86)} 'Microsoft SDKs\Windows'
    $found = Get-ChildItem $sdk -Recurse -Filter ildasm.exe -ErrorAction SilentlyContinue |
        Where-Object { $_.FullName -match 'NETFX 4\.\d+(\.\d+)? Tools' } |
        Sort-Object @{ Expression = { $_.FullName -match '\\x64\\' } ; Descending = $true }, FullName -Descending |
        Select-Object -First 1
    if (-not $found) { throw 'ildasm.exe bulunamadı. Visual Studio ".NET Framework 4.8 targeting pack / SDK" bileşenini kurun.' }
    $found.FullName
}

$ildasm = Find-Ildasm
$ilasm = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\ilasm.exe'
if (-not (Test-Path $ilasm)) { throw "ilasm.exe bulunamadı: $ilasm" }

$Assembly = (Resolve-Path $Assembly).Path
$work = Join-Path ([IO.Path]::GetTempPath()) ('troialens_dllexport_' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory $work | Out-Null
Push-Location $work
try {
    & $ildasm $Assembly /out=lib.il /utf8 /nobar | Out-Null
    if ($LASTEXITCODE -ne 0 -or -not (Test-Path lib.il)) { throw "ildasm başarısız ($LASTEXITCODE)" }

    $lines = [IO.File]::ReadAllLines((Join-Path $work 'lib.il'))
    $out = New-Object System.Collections.Generic.List[string]
    $header = $null
    $inHeader = $false
    $method = $null
    $ordinal = 0
    $exported = @()

    foreach ($line in $lines) {
        if ($line -match '^\s*\.corflags\s') {
            # Not IL-only any more: the image now carries a v-table fixup for the exports
            $out.Add('.corflags 0x00000000')
            continue
        }
        if ($line -match '^\s*\.method\s') {
            $header = $line
            $inHeader = $true
        }
        elseif ($inHeader) {
            $header += ' ' + $line.Trim()
        }
        if ($inHeader -and $line -match '\bmanaged\b') {
            $inHeader = $false
            $sig = $header -replace 'marshal\s*\([^)]*\)', '' -replace '^\s*\.method\s+', ''
            if ($sig -match "('[^']+'|[A-Za-z_][\w`$@?]*)\s*\(") { $method = $Matches[1].Trim("'") } else { $method = $null }
        }
        if ($line -match '^\s*\.custom\s+instance\s+void\s+[\w.]*DllExportAttribute::\.ctor\(\)') {
            if (-not $method) { throw "DllExport: metot adı çözülemedi: $header" }
            $ordinal++
            $exported += $method
            $out.Add("    .export [$ordinal] as $method")
            continue
        }
        $out.Add($line)
    }

    if ($ordinal -eq 0) { throw 'DllExport: [DllExport] ile işaretli metot bulunamadı.' }
    [IO.File]::WriteAllLines((Join-Path $work 'lib.il'), $out, (New-Object Text.UTF8Encoding $true))

    $ilArgs = @('lib.il', '/dll', '/x64', '/pe64', '/quiet', '/nologo', "/output=$Assembly")
    if (Test-Path lib.res) { $ilArgs += '/resource=lib.res' }
    & $ilasm @ilArgs
    if ($LASTEXITCODE -ne 0) { throw "ilasm başarısız ($LASTEXITCODE)" }

    Write-Host "DllExport: $($exported -join ', ')"
}
finally {
    Pop-Location
    Remove-Item -Recurse -Force $work -ErrorAction SilentlyContinue
}
