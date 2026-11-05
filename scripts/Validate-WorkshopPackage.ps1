#requires -Version 7.0
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$PackageDirectory,
    [Parameter(Mandatory)][string]$BaselineIndex
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
# Read only: do not extract files, load plugin code, build, modify the index, or contact the game.
$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$package = [IO.Path]::GetFullPath($PackageDirectory)
$baselinePath = [IO.Path]::GetFullPath($BaselineIndex)
$name = 'RetainerWorkshop'
$version = '0.1.8.0'
$tag = 'v0.1.8'
$game = '2026.10.06.0000.0000'
$repository = 'https://github.com/Houraiji9/FFXIV-StandalonePlugins'
$oldVersions = [ordered]@{ BatchTrade = '0.1.5.0'; RetainerCraftBridge = '0.1.4.0'; RetainerVendorSweep = '0.1.6.0' }

function Assert-ExactNames([string[]]$Actual, [string[]]$Expected, [string]$Label) {
    if ($Actual.Count -ne $Expected.Count) { throw "$Label has the wrong entry count." }
    $seen = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    foreach ($item in $Actual) {
        if (!$seen.Add($item)) { throw "$Label has a duplicate entry: $item" }
    }
    foreach ($item in $Expected) {
        if (!$seen.Contains($item)) { throw "$Label is missing an exact allowed name: $item" }
    }
}

function Assert-UniqueJsonProperties([System.Text.Json.JsonElement]$Element, [int]$Depth = 0) {
    if ($Depth -gt 90) { throw 'JSON nesting is too deep.' }
    if ($Element.ValueKind -eq [System.Text.Json.JsonValueKind]::Object) {
        $seen = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
        foreach ($property in $Element.EnumerateObject()) {
            if (!$seen.Add($property.Name)) { throw "Duplicate JSON property: $($property.Name)" }
            Assert-UniqueJsonProperties $property.Value ($Depth + 1)
        }
    } elseif ($Element.ValueKind -eq [System.Text.Json.JsonValueKind]::Array) {
        foreach ($item in $Element.EnumerateArray()) { Assert-UniqueJsonProperties $item ($Depth + 1) }
    }
}

function Read-JsonText([string]$Text) {
    $options = [System.Text.Json.JsonDocumentOptions]::new()
    $options.MaxDepth = 100
    $document = [System.Text.Json.JsonDocument]::Parse($Text, $options)
    try { Assert-UniqueJsonProperties $document.RootElement }
    finally { $document.Dispose() }
    # Preserve nested arrays, including empty and single-element arrays.
    return ,(ConvertFrom-Json -InputObject $Text -AsHashtable -NoEnumerate -Depth 100)
}

function Read-JsonFile([string]$Path) {
    $file = Get-Item -LiteralPath $Path
    if ($file.PSIsContainer -or $file.Length -gt 33554432) { throw 'JSON input is not a bounded file.' }
    return ,(Read-JsonText ([IO.File]::ReadAllText($file.FullName)))
}

function ConvertTo-CanonicalValue($Value) {
    if ($null -eq $Value) { return $null }
    if ($Value -is [Collections.IDictionary]) {
        $result = [ordered]@{}
        [string[]]$keys = @($Value.Keys)
        [Array]::Sort($keys, [StringComparer]::Ordinal)
        foreach ($key in $keys) { $result[$key] = ConvertTo-CanonicalValue $Value[$key] }
        return $result
    }
    if ($Value -is [Collections.IEnumerable] -and $Value -isnot [string]) {
        $items = [Collections.Generic.List[object]]::new()
        foreach ($item in $Value) { $items.Add((ConvertTo-CanonicalValue $item)) }
        return ,$items.ToArray()
    }
    return $Value
}

function Canonical-Json($Value) {
    $normalized = ConvertTo-CanonicalValue $Value
    return ConvertTo-Json -InputObject $normalized -Compress -Depth 100
}

function Assert-SameJson($Actual, $Expected, [string]$Label) {
    if ((Canonical-Json $Actual) -cne (Canonical-Json $Expected)) { throw "$Label changed after deep normalization." }
}

function Assert-Index($Index, [string[]]$Names, [string]$Label) {
    if ($Index -isnot [array]) { throw "$Label must be a JSON array." }
    $actual = @(
        foreach ($entry in $Index) {
            if ($entry -isnot [Collections.IDictionary] -or !$entry.Contains('InternalName') -or
                $entry.InternalName -isnot [string]) { throw "$Label has an invalid entry." }
            $entry.InternalName
        }
    )
    Assert-ExactNames $actual $Names $Label
}

function Assert-Manifest($Manifest, [string]$Label) {
    if ($Manifest -isnot [Collections.IDictionary]) { throw "$Label is not an object." }
    foreach ($key in @('InternalName', 'AssemblyVersion', 'DalamudApiLevel', 'ApplicableVersion', 'RepoUrl', 'CanUnloadAsync')) {
        if (!$Manifest.Contains($key)) { throw "$Label is missing $key." }
    }
    if ($Manifest.InternalName -cne $name -or $Manifest.AssemblyVersion -cne $version -or
        $Manifest.DalamudApiLevel -ne 15 -or $Manifest.ApplicableVersion -cne $game -or
        $Manifest.RepoUrl -cne $repository -or $Manifest.CanUnloadAsync -isnot [bool] -or $Manifest.CanUnloadAsync) {
        throw "$Label identity, version, API, game version, repository, or unload flag differs."
    }
}

