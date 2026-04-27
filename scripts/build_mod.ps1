param(
    [string]$ProjectPath = "src/SpireMindMod/SpireMindMod.csproj",
    [string]$Configuration = "Release"
)

$ErrorActionPreference = "Stop"

$repoRoot = Resolve-Path (Join-Path $PSScriptRoot "..")
$resolvedProjectPath = Resolve-Path (Join-Path $repoRoot $ProjectPath)

Write-Host "[SpireMind] Build started: $resolvedProjectPath"
Write-Host "[SpireMind] R1 project builds as net8.0 for local verification."
dotnet build $resolvedProjectPath -c $Configuration
if ($LASTEXITCODE -ne 0) {
    throw "dotnet build failed with exit code $LASTEXITCODE."
}

Write-Host "[SpireMind] Build finished."
