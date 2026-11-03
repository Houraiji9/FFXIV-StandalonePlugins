param([string]$Destination = (Join-Path $PSScriptRoot '../.sdk/dalamud-26-11-03-01'))
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$Destination = [IO.Path]::GetFullPath($Destination)
$cache = Split-Path $Destination -Parent
New-Item -ItemType Directory -Force -Path $cache, $Destination | Out-Null

function Get-VerifiedFile([string]$Url, [string]$Path, [string]$Sha256) {
    if (!(Test-Path -LiteralPath $Path) -or (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash -ne $Sha256) {
        Invoke-WebRequest -Uri $Url -OutFile $Path
    }
    if ((Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash -ne $Sha256) {
        throw "SHA256 mismatch: $Url"
    }
}

# Published upstream assets; hashes checked against GitHub release asset digests.
$archive = Join-Path $cache 'dalamud-26-11-03-01.7z'
Get-VerifiedFile 'https://github.com/Dalamud-DailyRoutines/Dalamud/releases/download/26-11-03-01/latest.7z' $archive '2d07b8d657dd1fc8624730c7af5eab622fbca1f1dbaf6a9cd3b14559ced73893'
$extractor = Join-Path $cache '7zr-26.05.exe'
Get-VerifiedFile 'https://github.com/ip7z/7zip/releases/download/26.05/7zr.exe' $extractor '9bf582b894dfa665f852d7e9112b02290a1c303c2d95a21fcdc41ffcb03143b7'
& $extractor x $archive "-o$Destination" -y -bso0
if ($LASTEXITCODE -ne 0) { throw 'Dalamud reference extraction failed.' }
if ((Get-FileHash -LiteralPath (Join-Path $Destination 'Dalamud.dll') -Algorithm SHA256).Hash -ne '4e24cf51b1472021539affd527364f74f1484fbd621c1c5642f7b25191917189') {
    throw 'Unexpected Dalamud.dll after extraction.'
}
Write-Host "Dalamud API 15 references: $Destination"
