param(
    [string]$Tag = 'v0.1.0',
    [string]$Repository = 'Houraiji9/FFXIV-StandalonePlugins',
    [long]$Timestamp = [DateTimeOffset]::UtcNow.ToUnixTimeSeconds()
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
if ($Tag -notmatch '^v\d+\.\d+\.\d+(?:\.\d+)?$') { throw 'Tag must be a version such as v0.1.0.' }
if ($Repository -notmatch '^[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+$') { throw 'Invalid GitHub repository.' }
$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$out = Join-Path $root 'artifacts'
New-Item -ItemType Directory -Force -Path $out | Out-Null
$entries = @()
$hashes = @()
Add-Type -AssemblyName System.IO.Compression
foreach ($name in @('BatchTrade', 'RetainerCraftBridge')) {
    $project = Join-Path $root "plugins/$name"
    $manifest = Get-Content -Raw -LiteralPath (Join-Path $project "$name.json") | ConvertFrom-Json -AsHashtable
    $dll = Join-Path $project "bin/Release/net10.0-windows/$name.dll"
    $version = [Reflection.AssemblyName]::GetAssemblyName($dll).Version.ToString()
    if ($version -ne $manifest.AssemblyVersion) { throw "Manifest/assembly version mismatch: $name" }
    $tagVersion = [Version]$Tag.Substring(1)
    $tagVersion = [Version]::new($tagVersion.Major, $tagVersion.Minor, [Math]::Max(0, $tagVersion.Build), [Math]::Max(0, $tagVersion.Revision))
    if ($tagVersion -ne [Version]$version) { throw "Tag/assembly version mismatch: $name" }
    if ($manifest.InternalName -ne $name -or $manifest.DalamudApiLevel -ne 15) { throw "Invalid plugin manifest: $name" }
    $zipPath = Join-Path $out "$name.zip"
    # FileMode.Create replaces only this generated archive; no recursive cleanup.
    $stream = [IO.File]::Open($zipPath, [IO.FileMode]::Create)
    $zip = [IO.Compression.ZipArchive]::new($stream, [IO.Compression.ZipArchiveMode]::Create)
    try {
        $files = @{
            "$name.dll" = $dll
            "$name.json" = (Join-Path $project "$name.json")
            'LICENSE' = (Join-Path $root 'LICENSE')
            'THIRD-PARTY-NOTICES.md' = (Join-Path $root 'THIRD-PARTY-NOTICES.md')
            'README.md' = (Join-Path $project 'README.md')
        }
        foreach ($license in Get-ChildItem -LiteralPath (Join-Path $root 'licenses') -File) {
            $files["licenses/$($license.Name)"] = $license.FullName
        }
        foreach ($relative in ($files.Keys | Sort-Object)) {
            $entry = $zip.CreateEntry($relative, [IO.Compression.CompressionLevel]::Optimal)
            $entry.LastWriteTime = [DateTimeOffset]::new(2026, 11, 3, 0, 0, 0, [TimeSpan]::Zero)
            $entryStream = $entry.Open()
            $sourceStream = [IO.File]::OpenRead($files[$relative])
            try { $sourceStream.CopyTo($entryStream) } finally { $sourceStream.Dispose(); $entryStream.Dispose() }
        }
    }
    finally { $zip.Dispose(); $stream.Dispose() }
    $url = "https://github.com/$Repository/releases/download/$Tag/$name.zip"
    $manifest['DownloadLinkInstall'] = $url
    $manifest['DownloadLinkUpdate'] = $url
    $manifest['DownloadLinkTesting'] = $url
    $manifest['LastUpdate'] = $Timestamp
    $manifest['IsHide'] = $false
    $manifest['IsTestingExclusive'] = $false
    $entries += $manifest
    $hashes += "$((Get-FileHash -LiteralPath $zipPath -Algorithm SHA256).Hash.ToLowerInvariant())  $name.zip"
}
ConvertTo-Json -InputObject $entries -Depth 10 | Set-Content -LiteralPath (Join-Path $out 'pluginmaster.json') -Encoding utf8NoBOM
$hashes | Set-Content -LiteralPath (Join-Path $out 'SHA256SUMS.txt') -Encoding utf8NoBOM
Write-Host "Packages and repository index: $out"
