param(
    [string]$Dotnet = 'dotnet',
    [string]$DalamudLibPath = (Join-Path $PSScriptRoot '../.sdk/dalamud-26-11-03-01')
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_GENERATE_ASPNET_CERTIFICATE = 'false'
$env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE = '1'
$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$DalamudLibPath = [IO.Path]::GetFullPath($DalamudLibPath)
Push-Location $root
try {
    foreach ($name in @('BatchTrade', 'RetainerCraftBridge', 'RetainerVendorSweep', 'RetainerWorkshop')) {
        & $Dotnet build "plugins/$name/$name.csproj" -c Release "-p:DalamudLibPath=$DalamudLibPath" --nologo
        if ($LASTEXITCODE -ne 0) { throw "Build failed: $name" }
        & $Dotnet run --project "tests/$name.Tests/$name.Tests.csproj" -c Release
        if ($LASTEXITCODE -ne 0) { throw "Tests failed: $name" }
    }
    foreach ($name in @('MaterialRestock', 'CraftSession', 'RetainerWorkshop.Ledger', 'RetainerWorkshop.Sessions', 'RetainerWorkshop.Config')) {
        & $Dotnet run --project "tests/$name.Tests/$name.Tests.csproj" -c Release
        if ($LASTEXITCODE -ne 0) { throw "Tests failed: $name" }
    }
}
finally { Pop-Location }