function Assert-NoPrivatePath([byte[]]$Bytes, [string]$Label) {
    $texts = @([Text.Encoding]::UTF8.GetString($Bytes), [Text.Encoding]::Unicode.GetString($Bytes))
    if ($Bytes.Length -gt 1) { $texts += [Text.Encoding]::Unicode.GetString($Bytes, 1, $Bytes.Length - 1) }
    foreach ($text in $texts) {
        if ($text -match '(?i)[a-z]:[\\/](?:Users|Agent|项目|a)[\\/]|[a-z]:[\\/](?:NewMoe|FFXIV)[\\/]|/home/runner/|/Users/') {
            throw "Private user/build path marker found in $Label."
        }
    }
}

function Read-ZipBytes($Entry) {
    if ($null -eq $Entry -or $Entry.Length -lt 1 -or $Entry.Length -gt 33554432) { throw 'Missing, empty, or oversized ZIP entry.' }
    $source = $Entry.Open()
    $buffer = [IO.MemoryStream]::new()
    try { $source.CopyTo($buffer); return ,$buffer.ToArray() }
    finally { $source.Dispose(); $buffer.Dispose() }
}

if (!(Test-Path -LiteralPath $package -PathType Container)) { throw 'PackageDirectory must already exist.' }
if (!(Test-Path -LiteralPath $baselinePath -PathType Leaf)) { throw 'BaselineIndex must already be a file.' }
$indexPath = Join-Path $package 'pluginmaster.json'
if ($baselinePath.Equals($indexPath, [StringComparison]::OrdinalIgnoreCase)) { throw 'BaselineIndex must be the pre-release index, not the candidate index.' }
$expectedFiles = @("$name.zip", 'pluginmaster.json', 'SHA256SUMS.txt')
Assert-ExactNames @(Get-ChildItem -LiteralPath $package -Force | Select-Object -ExpandProperty Name) $expectedFiles 'Package directory'
foreach ($file in $expectedFiles) {
    if (!(Test-Path -LiteralPath (Join-Path $package $file) -PathType Leaf)) { throw "Expected a regular file: $file" }
}
$baseline = Read-JsonFile $baselinePath
$index = Read-JsonFile $indexPath
$baselineNames = @($oldVersions.Keys)
# A post-release main checkout already contains Workshop; it is the only allowed extra baseline entry.
if ($baseline -is [array] -and $baseline.Count -eq 4) { $baselineNames += $name }
Assert-Index $baseline $baselineNames 'Baseline index'
Assert-Index $index @('BatchTrade', 'RetainerCraftBridge', 'RetainerVendorSweep', $name) 'Candidate index'
foreach ($oldName in $oldVersions.Keys) {
    $before = @($baseline | Where-Object { $_.InternalName -ceq $oldName })[0]
    $after = @($index | Where-Object { $_.InternalName -ceq $oldName })[0]
    if ($before.AssemblyVersion -cne $oldVersions[$oldName]) { throw "Unexpected baseline version: $oldName" }
    Assert-SameJson $after $before "Complete existing entry $oldName"
}
$sourceManifest = Read-JsonFile (Join-Path $root "plugins/$name/$name.json")
$newEntry = @($index | Where-Object { $_.InternalName -ceq $name })[0]
Assert-Manifest $sourceManifest 'Source manifest'
Assert-Manifest $newEntry 'Candidate entry'
$distributionKeys = @('DownloadLinkInstall', 'DownloadLinkUpdate', 'DownloadLinkTesting', 'LastUpdate', 'IsHide', 'IsTestingExclusive')
Assert-ExactNames @($newEntry.Keys) (@($sourceManifest.Keys) + $distributionKeys) 'Candidate entry fields'
foreach ($key in $sourceManifest.Keys) { Assert-SameJson $newEntry[$key] $sourceManifest[$key] "Candidate/source manifest field $key" }
foreach ($key in @('DownloadLinkInstall', 'DownloadLinkUpdate', 'DownloadLinkTesting')) {
    if ($newEntry[$key] -cne "$repository/releases/download/$tag/$name.zip") { throw "Unexpected download URL: $key" }
}
if ($newEntry.IsHide -isnot [bool] -or $newEntry.IsHide -or
    $newEntry.IsTestingExclusive -isnot [bool] -or $newEntry.IsTestingExclusive -or
    $newEntry.LastUpdate -isnot [long] -or $newEntry.LastUpdate -lt 1) { throw 'Invalid distribution flags or timestamp.' }

