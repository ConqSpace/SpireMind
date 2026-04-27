<#
.SYNOPSIS
Runs a semi-manual R2 runtime smoke check for SpireMind.

.DESCRIPTION
The script can optionally build, deploy, and launch STS2. It then watches
godot.log and combat_state.json while the user manually enters combat, plays
one card, and ends the turn. It never clicks, moves the mouse, or sends keys.

.EXAMPLE
.\scripts\runtime_smoke_check.ps1 -Build -Deploy -LaunchGame -ModsDir "I:\SteamLibrary\steamapps\common\Slay the Spire 2\mods"

.EXAMPLE
.\scripts\runtime_smoke_check.ps1 -LaunchGame -LaunchMode Exe -Sts2ExePath "I:\SteamLibrary\steamapps\common\Slay the Spire 2\SlayTheSpire2.exe"

.EXAMPLE
.\scripts\runtime_smoke_check.ps1 -CheckSeconds 90 -PollSeconds 5

.EXAMPLE
.\scripts\runtime_smoke_check.ps1 -Build -Deploy -LaunchGame -WhatIf
#>
[CmdletBinding(SupportsShouldProcess = $true)]
param(
    [switch]$Help,
    [switch]$Build,
    [switch]$Deploy,
    [switch]$LaunchGame,
    [ValidateSet("Steam", "Exe")]
    [string]$LaunchMode = "Steam",
    [string]$Sts2Path = "I:\SteamLibrary\steamapps\common\Slay the Spire 2",
    [string]$Sts2ExePath = "",
    [string]$ModsDir = "",
    [string]$PckPath = "",
    [string]$Configuration = "Release",
    [int]$CheckSeconds = 120,
    [int]$PollSeconds = 5,
    [int]$RecentSeconds = 30
)

$ErrorActionPreference = "Stop"

function Show-SmokeHelp {
    Write-Host "SpireMind R2 runtime semi-manual smoke check"
    Write-Host ""
    Write-Host "Options:"
    Write-Host "  -Build       Run scripts/build_mod.ps1"
    Write-Host "  -Deploy      Run scripts/deploy_mod.ps1"
    Write-Host "  -LaunchGame  Start STS2"
    Write-Host "  -LaunchMode  Steam or Exe. Default: Steam"
    Write-Host "               Steam uses steam://rungameid/2868840"
    Write-Host "               Exe starts the executable from -Sts2ExePath or -Sts2Path"
    Write-Host "  -WhatIf      Show build/deploy/launch actions without running them"
    Write-Host ""
    Write-Host "Examples:"
    Write-Host '  .\scripts\runtime_smoke_check.ps1 -Build -Deploy -LaunchGame -ModsDir "I:\SteamLibrary\steamapps\common\Slay the Spire 2\mods"'
    Write-Host '  .\scripts\runtime_smoke_check.ps1 -LaunchGame -LaunchMode Exe -Sts2ExePath "I:\SteamLibrary\steamapps\common\Slay the Spire 2\SlayTheSpire2.exe"'
    Write-Host '  .\scripts\runtime_smoke_check.ps1 -CheckSeconds 90 -PollSeconds 5'
}

function Resolve-RepoRoot {
    return (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
}

function Resolve-Sts2Exe {
    param(
        [string]$InstallPath,
        [string]$ExplicitExePath
    )

    if (-not [string]::IsNullOrWhiteSpace($ExplicitExePath)) {
        return (Resolve-Path $ExplicitExePath).Path
    }

    $candidateNames = @(
        "SlayTheSpire2.exe",
        "Slay the Spire 2.exe",
        "STS2.exe"
    )

    foreach ($candidateName in $candidateNames) {
        $candidatePath = Join-Path $InstallPath $candidateName
        if (Test-Path $candidatePath) {
            return (Resolve-Path $candidatePath).Path
        }
    }

    throw "STS2 executable was not found. Pass -Sts2ExePath with the exact path."
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
        if ($Object -is [System.Collections.IDictionary] -and $Object.Contains($name)) {
            return $Object[$name]
        }

        $property = $Object.PSObject.Properties[$name]
        if ($null -ne $property) {
            return $property.Value
        }
    }

    return $null
}

function Get-CollectionCount {
    param([object]$Value)

    if ($null -eq $Value) {
        return 0
    }

    if ($Value -is [string]) {
        return 1
    }

    if ($Value -is [System.Collections.ICollection]) {
        return $Value.Count
    }

    return 1
}

function New-SmokeResult {
    param(
        [string]$Name,
        [string]$Status,
        [string]$Detail
    )

    return [pscustomobject]@{
        Name = $Name
        Status = $Status
        Detail = $Detail
    }
}

