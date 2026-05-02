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
        supported_action_types = @(
            "play_card",
            "end_turn",
            "claim_gold_reward",
            "claim_relic_reward",
            "claim_potion_reward",
            "choose_card_reward",
            "skip_card_reward",
            "proceed_reward_screen",
            "choose_map_node",
            "choose_event_option",
            "choose_rest_site_option",
            "proceed_rest_site",
            "choose_card_selection",
            "confirm_card_selection"
        )
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
$codexDeciderPath = Join-Path $ProjectRoot "scripts\codex_decider.js"

if (-not (Test-Path $bridgePath)) {
    throw "브리지 파일을 찾지 못했습니다: $bridgePath"
}

if (-not (Test-Path $decisionLoopPath)) {
    throw "의사결정 루프 파일을 찾지 못했습니다: $decisionLoopPath"
}

if (-not (Test-Path $codexDeciderPath)) {
    throw "Codex 판단기 파일을 찾지 못했습니다: $codexDeciderPath"
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
    $stateForSmoke = $combatStateJson | ConvertFrom-Json
    $initialPhase = [string]$stateForSmoke.phase
    $smokeAction = @($stateForSmoke.legal_actions | Where-Object {
        $null -ne $_.action_id -and -not [string]::IsNullOrWhiteSpace([string]$_.action_id)
    } | Select-Object -First 1)
    if ($smokeAction.Count -lt 1) {
        throw "smoke check에 사용할 legal_actions가 없습니다."
    }
    $smokeActionId = [string]$smokeAction[0].action_id
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

    $previousFakeDecision = $env:SPIREMIND_CODEX_FAKE_DECISION
    try {
        $env:SPIREMIND_CODEX_FAKE_DECISION = (@{
            selected_action_id = $smokeActionId
            reason = "smoke codex adapter"
        } | ConvertTo-Json -Compress)
        $codexDryRunText = & node $decisionLoopPath --bridge-url $BridgeUrl --mode command --command node --command-arg $codexDeciderPath --once --dry-run
        if ($LASTEXITCODE -ne 0) {
            throw "Codex 판단기 dry-run이 실패했습니다."
        }

        $codexDryRun = $codexDryRunText | ConvertFrom-Json
        if ($codexDryRun.status -ne "dry_run" -or $codexDryRun.decision.selected_action_id -ne $smokeActionId) {
            throw "Codex 판단기 dry-run 결과가 올바르지 않습니다."
        }
    } finally {
        $env:SPIREMIND_CODEX_FAKE_DECISION = $previousFakeDecision
    }

    $runLogDir = Join-Path ([System.IO.Path]::GetTempPath()) ("spiremind_run_record_" + [guid]::NewGuid().ToString("N"))
    New-Item -ItemType Directory -Force -Path $runLogDir | Out-Null
    $waitStdoutPath = Join-Path $runLogDir "wait_stdout.json"
    $waitStderrPath = Join-Path $runLogDir "wait_stderr.txt"
    $commandScript = "process.stdout.write(JSON.stringify({selected_action_id:'$smokeActionId',reason:'smoke'}));"
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
            -and $latest.latest_action.selected_action_id -eq $smokeActionId `
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
    $memorySummaryPath = Join-Path $runLogDir "memory_summary.json"
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
    if (-not (Test-Path $memorySummaryPath)) {
        throw "memory_summary.json이 생성되지 않았습니다."
    }

    $decisionRecords = @(Get-Content -Encoding UTF8 $decisionsPath | ForEach-Object { $_ | ConvertFrom-Json })
    $waitDecisionRecord = $decisionRecords | Select-Object -First 1
    if ($null -eq $waitDecisionRecord.after_state_summary -or $null -eq $waitDecisionRecord.state_delta) {
        throw "decisions.jsonl에 판단 전후 상태 요약과 변화량이 기록되지 않았습니다."
    }
    if ($null -eq $waitDecisionRecord.state_delta.player -or $null -eq $waitDecisionRecord.state_delta.enemies) {
        throw "state_delta에 플레이어 변화와 적 변화가 기록되지 않았습니다."
    }
    $memorySummary = Get-Content -Raw -Encoding UTF8 $memorySummaryPath | ConvertFrom-Json
    if ($memorySummary.source.decisions_seen -lt 1 -or $memorySummary.combat.decisions_recorded -lt 1) {
        throw "memory_summary.json에 판단 기록 요약이 반영되지 않았습니다."
    }

    $metrics = Get-Content -Raw -Encoding UTF8 $metricsPath | ConvertFrom-Json
    if ($metrics.submitted_decisions -lt 1 -or $metrics.actions_applied -lt 1) {
        throw "metrics.json에 제출과 실행 지표가 기록되지 않았습니다."
    }
    if ($initialPhase -eq "combat_turn" -and ($metrics.observed_combats -lt 1 -or $metrics.combat_turn_decisions -lt 1)) {
        throw "metrics.json에 전투 관찰 지표가 기록되지 않았습니다."
    }
    if ($smokeActionId -eq "end_turn" -and $metrics.end_turns_applied -lt 1) {
        throw "metrics.json end_turn count was not recorded."
    }

    $scenarioConfig = Get-Content -Raw -Encoding UTF8 $scenarioConfigPath | ConvertFrom-Json
    if ($scenarioConfig.scenario_id -ne "smoke_scenario" -or $scenarioConfig.play_session_id -ne "smoke_session") {
        throw "scenario_config.json의 식별자가 올바르지 않습니다."
    }

    $combatLogText = Get-Content -Raw -Encoding UTF8 $combatLogPath
    if ($initialPhase -eq "combat_turn" -and $combatLogText -notmatch '"event_type":"combat_observed"') {
        throw "combat_log.jsonl combat_observed event was not recorded."
    }
    if ($combatLogText -notmatch '"event_type":"decision_submitted"' -or $combatLogText -notmatch '"event_type":"action_result_observed"') {
        throw "combat_log.jsonl에 필요한 최소 이벤트가 기록되지 않았습니다."
    }
    if ($combatLogText -notmatch '"state_delta"') {
        throw "combat_log.jsonl에 판단 전후 변화량이 기록되지 않았습니다."
    }

    $historyCommandScript = "let input='';process.stdin.setEncoding('utf8');process.stdin.on('data',(chunk)=>input+=chunk);process.stdin.on('end',()=>{const request=JSON.parse(input);if(!request.recent_history||!request.recent_history.memory_summary||!Array.isArray(request.recent_history.combat_events)||request.recent_history.combat_events.length<1){process.exit(2);}process.stdout.write(JSON.stringify({selected_action_id:'$smokeActionId',reason:'recent history observed'}));});"
    $historyDryRunText = & node $decisionLoopPath `
        --bridge-url $BridgeUrl `
        --mode command `
        --command node `
        --command-arg "-e" `
        --command-arg $historyCommandScript `
        --once `
        --dry-run `
        --run-log-dir $runLogDir
    if ($LASTEXITCODE -ne 0) {
        throw "최근 기록이 command 판단 요청에 전달되지 않았습니다."
    }

    $historyDryRun = $historyDryRunText | ConvertFrom-Json
    if ($historyDryRun.status -ne "dry_run" -or $historyDryRun.decision.reason -ne "recent history observed") {
        throw "최근 기록 확인용 dry-run 결과가 올바르지 않습니다."
    }

    $combatStopState = $combatStateJson | ConvertFrom-Json
    $combatStopState.state_id = "combat_loop_stop_test_" + [guid]::NewGuid().ToString("N")
    $combatStopState.phase = "combat_turn"
    $combatStopState.enemies = @()
    $combatStopState.legal_actions = @()
    $combatStopStateJson = $combatStopState | ConvertTo-Json -Depth 100
    $null = Invoke-RestMethod `
        -Method Post `
        -Uri "$BridgeUrl/state" `
        -ContentType "application/json; charset=utf-8" `
        -Body $combatStopStateJson `
        -TimeoutSec 5

    $combatStopRunLogDir = Join-Path ([System.IO.Path]::GetTempPath()) ("spiremind_combat_stop_" + [guid]::NewGuid().ToString("N"))
    New-Item -ItemType Directory -Force -Path $combatStopRunLogDir | Out-Null
    $combatStopText = & node $decisionLoopPath --bridge-url $BridgeUrl --mode heuristic --until-combat-end --max-decisions 3 --run-log-dir $combatStopRunLogDir
    if ($LASTEXITCODE -ne 0) {
        throw "전투 반복 루프 종료 감지 검증이 실패했습니다."
    }

    $combatStop = $combatStopText | ConvertFrom-Json
    if ($combatStop.status -ne "combat_loop_stopped" -or $combatStop.reason -ne "no_live_enemies") {
        throw "전투 반복 루프 종료 감지 결과가 올바르지 않습니다: $combatStopText"
    }
    if ($null -eq $combatStop.combat_outcome -or $combatStop.combat_outcome.reason -ne "no_live_enemies") {
        throw "전투 종료 결과 요약이 stdout에 포함되지 않았습니다."
    }
    if ($null -eq $combatStop.combat_outcome.player -or $null -eq $combatStop.combat_outcome.actions_applied) {
        throw "전투 종료 결과 요약에 플레이어와 행동 집계가 없습니다."
    }

    $combatStopLogPath = Join-Path $combatStopRunLogDir "combat_log.jsonl"
    if (-not (Test-Path $combatStopLogPath)) {
        throw "전투 반복 루프 종료 로그가 생성되지 않았습니다."
    }
    $combatStopLogText = Get-Content -Raw -Encoding UTF8 $combatStopLogPath
    if ($combatStopLogText -notmatch '"event_type":"combat_loop_stopped"' -or $combatStopLogText -notmatch '"event_type":"combat_ended"') {
        throw "전투 반복 루프 종료 이벤트가 기록되지 않았습니다."
    }
    if ($combatStopLogText -notmatch '"combat_outcome"') {
        throw "전투 반복 루프 종료 이벤트에 결과 요약이 기록되지 않았습니다."
    }

    $rewardStopState = $combatStateJson | ConvertFrom-Json
    $rewardStopState.state_id = "reward_loop_stop_test_" + [guid]::NewGuid().ToString("N")
    $rewardStopState.phase = "reward"
    $rewardStopState.enemies = @()
    $rewardStopState.legal_actions = @(
        [pscustomobject]@{
            action_id = "choose_reward_0_card_0"
            type = "choose_card_reward"
            reward_id = "reward_0"
            card_reward_index = 0
        }
    )
    $rewardPayload = [pscustomobject]@{
        reward_count = 1
        rewards = @(
            [pscustomobject]@{
                reward_id = "reward_0"
                type = "card_reward"
                cards = @(
                    [pscustomobject]@{
                        card_reward_index = 0
                        name = "Strike"
                    }
                )
            }
        )
    }
    $rewardStopState | Add-Member -NotePropertyName "reward" -NotePropertyValue $rewardPayload -Force
    $rewardStopStateJson = $rewardStopState | ConvertTo-Json -Depth 100
    $null = Invoke-RestMethod `
        -Method Post `
        -Uri "$BridgeUrl/state" `
        -ContentType "application/json; charset=utf-8" `
        -Body $rewardStopStateJson `
        -TimeoutSec 5

    $rewardStopRunLogDir = Join-Path ([System.IO.Path]::GetTempPath()) ("spiremind_reward_stop_" + [guid]::NewGuid().ToString("N"))
    New-Item -ItemType Directory -Force -Path $rewardStopRunLogDir | Out-Null
    $rewardStopText = & node $decisionLoopPath --bridge-url $BridgeUrl --mode heuristic --until-combat-end --max-decisions 3 --run-log-dir $rewardStopRunLogDir
    if ($LASTEXITCODE -ne 0) {
        throw "보상 화면 반복 루프 종료 감지 검증이 실패했습니다."
    }

    $rewardStop = $rewardStopText | ConvertFrom-Json
    if ($rewardStop.status -ne "combat_loop_stopped" -or $rewardStop.reason -ne "non_combat_phase:reward") {
        throw "보상 화면 종료 감지 결과가 올바르지 않습니다: $rewardStopText"
    }

    $mapStopState = $combatStateJson | ConvertFrom-Json
    $mapStopState.state_id = "map_loop_stop_test_" + [guid]::NewGuid().ToString("N")
    $mapStopState.phase = "map"
    $mapStopState.enemies = @()
    $mapStopState.legal_actions = @(
        [pscustomobject]@{
            action_id = "choose_map_r1_c0"
            type = "choose_map_node"
            node_id = "map_r1_c0"
            row = 1
            column = 0
            room_type = "Monster"
        }
    )
    $mapPayload = [pscustomobject]@{
        current = [pscustomobject]@{
            node_id = $null
            row = $null
            column = $null
            room_type = $null
        }
        available_next_nodes = @(
            [pscustomobject]@{
                node_id = "map_r1_c0"
                row = 1
                column = 0
                room_type = "Monster"
                reachable_now = $true
            }
        )
    }
    $mapStopState | Add-Member -NotePropertyName "map" -NotePropertyValue $mapPayload -Force
    $mapStopStateJson = $mapStopState | ConvertTo-Json -Depth 100
    $null = Invoke-RestMethod `
        -Method Post `
        -Uri "$BridgeUrl/state" `
        -ContentType "application/json; charset=utf-8" `
        -Body $mapStopStateJson `
        -TimeoutSec 5

    $mapStopRunLogDir = Join-Path ([System.IO.Path]::GetTempPath()) ("spiremind_map_stop_" + [guid]::NewGuid().ToString("N"))
    New-Item -ItemType Directory -Force -Path $mapStopRunLogDir | Out-Null
    $mapStopText = & node $decisionLoopPath --bridge-url $BridgeUrl --mode heuristic --until-combat-end --max-decisions 3 --run-log-dir $mapStopRunLogDir
    if ($LASTEXITCODE -ne 0) {
        throw "지도 화면 반복 루프 종료 감지 검증이 실패했습니다."
    }

    $mapStop = $mapStopText | ConvertFrom-Json
    if ($mapStop.status -ne "combat_loop_stopped" -or $mapStop.reason -ne "non_combat_phase:map") {
        throw "지도 화면 종료 감지 결과가 올바르지 않습니다: $mapStopText"
    }

    $null = Invoke-RestMethod `
        -Method Post `
        -Uri "$BridgeUrl/state" `
        -ContentType "application/json; charset=utf-8" `
        -Body $combatStateJson `
        -TimeoutSec 5

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
        supported_action_types = @(
            "play_card",
            "end_turn",
            "claim_gold_reward",
            "claim_relic_reward",
            "claim_potion_reward",
            "choose_card_reward",
            "skip_card_reward",
            "proceed_reward_screen",
            "choose_map_node",
            "choose_event_option",
            "choose_rest_site_option",
            "proceed_rest_site",
            "choose_card_selection",
            "confirm_card_selection"
        )
    } | ConvertTo-Json -Depth 8
    $staleClaim = Invoke-RestMethod `
        -Method Post `
        -Uri "$BridgeUrl/action/claim" `
        -ContentType "application/json; charset=utf-8" `
        -Body $staleClaimBody `
        -TimeoutSec 5

    $retryLatest = Invoke-RestMethod -Method Get -Uri "$BridgeUrl/action/latest" -TimeoutSec 5
    if ($staleClaim.status -eq "stale_retry_queued") {
        if ($retryLatest.latest_action.execution_status -ne "pending") {
            throw "재시도 행동이 pending 상태가 아닙니다: $($retryLatest.latest_action.execution_status)"
        }
    } elseif ($staleClaim.status -eq "stale") {
        if ($null -ne $retryLatest.action_plan) {
            $planFailure = $retryLatest.action_plan.failure
            if ($null -eq $planFailure -or [string]::IsNullOrWhiteSpace([string]$planFailure.reason)) {
                throw "stale claim이 재시도 없이 종료됐지만 실패 사유가 기록되지 않았습니다."
            }
        } elseif ($retryLatest.latest_action.execution_status -ne "stale" -or $retryLatest.latest_action.result -ne "stale") {
            throw "단일 행동 stale 결과가 latest_action에 기록되지 않았습니다."
        }
    } else {
        throw "stale claim 결과가 올바르지 않습니다: $($staleClaim.status)"
    }

    [pscustomobject]@{
        status = "PASS"
        bridge_url = $BridgeUrl
        dry_run_decision = $dryRun.decision
        codex_adapter_decision = $codexDryRun.decision
        wait_result_status = $waitResult.status
        run_log_dir = $runLogDir
        metrics = $metrics
        scenario_id = $scenarioConfig.scenario_id
        memory_summary = $memorySummary
        state_delta = $waitDecisionRecord.state_delta
        combat_stop_reason = $combatStop.reason
        combat_outcome = $combatStop.combat_outcome
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