$zipPath = Join-Path $package "$name.zip"
$digest = (Get-FileHash -LiteralPath $zipPath -Algorithm SHA256).Hash.ToLowerInvariant()
$hashPath = Join-Path $package 'SHA256SUMS.txt'
$hashText = [IO.File]::ReadAllText($hashPath)
$hashMatch = [regex]::Match($hashText, '\A([0-9a-fA-F]{64})  RetainerWorkshop\.zip(?:\r?\n)?\z')
if (!$hashMatch.Success -or $hashMatch.Groups[1].Value.ToLowerInvariant() -cne $digest) { throw 'SHA256SUMS is not the exact matching single-ZIP record.' }
foreach ($path in @($indexPath, $hashPath)) { Assert-NoPrivatePath ([IO.File]::ReadAllBytes($path)) ([IO.Path]::GetFileName($path)) }

Add-Type -AssemblyName System.IO.Compression
Add-Type -AssemblyName System.Reflection.Metadata
$zip = [IO.Compression.ZipFile]::OpenRead($zipPath)
try {
    $sourceFiles = [ordered]@{
        "$name.json" = (Join-Path $root "plugins/$name/$name.json")
        'README.md' = (Join-Path $root "plugins/$name/README.md")
        'LICENSE' = (Join-Path $root 'LICENSE')
        'THIRD-PARTY-NOTICES.md' = (Join-Path $root 'THIRD-PARTY-NOTICES.md')
    }
    # Fixed seven-member package: adding a host DLL, config, log or extra file must fail.
    foreach ($license in @('AutoRetainerAPI.LICENSE', 'QuickTransfer.LICENSE')) {
        $sourceFiles["licenses/$license"] = Join-Path $root "licenses/$license"
    }
    if ($sourceFiles.Count -ne 6) { throw 'Expected six non-assembly source files.' }
    Assert-ExactNames @($zip.Entries.FullName) (@("$name.dll") + @($sourceFiles.Keys)) 'ZIP allowlist'
    $totalSize = 0L
    foreach ($entry in $zip.Entries) {
        $totalSize += $entry.Length
        if ($totalSize -gt 134217728 -or (($entry.ExternalAttributes -shr 16) -band 0xF000) -eq 0xA000) { throw 'ZIP is oversized or contains a symbolic link.' }
        $bytes = Read-ZipBytes $entry
        Assert-NoPrivatePath $bytes $entry.FullName
        if ($sourceFiles.Contains($entry.FullName)) {
            $expectedDigest = (Get-FileHash -LiteralPath $sourceFiles[$entry.FullName] -Algorithm SHA256).Hash
            $actualDigest = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($bytes))
            if ($actualDigest -cne $expectedDigest) { throw "ZIP file differs from the current source: $($entry.FullName)" }
        }
    }
    $zipManifest = Read-JsonText ([Text.Encoding]::UTF8.GetString((Read-ZipBytes $zip.GetEntry("$name.json"))))
    Assert-Manifest $zipManifest 'ZIP manifest'
    Assert-SameJson $zipManifest $sourceManifest 'ZIP/source manifest'
    $dllBytes = Read-ZipBytes $zip.GetEntry("$name.dll")
    $stream = [IO.MemoryStream]::new($dllBytes, $false)
    $pe = [System.Reflection.PortableExecutable.PEReader]::new($stream)
    try {
        if (!$pe.HasMetadata -or $pe.PEHeaders.CoffHeader.Machine.ToString() -cne 'Amd64' -or
            $pe.PEHeaders.PEHeader.Magic.ToString() -cne 'PE32Plus' -or
            ([int]$pe.PEHeaders.CoffHeader.Characteristics -band 0x2000) -eq 0) { throw 'Expected a managed x64 DLL.' }
        $metadata = [System.Reflection.Metadata.PEReaderExtensions]::GetMetadataReader($pe)
        if (!$metadata.IsAssembly) { throw 'Missing assembly metadata.' }
        $assembly = $metadata.GetAssemblyDefinition()
        if ($metadata.GetString($assembly.Name) -cne $name -or $assembly.Version.ToString() -cne $version) { throw 'Assembly identity/version mismatch.' }
        foreach ($debug in $pe.ReadDebugDirectory()) {
            if ($debug.Type.ToString() -cne 'Reproducible') { throw 'PDB or unexpected debug directory found.' }
        }
        if ([Text.Encoding]::ASCII.GetString($dllBytes).Contains('RSDS')) { throw 'CodeView/PDB marker found.' }
    }
    finally { $pe.Dispose(); $stream.Dispose() }
    $entryNames = @($zip.Entries.FullName)
}
finally { $zip.Dispose() }

[pscustomobject]@{
    Verified = $true
    InternalName = $name
    Version = $version
    ApiLevel = 15
    GameVersion = $game
    SHA256 = $digest
    ZipEntries = $entryNames
    IndexEntries = 4
    ExistingEntriesUnchanged = @($oldVersions.Keys)
    BaselineIndex = $baselinePath
    BaselineSHA256 = (Get-FileHash -LiteralPath $baselinePath -Algorithm SHA256).Hash.ToLowerInvariant()
    Comparison = 'Recursive object-key normalization; array order and complete fields preserved'
    PrivacyMarkerCheck = 'UTF-8 and both UTF-16 alignments'
    AssembliesLoaded = $false
    FilesWritten = $false
} | ConvertTo-Json -Depth 5