function Get-CombatStateSnapshot {
    param(
        [string]$CombatStatePath,
        [string]$GodotLogPath,
        [int]$FreshSeconds
    )

    $results = New-Object System.Collections.Generic.List[object]
    $json = $null
    $jsonReadError = $null
    $combatStateExists = Test-Path $CombatStatePath

    if ($combatStateExists) {
        $results.Add((New-SmokeResult "combat_state.json exists" "PASS" $CombatStatePath))

        $combatStateItem = Get-Item $CombatStatePath
        $ageSeconds = [int]((Get-Date) - $combatStateItem.LastWriteTime).TotalSeconds
        if ($ageSeconds -le $FreshSeconds) {
            $results.Add((New-SmokeResult "combat_state.json fresh" "PASS" "updated $ageSeconds seconds ago"))
        }
        else {
            $results.Add((New-SmokeResult "combat_state.json fresh" "WARN" "updated $ageSeconds seconds ago; expected <= $FreshSeconds"))
        }

        try {
            $json = Get-Content -Raw -Encoding UTF8 $CombatStatePath | ConvertFrom-Json
        }
        catch {
            $jsonReadError = $_.Exception.Message
        }
    }
    else {
        $results.Add((New-SmokeResult "combat_state.json exists" "FAIL" "file is missing: $CombatStatePath"))
        $results.Add((New-SmokeResult "combat_state.json fresh" "WARN" "file is missing; cannot check update time"))
    }

    if ($null -eq $json) {
        $detail = "JSON was not readable yet."
        if (-not [string]::IsNullOrWhiteSpace($jsonReadError)) {
            $detail = $jsonReadError
        }

        $results.Add((New-SmokeResult "card pile total" "FAIL" $detail))
        $results.Add((New-SmokeResult "player hp or energy" "FAIL" $detail))
        $results.Add((New-SmokeResult "enemy count" "WARN" $detail))
        $results.Add((New-SmokeResult "relic count" "WARN" $detail))
    }
    else {
        $piles = Get-JsonValue $json @("piles")
        $handCount = Get-CollectionCount (Get-JsonValue $piles @("hand"))
        $drawCount = Get-CollectionCount (Get-JsonValue $piles @("draw_pile", "draw"))
        $discardCount = Get-CollectionCount (Get-JsonValue $piles @("discard_pile", "discard"))
        $exhaustCount = Get-CollectionCount (Get-JsonValue $piles @("exhaust_pile", "exhaust"))
        $totalCards = $handCount + $drawCount + $discardCount + $exhaustCount

        if ($totalCards -gt 0) {
            $results.Add((New-SmokeResult "card pile total" "PASS" "hand=$handCount, draw=$drawCount, discard=$discardCount, exhaust=$exhaustCount"))
        }
        else {
            $results.Add((New-SmokeResult "card pile total" "FAIL" "hand/draw/discard/exhaust total is 0"))
        }

        $player = Get-JsonValue $json @("player")
        $playerHp = Get-JsonValue $player @("hp")
        $playerEnergy = Get-JsonValue $player @("energy")
        if ($null -ne $playerHp -or $null -ne $playerEnergy) {
            $results.Add((New-SmokeResult "player hp or energy" "PASS" "hp=$playerHp, energy=$playerEnergy"))
        }
        else {
            $results.Add((New-SmokeResult "player hp or energy" "WARN" "both hp and energy are null"))
        }

        $enemyCount = Get-CollectionCount (Get-JsonValue $json @("enemies"))
        if ($enemyCount -gt 0) {
            $results.Add((New-SmokeResult "enemy count" "PASS" "$enemyCount enemies"))
        }
        else {
            $results.Add((New-SmokeResult "enemy count" "WARN" "enemy list is empty"))
        }

        $relicCount = Get-CollectionCount (Get-JsonValue $json @("relics"))
        $results.Add((New-SmokeResult "relic count" "PASS" "$relicCount relics"))
    }

    if (Test-Path $GodotLogPath) {
        $logText = (Get-Content -Encoding UTF8 -Tail 1200 $GodotLogPath) -join "`n"
        $harmonyObservationPhrase = "Harmony " + (-join ([char[]](0xC804, 0xD22C))) + " " + (-join ([char[]](0xC0C1, 0xD0DC))) + " " + (-join ([char[]](0xAD00, 0xCC30))) + " " + (-join ([char[]](0xC9C0, 0xC810)))
        $combatOutputPathPhrase = "combat_state.v1 " + (-join ([char[]](0xCD9C, 0xB825))) + " " + (-join ([char[]](0xACBD, 0xB85C)))
        $hasSpireMind = $logText -match "SpireMind"
        $hasObservationLog = $logText.Contains($harmonyObservationPhrase) -or $logText.Contains($combatOutputPathPhrase)
        if ($hasSpireMind -and $hasObservationLog) {
            $results.Add((New-SmokeResult "godot.log SpireMind R2 log" "PASS" "SpireMind and R2 observation log were found"))
        }
        else {
            $results.Add((New-SmokeResult "godot.log SpireMind R2 log" "WARN" "SpireMind=$hasSpireMind, R2Observation=$hasObservationLog"))
        }
    }
    else {
        $results.Add((New-SmokeResult "godot.log SpireMind R2 log" "WARN" "log file is missing: $GodotLogPath"))
    }

    return $results
}

