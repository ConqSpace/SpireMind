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

.EXAMPLE
.\scripts\runtime_smoke_check.ps1 -Build -Deploy -StartBridge -LaunchGame -StartSeededRun -Seed "7MJCUHEB5Q" -ModsDir "I:\SteamLibrary\steamapps\common\Slay the Spire 2\mods"

.EXAMPLE
.\scripts\runtime_smoke_check.ps1 -StartBridge -StartSeededRun -ForceAbandonRun -Seed "7MJCUHEB5Q"
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
    [switch]$StartBridge,
    [string]$BridgeHost = "127.0.0.1",
    [int]$BridgePort = 17832,
    [switch]$StartSeededRun,
    [switch]$ForceAbandonRun,
    [string]$Seed = "7MJCUHEB5Q",
    [string]$CharacterId = "Ironclad",
    [int]$LaunchWaitSeconds = 25,
    [int]$CommandWaitSeconds = 180,
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
    Write-Host "  -StartBridge Start the local SpireMind bridge if it is not already healthy"
    Write-Host "  -StartSeededRun"
    Write-Host "               Write a start_new_run command for a custom seeded run"
    Write-Host "  -ForceAbandonRun"
    Write-Host "               Let start_new_run abandon the in-progress run before starting the seed"
    Write-Host "  -Seed        Seed used by -StartSeededRun. Default: 7MJCUHEB5Q"
    Write-Host "  -CharacterId Character used by -StartSeededRun. Default: Ironclad"
    Write-Host "  -WhatIf      Show build/deploy/launch actions without running them"
    Write-Host ""
    Write-Host "Examples:"
    Write-Host '  .\scripts\runtime_smoke_check.ps1 -Build -Deploy -LaunchGame -ModsDir "I:\SteamLibrary\steamapps\common\Slay the Spire 2\mods"'
    Write-Host '  .\scripts\runtime_smoke_check.ps1 -LaunchGame -LaunchMode Exe -Sts2ExePath "I:\SteamLibrary\steamapps\common\Slay the Spire 2\SlayTheSpire2.exe"'
    Write-Host '  .\scripts\runtime_smoke_check.ps1 -Build -Deploy -StartBridge -LaunchGame -StartSeededRun -Seed "7MJCUHEB5Q" -ModsDir "I:\SteamLibrary\steamapps\common\Slay the Spire 2\mods"'
    Write-Host '  .\scripts\runtime_smoke_check.ps1 -StartBridge -StartSeededRun -ForceAbandonRun -Seed "7MJCUHEB5Q"'
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

function Get-SpireMindDataDir {
    return (Join-Path $env:APPDATA "SlayTheSpire2\SpireMind")
}

function Clear-AutotestCommandFiles {
    $dataDir = Get-SpireMindDataDir
    $commandPath = Join-Path $dataDir "autotest_command.json"
    $resultPath = Join-Path $dataDir "autotest_result.json"

    Remove-Item -LiteralPath $commandPath -Force -ErrorAction SilentlyContinue
    Remove-Item -LiteralPath $resultPath -Force -ErrorAction SilentlyContinue
    Write-Host "[SpireMind] Cleared stale autotest command files."
}

function Invoke-BridgeHealth {
    param([string]$BridgeUrl)

    try {
        return Invoke-RestMethod -Method Get -Uri "$BridgeUrl/health" -TimeoutSec 2
    }
    catch {
        return $null
    }
}

function Start-SpireMindBridge {
    param(
        [string]$RepoRoot,
        [string]$HostName,
        [int]$Port
    )

    $bridgeUrl = "http://${HostName}:$Port"
    $health = Invoke-BridgeHealth -BridgeUrl $bridgeUrl
    if ($null -ne $health -and $health.ok -eq $true) {
        Write-Host "[SpireMind] Bridge is already healthy: $bridgeUrl"
        return
    }

    $bridgeScriptPath = Join-Path $RepoRoot "bridge\spiremind_bridge.js"
    $bridgeArguments = @(
        $bridgeScriptPath,
        "--http-host",
        $HostName,
        "--http-port",
        [string]$Port
    )

    if ($PSCmdlet.ShouldProcess($bridgeUrl, "Start SpireMind bridge")) {
        Start-Process `
            -FilePath "node" `
            -ArgumentList $bridgeArguments `
            -WorkingDirectory $RepoRoot `
            -WindowStyle Hidden | Out-Null

        $deadline = (Get-Date).AddSeconds(10)
        while ((Get-Date) -lt $deadline) {
            Start-Sleep -Milliseconds 500
            $health = Invoke-BridgeHealth -BridgeUrl $bridgeUrl
            if ($null -ne $health -and $health.ok -eq $true) {
                Write-Host "[SpireMind] Bridge started: $bridgeUrl"
                return
            }
        }

        Write-Warning "[SpireMind] Bridge did not report healthy within 10 seconds: $bridgeUrl"
    }
}

function New-AutotestCommandId {
    param([string]$Prefix)

    return "{0}-{1:yyyyMMdd-HHmmss}-{2}" -f $Prefix, (Get-Date), ([guid]::NewGuid().ToString("N").Substring(0, 8))
}

