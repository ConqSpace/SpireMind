param(
    [string]$ProjectRoot = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path,
    [string]$BridgeUrl = "http://127.0.0.1:17833",
    [string]$CombatStatePath = (Join-Path $env:APPDATA "SlayTheSpire2\SpireMind\combat_state.json")
)

$ErrorActionPreference = "Stop"
[Console]::OutputEncoding = [System.Text.UTF8Encoding]::new()
$OutputEncoding = [System.Text.UTF8Encoding]::new()

function Test-BridgeHealth {
    param([string]$Url)

    try {
        return Invoke-RestMethod -Method Get -Uri "$Url/health" -TimeoutSec 2
    } catch {
        return $null
    }
}

function Wait-BridgeHealth {
    param([string]$Url)

    for ($index = 0; $index -lt 40; $index += 1) {
        $health = Test-BridgeHealth -Url $Url
        if ($null -ne $health -and $health.ok -eq $true) {
            return
        }

        Start-Sleep -Milliseconds 250
    }

    throw "브리지 상태 확인에 실패했습니다: $Url"
}

if (-not (Test-Path $CombatStatePath)) {
    throw "전투 상태 파일을 찾지 못했습니다: $CombatStatePath"
}

$bridgeUri = [Uri]$BridgeUrl
$bridgePath = Join-Path $ProjectRoot "bridge\spiremind_bridge.js"
$decisionLoopPath = Join-Path $ProjectRoot "bridge\spiremind_decision_loop.js"

if (-not (Test-Path $bridgePath)) {
    throw "브리지 파일을 찾지 못했습니다: $bridgePath"
}

if (-not (Test-Path $decisionLoopPath)) {
    throw "의사결정 루프 파일을 찾지 못했습니다: $decisionLoopPath"
}

$startedBridge = $null
if ($null -eq (Test-BridgeHealth -Url $BridgeUrl)) {
    $startedBridge = Start-Process `
        -FilePath "node" `
        -ArgumentList @($bridgePath, "--http-host", $bridgeUri.Host, "--http-port", [string]$bridgeUri.Port) `
        -WorkingDirectory $ProjectRoot `
        -WindowStyle Hidden `
        -PassThru
    Wait-BridgeHealth -Url $BridgeUrl
}

try {
    $combatStateJson = Get-Content -Path $CombatStatePath -Raw -Encoding UTF8
    $null = Invoke-RestMethod `
        -Method Post `
        -Uri "$BridgeUrl/state" `
        -ContentType "application/json; charset=utf-8" `
        -Body $combatStateJson `
        -TimeoutSec 5

    $dryRunText = & node $decisionLoopPath --bridge-url $BridgeUrl --mode heuristic --once --dry-run --max-actions-per-turn 2
    if ($LASTEXITCODE -ne 0) {
        throw "의사결정 루프 dry-run이 실패했습니다."
    }

    $dryRun = $dryRunText | ConvertFrom-Json
    if ($dryRun.status -ne "dry_run") {
        throw "dry-run 결과 상태가 올바르지 않습니다: $($dryRun.status)"
    }

    $submitText = & node $decisionLoopPath --bridge-url $BridgeUrl --mode heuristic --once --max-actions-per-turn 2
    if ($LASTEXITCODE -ne 0) {
        throw "의사결정 루프 제출이 실패했습니다."
    }

    $submit = $submitText | ConvertFrom-Json
    if ($submit.status -ne "submitted") {
        throw "제출 결과 상태가 올바르지 않습니다: $($submit.status)"
    }

    $latest = Invoke-RestMethod -Method Get -Uri "$BridgeUrl/action/latest" -TimeoutSec 5
    if ($null -eq $latest.latest_action) {
        throw "브리지에 latest_action이 기록되지 않았습니다."
    }

    $stateForRetry = $combatStateJson | ConvertFrom-Json
    $stateForRetry.state_id = "combat_stale_retry_test_" + [guid]::NewGuid().ToString("N")
    $stateForRetryJson = $stateForRetry | ConvertTo-Json -Depth 100
    $null = Invoke-RestMethod `
        -Method Post `
        -Uri "$BridgeUrl/state" `
        -ContentType "application/json; charset=utf-8" `
        -Body $stateForRetryJson `
        -TimeoutSec 5

    $currentAfterStateChange = Invoke-RestMethod -Method Get -Uri "$BridgeUrl/state/current" -TimeoutSec 5
    $staleClaimBody = [ordered]@{
        executor_id = "decision-loop-smoke"
        observed_state_id = $currentAfterStateChange.state.state_id
        observed_state_version = $currentAfterStateChange.state_version
        supported_action_types = @("play_card", "end_turn")
    } | ConvertTo-Json -Depth 8
    $staleClaim = Invoke-RestMethod `
        -Method Post `
        -Uri "$BridgeUrl/action/claim" `
        -ContentType "application/json; charset=utf-8" `
        -Body $staleClaimBody `
        -TimeoutSec 5

    if ($staleClaim.status -ne "stale_retry_queued") {
        throw "stale claim 재시도가 큐에 들어가지 않았습니다: $($staleClaim.status)"
    }

    $retryLatest = Invoke-RestMethod -Method Get -Uri "$BridgeUrl/action/latest" -TimeoutSec 5
    if ($retryLatest.latest_action.execution_status -ne "pending") {
        throw "재시도 행동이 pending 상태가 아닙니다: $($retryLatest.latest_action.execution_status)"
    }

    [pscustomobject]@{
        status = "PASS"
        bridge_url = $BridgeUrl
        dry_run_decision = $dryRun.decision
        submitted_action = $latest.latest_action.selected_action_id
        stale_retry_status = $staleClaim.status
        retry_action = $retryLatest.latest_action.selected_action_id
        submitted_plan_status = $retryLatest.action_plan.status
        submitted_plan_count = @($latest.action_plan.actions).Count
    } | ConvertTo-Json -Depth 12
} finally {
    if ($null -ne $startedBridge -and -not $startedBridge.HasExited) {
        Stop-Process -Id $startedBridge.Id -Force
    }
}