function Write-SmokeSummary {
    param([object[]]$Results)

    $failCount = @($Results | Where-Object { $_.Status -eq "FAIL" }).Count
    $warnCount = @($Results | Where-Object { $_.Status -eq "WARN" }).Count

    if ($failCount -gt 0) {
        $overallStatus = "FAIL"
    }
    elseif ($warnCount -gt 0) {
        $overallStatus = "WARN"
    }
    else {
        $overallStatus = "PASS"
    }

    Write-Host ""
    Write-Host "==== SpireMind R2 runtime smoke summary: $overallStatus ===="
    foreach ($result in $Results) {
        Write-Host ("[{0}] {1}: {2}" -f $result.Status, $result.Name, $result.Detail)
    }

    return $overallStatus
}

if ($Help) {
    Show-SmokeHelp
    return
}

if ($PollSeconds -lt 1) {
    throw "-PollSeconds must be 1 or greater."
}

if ($CheckSeconds -lt 1) {
    throw "-CheckSeconds must be 1 or greater."
}

$repoRoot = Resolve-RepoRoot
$buildScriptPath = Join-Path $repoRoot "scripts\build_mod.ps1"
$deployScriptPath = Join-Path $repoRoot "scripts\deploy_mod.ps1"
$godotLogPath = Join-Path $env:APPDATA "SlayTheSpire2\logs\godot.log"
$combatStatePath = Join-Path $env:APPDATA "SlayTheSpire2\SpireMind\combat_state.json"
$steamLaunchUri = "steam://rungameid/2868840"

Write-Host "[SpireMind] Starting R2 runtime semi-manual smoke check."
Write-Host "[SpireMind] Default STS2 path candidate: $Sts2Path"
Write-Host "[SpireMind] Launch mode: $LaunchMode"
Write-Host "[SpireMind] Watching godot.log: $godotLogPath"
Write-Host "[SpireMind] Watching combat_state.json: $combatStatePath"
Write-Host ""

if ($Build) {
    if ($PSCmdlet.ShouldProcess($buildScriptPath, "Run mod build")) {
        & $buildScriptPath -Configuration $Configuration
        if ($LASTEXITCODE -ne 0) {
            throw "Build script failed with exit code $LASTEXITCODE."
        }
    }
}

if ($Deploy) {
    $deployArgs = @{
        Configuration = $Configuration
    }

    if (-not [string]::IsNullOrWhiteSpace($ModsDir)) {
        $deployArgs["ModsDir"] = $ModsDir
    }

    if (-not [string]::IsNullOrWhiteSpace($PckPath)) {
        $deployArgs["PckPath"] = $PckPath
    }

    if ($PSCmdlet.ShouldProcess($deployScriptPath, "Run mod deploy")) {
        & $deployScriptPath @deployArgs
        if ($LASTEXITCODE -ne 0) {
            throw "Deploy script failed with exit code $LASTEXITCODE."
        }
    }
}

if ($LaunchGame) {
    if ($LaunchMode -eq "Steam") {
        if ($PSCmdlet.ShouldProcess($steamLaunchUri, "Launch STS2 through Steam URL")) {
            Start-Process -FilePath $steamLaunchUri
        }
    }
    else {
        $resolvedExePath = Resolve-Sts2Exe -InstallPath $Sts2Path -ExplicitExePath $Sts2ExePath
        if ($PSCmdlet.ShouldProcess($resolvedExePath, "Launch STS2 executable directly")) {
            Start-Process -FilePath $resolvedExePath -WorkingDirectory (Split-Path $resolvedExePath -Parent)
        }
    }
}

if ($WhatIfPreference) {
    Write-Host ""
    Write-Host "[SpireMind] WhatIf mode finished. Monitoring was not started."
    return
}

Write-Host "Manual step: enter combat in the game, play one card, then end the turn."
Write-Host "This script does not click, move the mouse, or send keyboard input."
Write-Host ""

$deadline = (Get-Date).AddSeconds($CheckSeconds)
$lastResults = @()

while ((Get-Date) -lt $deadline) {
    $lastResults = @(Get-CombatStateSnapshot -CombatStatePath $combatStatePath -GodotLogPath $godotLogPath -FreshSeconds $RecentSeconds)
    $failCount = @($lastResults | Where-Object { $_.Status -eq "FAIL" }).Count
    $warnCount = @($lastResults | Where-Object { $_.Status -eq "WARN" }).Count
    $passCount = @($lastResults | Where-Object { $_.Status -eq "PASS" }).Count
    $remainingSeconds = [int]($deadline - (Get-Date)).TotalSeconds

    Write-Host ("[{0:HH:mm:ss}] PASS={1}, WARN={2}, FAIL={3}, remaining={4}s" -f (Get-Date), $passCount, $warnCount, $failCount, $remainingSeconds)

    Start-Sleep -Seconds $PollSeconds
}

$overallStatus = Write-SmokeSummary -Results $lastResults

if ($overallStatus -eq "FAIL") {
    exit 1
}

exit 0