function Write-StartSeededRunCommand {
    param(
        [string]$SeedValue,
        [string]$Character,
        [int]$TimeoutSeconds,
        [bool]$AbandonCurrentRun
    )

    $dataDir = Get-SpireMindDataDir
    New-Item -ItemType Directory -Force -Path $dataDir | Out-Null

    $commandPath = Join-Path $dataDir "autotest_command.json"
    $resultPath = Join-Path $dataDir "autotest_result.json"
    $commandId = New-AutotestCommandId -Prefix "cmd-start-seeded"
    $command = [ordered]@{
        id = $commandId
        action = "start_new_run"
        params = [ordered]@{
            seed = $SeedValue
            character_id = $Character
            timeout_ms = [Math]::Max(1, $TimeoutSeconds) * 1000
            ready_timeout_ms = [Math]::Max(1, $TimeoutSeconds) * 1000
            force_abandon = $AbandonCurrentRun
        }
    }

    if ($PSCmdlet.ShouldProcess($commandPath, "Write start_new_run command")) {
        $command | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $commandPath -Encoding UTF8
        Write-Host "[SpireMind] Wrote autotest command: $commandPath"
        Write-Host "[SpireMind] Command id: $commandId"
    }

    return [pscustomobject]@{
        Id = $commandId
        CommandPath = $commandPath
        ResultPath = $resultPath
    }
}

function Wait-AutotestCommandResult {
    param(
        [string]$CommandId,
        [string]$ResultPath,
        [int]$TimeoutSeconds,
        [int]$PollSeconds
    )

    $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
    while ((Get-Date) -lt $deadline) {
        if (Test-Path $ResultPath) {
            try {
                $result = Get-Content -Raw -Encoding UTF8 -LiteralPath $ResultPath | ConvertFrom-Json
                if ($result.id -eq $CommandId) {
                    Write-Host ("[SpireMind] Autotest result: {0} - {1}" -f $result.status, $result.message)
                    if ($result.status -in @("applied", "failed", "rejected")) {
                        return $result
                    }
                }
            }
            catch {
                Write-Warning "[SpireMind] Could not read autotest result yet: $($_.Exception.Message)"
            }
        }

        Start-Sleep -Seconds ([Math]::Max(1, $PollSeconds))
    }

    throw "Timed out waiting for autotest command result: $CommandId"
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
        [int]$FreshSeconds,
        [switch]$AllowNonCombatState
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
        $results.Add((New-SmokeResult "legal_actions count" "FAIL" $detail))
        $results.Add((New-SmokeResult "enemy count" "WARN" $detail))
        $results.Add((New-SmokeResult "relic count" "WARN" $detail))
    }
    else {
        $phase = Get-JsonValue $json @("phase")
        $piles = Get-JsonValue $json @("piles")
        $handCount = Get-CollectionCount (Get-JsonValue $piles @("hand"))
        $drawCount = Get-CollectionCount (Get-JsonValue $piles @("draw_pile", "draw"))
        $discardCount = Get-CollectionCount (Get-JsonValue $piles @("discard_pile", "discard"))
        $exhaustCount = Get-CollectionCount (Get-JsonValue $piles @("exhaust_pile", "exhaust"))
        $totalCards = $handCount + $drawCount + $discardCount + $exhaustCount

        if ($totalCards -gt 0) {
            $results.Add((New-SmokeResult "card pile total" "PASS" "hand=$handCount, draw=$drawCount, discard=$discardCount, exhaust=$exhaustCount"))
        }
        elseif ($AllowNonCombatState -and $phase -ne "combat") {
            $results.Add((New-SmokeResult "card pile total" "WARN" "phase=$phase; card piles can be empty before combat"))
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

        $legalActionCount = Get-CollectionCount (Get-JsonValue $json @("legal_actions"))
        if ($legalActionCount -gt 0) {
            $results.Add((New-SmokeResult "legal_actions count" "PASS" "$legalActionCount actions"))
        }
        else {
            $results.Add((New-SmokeResult "legal_actions count" "FAIL" "legal_actions is empty or missing"))
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
$bridgeUrl = "http://${BridgeHost}:$BridgePort"

Write-Host "[SpireMind] Starting R2 runtime semi-manual smoke check."
Write-Host "[SpireMind] Default STS2 path candidate: $Sts2Path"
Write-Host "[SpireMind] Launch mode: $LaunchMode"
Write-Host "[SpireMind] Bridge URL: $bridgeUrl"
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

if ($StartBridge) {
    Start-SpireMindBridge -RepoRoot $repoRoot -HostName $BridgeHost -Port $BridgePort
}

if ($StartSeededRun) {
    Clear-AutotestCommandFiles
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

if ($StartSeededRun) {
    if ($LaunchGame -and $LaunchWaitSeconds -gt 0) {
        Write-Host "[SpireMind] Waiting $LaunchWaitSeconds seconds for the game to reach the main menu."
        Start-Sleep -Seconds $LaunchWaitSeconds
    }

    $commandInfo = Write-StartSeededRunCommand `
        -SeedValue $Seed `
        -Character $CharacterId `
        -TimeoutSeconds $CommandWaitSeconds `
        -AbandonCurrentRun $ForceAbandonRun.IsPresent
    $commandResult = Wait-AutotestCommandResult `
        -CommandId $commandInfo.Id `
        -ResultPath $commandInfo.ResultPath `
        -TimeoutSeconds $CommandWaitSeconds `
        -PollSeconds $PollSeconds

    if ($commandResult.status -ne "applied") {
        throw "start_new_run command was not applied. status=$($commandResult.status), message=$($commandResult.message)"
    }

    Write-Host "[SpireMind] Seeded run command applied. Seed=$Seed, Character=$CharacterId"
}
else {
    Write-Host "Manual step: enter combat in the game, play one card, then end the turn."
    Write-Host "This script does not click, move the mouse, or send keyboard input."
}
Write-Host ""

$deadline = (Get-Date).AddSeconds($CheckSeconds)
$lastResults = @()

while ((Get-Date) -lt $deadline) {
    $lastResults = @(Get-CombatStateSnapshot `
        -CombatStatePath $combatStatePath `
        -GodotLogPath $godotLogPath `
        -FreshSeconds $RecentSeconds `
        -AllowNonCombatState:$StartSeededRun)
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
