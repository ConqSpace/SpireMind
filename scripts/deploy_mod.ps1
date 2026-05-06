param(
    [string]$ProjectPath = "src/SpireMindMod/SpireMindMod.csproj",
    [string]$Configuration = "Release",
    [string]$SetupConfigPath = "",
    [string]$ModsDir = "",
    [switch]$AlsoDeployAppData,
    [string]$PckPath = ""
)

$ErrorActionPreference = "Stop"

$repoRoot = Resolve-Path (Join-Path $PSScriptRoot "..")
$resolvedProjectPath = Resolve-Path (Join-Path $repoRoot $ProjectPath)
$projectDir = Split-Path $resolvedProjectPath -Parent

function Resolve-SetupConfigPath {
    param(
        [string]$RepoRootPath,
        [string]$ExplicitPath
    )

    if (-not [string]::IsNullOrWhiteSpace($ExplicitPath)) {
        if ([System.IO.Path]::IsPathRooted($ExplicitPath)) {
            return $ExplicitPath
        }

        return (Join-Path $RepoRootPath $ExplicitPath)
    }

    return (Join-Path $RepoRootPath "config\local_setup.local.json")
}

function Get-JsonValue {
    param(
        [object]$Object,
        [string[]]$Names
    )

    if ($null -eq $Object) {
        return $null
    }

    foreach ($name in $Names) {
        $property = $Object.PSObject.Properties[$name]
        if ($null -ne $property) {
            return $property.Value
        }
    }

    return $null
}

if ([string]::IsNullOrWhiteSpace($ModsDir)) {
    $resolvedSetupConfigPath = Resolve-SetupConfigPath -RepoRootPath $repoRoot.Path -ExplicitPath $SetupConfigPath
    if (Test-Path $resolvedSetupConfigPath) {
        try {
            $setupConfig = Get-Content -Raw $resolvedSetupConfigPath | ConvertFrom-Json
            $sts2Config = Get-JsonValue $setupConfig @("sts2")
            $ModsDir = [string](Get-JsonValue $sts2Config @("mods_dir", "modsDir"))
        }
        catch {
            throw "Local setup config is not valid JSON: $resolvedSetupConfigPath"
        }
    }
}

if ([string]::IsNullOrWhiteSpace($ModsDir)) {
    $localPropsPath = Join-Path $projectDir "SpireMind.Local.props"
    if (Test-Path $localPropsPath) {
        [xml]$localProps = Get-Content -Raw $localPropsPath
        $ModsDir = $localProps.Project.PropertyGroup.Sts2ModsDir
    }
}

if ([string]::IsNullOrWhiteSpace($ModsDir)) {
    throw "ModsDir is required. Run node .\scripts\spiremind_setup.js, pass -ModsDir, or set Sts2ModsDir in SpireMind.Local.props."
}

dotnet build $resolvedProjectPath -c $Configuration
if ($LASTEXITCODE -ne 0) {
    throw "dotnet build failed with exit code $LASTEXITCODE. Deploy was not attempted."
}

[xml]$projectXml = Get-Content -Raw $resolvedProjectPath
$targetFramework = @(
    $projectXml.Project.PropertyGroup |
        ForEach-Object { $_.TargetFramework } |
        Where-Object { -not [string]::IsNullOrWhiteSpace($_) } |
        Select-Object -First 1
)
$targetFramework = ([string]$targetFramework).Trim()
if ([string]::IsNullOrWhiteSpace($targetFramework)) {
    throw "TargetFramework was not found in the project file."
}

$outputDir = Join-Path $projectDir "bin\$Configuration\$targetFramework"
$deployDir = Join-Path $ModsDir "SpireMind"

New-Item -ItemType Directory -Force -Path $deployDir | Out-Null

Copy-Item -Force (Join-Path $outputDir "SpireMind.dll") $deployDir
Copy-Item -Force (Join-Path $outputDir "SpireMind.json") $deployDir

if ($AlsoDeployAppData) {
    $appDataDeployDir = Join-Path $env:APPDATA "SlayTheSpire2\SpireMind"
    New-Item -ItemType Directory -Force -Path $appDataDeployDir | Out-Null
    Copy-Item -Force (Join-Path $outputDir "SpireMind.dll") $appDataDeployDir
    Copy-Item -Force (Join-Path $outputDir "SpireMind.json") $appDataDeployDir
    $depsPath = Join-Path $outputDir "SpireMind.deps.json"
    if (Test-Path $depsPath) {
        Copy-Item -Force $depsPath $appDataDeployDir
    }
    Write-Host "[SpireMind] AppData deploy finished: $appDataDeployDir"
}

if (-not [string]::IsNullOrWhiteSpace($PckPath)) {
    $resolvedPckPath = Resolve-Path $PckPath
    Copy-Item -Force $resolvedPckPath (Join-Path $deployDir "SpireMind.pck")
}

Write-Host "[SpireMind] Deploy finished: $deployDir"
if ([string]::IsNullOrWhiteSpace($PckPath)) {
    Write-Host "[SpireMind] SpireMind.pck was not copied. Pass -PckPath if the loader requires a Godot pack."
}
