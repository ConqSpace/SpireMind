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

function Wait-LatestAction {
    param(
        [string]$Url,
        [scriptblock]$Predicate,
        [int]$TimeoutSeconds = 20
    )

    $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
    do {
        Start-Sleep -Milliseconds 250
        $latest = Invoke-RestMethod -Method Get -Uri "$Url/action/latest" -TimeoutSec 5
        if (& $Predicate $latest) {
            return $latest
        }
    } while ((Get-Date) -lt $deadline)

    throw "latest_action 조건을 기다리다 시간 초과했습니다."
}

function Send-ActionClaim {
    param(
        [string]$Url,
        [object]$CurrentState
    )

    $claimBody = [ordered]@{
        executor_id = "decision-loop-smoke"
        observed_state_id = $CurrentState.state.state_id
        observed_state_version = $CurrentState.state_version
        supported_action_types = @("play_card", "end_turn")
    } | ConvertTo-Json -Depth 8

    return Invoke-RestMethod `
        -Method Post `
        -Uri "$Url/action/claim" `
        -ContentType "application/json; charset=utf-8" `
        -Body $claimBody `
        -TimeoutSec 5
}

function Send-ActionResult {
    param(
        [string]$Url,
        [object]$Claim,
        [object]$CurrentState
    )

    $action = $Claim.action
    $resultBody = [ordered]@{
        submission_id = $action.submission_id
        claim_token = $Claim.claim_token
        executor_id = "decision-loop-smoke"
        result = "applied"
        note = "smoke check applied"
        observed_state_id = $CurrentState.state.state_id
        observed_state_version = $CurrentState.state_version
    } | ConvertTo-Json -Depth 8

    return Invoke-RestMethod `
        -Method Post `
        -Uri "$Url/action/result" `
        -ContentType "application/json; charset=utf-8" `
        -Body $resultBody `
        -TimeoutSec 5
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

    $runLogDir = Join-Path ([System.IO.Path]::GetTempPath()) ("spiremind_run_record_" + [guid]::NewGuid().ToString("N"))
    New-Item -ItemType Directory -Force -Path $runLogDir | Out-Null
    $waitStdoutPath = Join-Path $runLogDir "wait_stdout.json"
    $waitStderrPath = Join-Path $runLogDir "wait_stderr.txt"
    $commandScript = "process.stdout.write(JSON.stringify({selected_action_id:'end_turn',reason:'smoke'}));"
    $waitProcess = Start-Process `
        -FilePath "node" `
        -ArgumentList @(
            $decisionLoopPath,
            "--bridge-url", $BridgeUrl,
            "--mode", "command",
            "--command", "node",
            "--command-arg", "-e",
            "--command-arg", $commandScript,
            "--once",
            "--wait-result",
            "--run-log-dir", $runLogDir,
            "--scenario-id", "smoke_scenario",
            "--play-session-id", "smoke_session",
            "--result-timeout-ms", "15000",
            "--poll-ms", "250"
        ) `
        -WorkingDirectory $ProjectRoot `
        -WindowStyle Hidden `
        -RedirectStandardOutput $waitStdoutPath `
        -RedirectStandardError $waitStderrPath `
        -PassThru

    $pendingLatest = Wait-LatestAction -Url $BridgeUrl -TimeoutSeconds 10 -Predicate {
        param($latest)
        return $null -ne $latest.latest_action `
            -and $latest.latest_action.selected_action_id -eq "end_turn" `
            -and $latest.latest_action.execution_status -eq "pending"
    }

    $currentForClaim = Invoke-RestMethod -Method Get -Uri "$BridgeUrl/state/current" -TimeoutSec 5
    $claim = Send-ActionClaim -Url $BridgeUrl -CurrentState $currentForClaim
    if ($claim.status -ne "claimed") {
        throw "wait-result 검증용 claim이 실패했습니다: $($claim.status)"
    }

    $null = Send-ActionResult -Url $BridgeUrl -Claim $claim -CurrentState $currentForClaim

    $stateAfterWaitResult = $combatStateJson | ConvertFrom-Json
    $stateAfterWaitResult.state_id = "combat_wait_result_done_" + [guid]::NewGuid().ToString("N")
    $stateAfterWaitResultJson = $stateAfterWaitResult | ConvertTo-Json -Depth 100
    $null = Invoke-RestMethod `
        -Method Post `
        -Uri "$BridgeUrl/state" `
        -ContentType "application/json; charset=utf-8" `
        -Body $stateAfterWaitResultJson `
        -TimeoutSec 5

    if (-not $waitProcess.WaitForExit(20000)) {
        Stop-Process -Id $waitProcess.Id -Force
        throw "wait-result 의사결정 루프가 종료되지 않았습니다."
    }

    $waitProcess.Refresh()
    $waitExitCode = if ($null -eq $waitProcess.ExitCode) { 0 } else { [int]$waitProcess.ExitCode }
    if ($waitExitCode -ne 0) {
        $stderr = if (Test-Path $waitStderrPath) { Get-Content -Raw -Encoding UTF8 $waitStderrPath } else { "" }
        throw "wait-result 의사결정 루프가 실패했습니다: $stderr"
    }

    $waitResult = Get-Content -Raw -Encoding UTF8 $waitStdoutPath | ConvertFrom-Json
    if ($waitResult.status -ne "applied") {
        throw "wait-result 결과 상태가 applied가 아닙니다: $($waitResult.status)"
    }

    $decisionsPath = Join-Path $runLogDir "decisions.jsonl"
    $metricsPath = Join-Path $runLogDir "metrics.json"
    $deciderConfigPath = Join-Path $runLogDir "decider_config.json"
    $scenarioConfigPath = Join-Path $runLogDir "scenario_config.json"
    $combatLogPath = Join-Path $runLogDir "combat_log.jsonl"
    if (-not (Test-Path $decisionsPath)) {
        throw "decisions.jsonl이 생성되지 않았습니다."
    }
    if (-not (Test-Path $metricsPath)) {
        throw "metrics.json이 생성되지 않았습니다."
    }
    if (-not (Test-Path $deciderConfigPath)) {
        throw "decider_config.json이 생성되지 않았습니다."
    }
    if (-not (Test-Path $scenarioConfigPath)) {
        throw "scenario_config.json이 생성되지 않았습니다."
    }
    if (-not (Test-Path $combatLogPath)) {
        throw "combat_log.jsonl이 생성되지 않았습니다."
    }

    $metrics = Get-Content -Raw -Encoding UTF8 $metricsPath | ConvertFrom-Json
    if ($metrics.submitted_decisions -lt 1 -or $metrics.actions_applied -lt 1) {
        throw "metrics.json에 제출과 실행 지표가 기록되지 않았습니다."
    }
    if ($metrics.observed_combats -lt 1 -or $metrics.combat_turn_decisions -lt 1 -or $metrics.end_turns_applied -lt 1) {
        throw "metrics.json에 전투 관찰 지표가 기록되지 않았습니다."
    }

    $scenarioConfig = Get-Content -Raw -Encoding UTF8 $scenarioConfigPath | ConvertFrom-Json
    if ($scenarioConfig.scenario_id -ne "smoke_scenario" -or $scenarioConfig.play_session_id -ne "smoke_session") {
        throw "scenario_config.json의 식별자가 올바르지 않습니다."
    }

    $combatLogText = Get-Content -Raw -Encoding UTF8 $combatLogPath
    if ($combatLogText -notmatch '"event_type":"combat_observed"' -or $combatLogText -notmatch '"event_type":"decision_submitted"' -or $combatLogText -notmatch '"event_type":"action_result_observed"') {
        throw "combat_log.jsonl에 필요한 최소 이벤트가 기록되지 않았습니다."
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
        wait_result_status = $waitResult.status
        run_log_dir = $runLogDir
        metrics = $metrics
        scenario_id = $scenarioConfig.scenario_id
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
