param(
    [string]$Tag = 'v0.1.0',
    [string]$Repository = 'Houraiji9/FFXIV-StandalonePlugins',
    [long]$Timestamp = [DateTimeOffset]::UtcNow.ToUnixTimeSeconds(),
    [ValidateSet('BatchTrade', 'RetainerCraftBridge')]
    [string[]]$Plugins = @('BatchTrade', 'RetainerCraftBridge'),
    [string]$OutputDirectory
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
if ($Tag -notmatch '^v\d+\.\d+\.\d+(?:\.\d+)?$') { throw 'Tag must be a version such as v0.1.0.' }
if ($Repository -notmatch '^[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+$') { throw 'Invalid GitHub repository.' }
if ($Timestamp -lt 1) { throw 'Timestamp must be a positive Unix timestamp.' }
if (!$Plugins -or $Plugins.Count -eq 0) { throw 'Select at least one plugin.' }
$supported = @('BatchTrade', 'RetainerCraftBridge')
$selected = @($Plugins | ForEach-Object {
    $requested = $_
    $supported | Where-Object { $_ -ieq $requested }
})
if (@($selected | Select-Object -Unique).Count -ne $selected.Count) { throw 'Duplicate plugin selection.' }
$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$out = if ([string]::IsNullOrWhiteSpace($OutputDirectory)) {
    Join-Path $root "artifacts/$Tag"
} elseif ([IO.Path]::IsPathRooted($OutputDirectory)) {
    [IO.Path]::GetFullPath($OutputDirectory)
} else {
    [IO.Path]::GetFullPath((Join-Path $root $OutputDirectory))
}
# A fresh output directory prevents stale archives from becoming release assets.
# Never recursively delete or silently merge with an earlier packaging attempt.
if (Test-Path -LiteralPath $out) {
    if (!(Test-Path -LiteralPath $out -PathType Container) -or @(Get-ChildItem -LiteralPath $out -Force).Count -ne 0) {
        throw "Output directory must be empty; choose a fresh -OutputDirectory: $out"
    }
}
$tagVersion = [Version]$Tag.Substring(1)
$tagVersion = [Version]::new($tagVersion.Major, $tagVersion.Minor, [Math]::Max(0, $tagVersion.Build), [Math]::Max(0, $tagVersion.Revision))

# Existing entries are the authoritative baseline for plugins not in this release.
$baseline = @(Get-Content -Raw -LiteralPath (Join-Path $root 'pluginmaster.json') | ConvertFrom-Json -AsHashtable)
$seen = @{}
foreach ($entry in $baseline) {
    if ($entry -isnot [System.Collections.IDictionary] -or !$entry.Contains('InternalName') -or
        [string]::IsNullOrWhiteSpace($entry.InternalName)) { throw 'Invalid repository index entry.' }
    if ($seen.ContainsKey($entry.InternalName)) { throw "Duplicate index entry: $($entry.InternalName)" }
    $seen[$entry.InternalName] = $entry
}
$plans = @()
foreach ($name in $selected) {
    $project = Join-Path $root "plugins/$name"
    $manifestPath = Join-Path $project "$name.json"
    $manifest = Get-Content -Raw -LiteralPath $manifestPath | ConvertFrom-Json -AsHashtable
    $dll = Join-Path $project "bin/Release/net10.0-windows/$name.dll"
    $assembly = [Reflection.AssemblyName]::GetAssemblyName($dll)
    $version = $assembly.Version.ToString()
    if ($assembly.Name -cne $name) { throw "Assembly identity mismatch: $name" }
    if ($version -ne $manifest.AssemblyVersion) { throw "Manifest/assembly version mismatch: $name" }
    if ($tagVersion -ne [Version]$version) { throw "Tag/assembly version mismatch: $name" }
    if ($manifest.InternalName -cne $name -or $manifest.DalamudApiLevel -ne 15) { throw "Invalid plugin manifest: $name" }
    if ($seen.ContainsKey($name) -and [Version]$version -lt [Version]$seen[$name].AssemblyVersion) {
        throw "Refusing repository version downgrade: $name"
    }
    $files = @{
        "$name.dll" = $dll
        "$name.json" = $manifestPath
        'LICENSE' = (Join-Path $root 'LICENSE')
        'THIRD-PARTY-NOTICES.md' = (Join-Path $root 'THIRD-PARTY-NOTICES.md')
        'README.md' = (Join-Path $project 'README.md')
    }
    foreach ($license in Get-ChildItem -LiteralPath (Join-Path $root 'licenses') -File) {
        $files["licenses/$($license.Name)"] = $license.FullName
    }
    foreach ($file in $files.Values) {
        if (!(Test-Path -LiteralPath $file -PathType Leaf)) { throw "Package file is missing: $file" }
    }
    $plans += @{ Name = $name; Manifest = $manifest; Files = $files }
}

# All version/identity/file checks complete before creating any package.
New-Item -ItemType Directory -Force -Path $out | Out-Null
$hashes = @()
$replacements = @{}
Add-Type -AssemblyName System.IO.Compression
foreach ($plan in $plans) {
    $name = $plan.Name
    $zipPath = Join-Path $out "$name.zip"
    $stream = [IO.File]::Open($zipPath, [IO.FileMode]::CreateNew)
    $zip = [IO.Compression.ZipArchive]::new($stream, [IO.Compression.ZipArchiveMode]::Create)
    try {
        foreach ($relative in ($plan.Files.Keys | Sort-Object)) {
            $entry = $zip.CreateEntry($relative, [IO.Compression.CompressionLevel]::Optimal)
            $entry.LastWriteTime = [DateTimeOffset]::new(2026, 11, 3, 0, 0, 0, [TimeSpan]::Zero)
            $entryStream = $entry.Open()
            $sourceStream = [IO.File]::OpenRead($plan.Files[$relative])
            try { $sourceStream.CopyTo($entryStream) } finally { $sourceStream.Dispose(); $entryStream.Dispose() }
        }
    }
    finally { $zip.Dispose(); $stream.Dispose() }
    $manifest = $plan.Manifest
    $url = "https://github.com/$Repository/releases/download/$Tag/$name.zip"
    $manifest['DownloadLinkInstall'] = $url
    $manifest['DownloadLinkUpdate'] = $url
    $manifest['DownloadLinkTesting'] = $url
    $manifest['LastUpdate'] = $Timestamp
    $manifest['IsHide'] = $false
    $manifest['IsTestingExclusive'] = $false
    $replacements[$name] = $manifest
    $hashes += "$((Get-FileHash -LiteralPath $zipPath -Algorithm SHA256).Hash.ToLowerInvariant())  $name.zip"
}
$entries = @(
    foreach ($entry in $baseline) {
        if ($replacements.ContainsKey($entry.InternalName)) { $replacements[$entry.InternalName] }
        else { $entry } # Preserve every field/value of unselected plugins, including future fields.
    }
    foreach ($name in $selected) {
        if (!$seen.ContainsKey($name)) { $replacements[$name] }
    }
)
ConvertTo-Json -InputObject $entries -Depth 100 | Set-Content -LiteralPath (Join-Path $out 'pluginmaster.json') -Encoding utf8NoBOM
$hashes | Set-Content -LiteralPath (Join-Path $out 'SHA256SUMS.txt') -Encoding utf8NoBOM
Write-Host "Packaged $($selected -join ', '): $out"
