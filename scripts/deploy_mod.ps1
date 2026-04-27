param(
    [string]$ProjectPath = "src/SpireMindMod/SpireMindMod.csproj",
    [string]$Configuration = "Release",
    [string]$ModsDir = "",
    [string]$PckPath = ""
)

$ErrorActionPreference = "Stop"

$repoRoot = Resolve-Path (Join-Path $PSScriptRoot "..")
$resolvedProjectPath = Resolve-Path (Join-Path $repoRoot $ProjectPath)
$projectDir = Split-Path $resolvedProjectPath -Parent

if ([string]::IsNullOrWhiteSpace($ModsDir)) {
    $localPropsPath = Join-Path $projectDir "SpireMind.Local.props"
    if (Test-Path $localPropsPath) {
        [xml]$localProps = Get-Content -Raw $localPropsPath
        $ModsDir = $localProps.Project.PropertyGroup.Sts2ModsDir
    }
}

if ([string]::IsNullOrWhiteSpace($ModsDir)) {
    throw "ModsDir is required. Pass -ModsDir or set Sts2ModsDir in SpireMind.Local.props."
}

dotnet build $resolvedProjectPath -c $Configuration
if ($LASTEXITCODE -ne 0) {
    throw "dotnet build failed with exit code $LASTEXITCODE. Deploy was not attempted."
}

[xml]$projectXml = Get-Content -Raw $resolvedProjectPath
$targetFramework = $projectXml.Project.PropertyGroup.TargetFramework
if ([string]::IsNullOrWhiteSpace($targetFramework)) {
    throw "TargetFramework was not found in the project file."
}

$outputDir = Join-Path $projectDir "bin\$Configuration\$targetFramework"
$deployDir = Join-Path $ModsDir "SpireMind"

New-Item -ItemType Directory -Force -Path $deployDir | Out-Null

Copy-Item -Force (Join-Path $outputDir "SpireMind.dll") $deployDir
Copy-Item -Force (Join-Path $outputDir "SpireMind.json") $deployDir

if (-not [string]::IsNullOrWhiteSpace($PckPath)) {
    $resolvedPckPath = Resolve-Path $PckPath
    Copy-Item -Force $resolvedPckPath (Join-Path $deployDir "SpireMind.pck")
}

Write-Host "[SpireMind] Deploy finished: $deployDir"
if ([string]::IsNullOrWhiteSpace($PckPath)) {
    Write-Host "[SpireMind] SpireMind.pck was not copied. Pass -PckPath if the loader requires a Godot pack."
}
