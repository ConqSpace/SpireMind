#!/usr/bin/env node
"use strict";

const fs = require("fs");
const http = require("http");
const https = require("https");
const path = require("path");
const { createDecider } = require("./deciders");

const DEFAULT_BRIDGE_URL = "http://127.0.0.1:17832";
const DEFAULT_POLL_MS = 250;
const DEFAULT_TIMEOUT_MS = 120000;
const DEFAULT_RESULT_TIMEOUT_MS = 60000;
const DEFAULT_CODEX_MODEL = "gpt-5.4-mini";

function parseArgs(argv) {
  const options = {
    bridgeUrl: process.env.SPIREMIND_BRIDGE_URL || DEFAULT_BRIDGE_URL,
    decisionBackend: process.env.SPIREMIND_DECISION_BACKEND || "command",
    command: process.env.SPIREMIND_DECIDER_COMMAND || "node",
    commandArgs: [],
    codexCommand: process.env.SPIREMIND_CODEX_COMMAND || (process.platform === "win32" ? "codex.cmd" : "codex"),
    model: process.env.SPIREMIND_CODEX_MODEL || DEFAULT_CODEX_MODEL,
    effort: process.env.SPIREMIND_CODEX_EFFORT || "low",
    runLogDir: process.env.SPIREMIND_RUN_LOG_DIR || "",
    playSessionId: process.env.SPIREMIND_PLAY_SESSION_ID || "",
    scenarioId: process.env.SPIREMIND_SCENARIO_ID || "agent_daemon",
    maxDecisions: readPositiveInteger(process.env.SPIREMIND_MAX_DECISIONS, 20),
    pollMs: DEFAULT_POLL_MS,
    timeoutMs: readPositiveInteger(process.env.SPIREMIND_TIMEOUT_MS, DEFAULT_TIMEOUT_MS),
    resultTimeoutMs: readPositiveInteger(process.env.SPIREMIND_RESULT_TIMEOUT_MS, DEFAULT_RESULT_TIMEOUT_MS),
    runCount: readPositiveInteger(process.env.SPIREMIND_RUN_COUNT, 1),
    autoTrivial: false,
    dismissTerminal: false,
    dryRun: false,
    selfTest: false,
    help: false
  };

  for (let index = 0; index < argv.length; index += 1) {
    const token = argv[index];
    if (token === "--help" || token === "-h") {
      options.help = true;
      continue;
    }

    if (token === "--bridge-url" && index + 1 < argv.length) {
      options.bridgeUrl = argv[index + 1];
      index += 1;
      continue;
    }

    if (token.startsWith("--bridge-url=")) {
      options.bridgeUrl = token.slice("--bridge-url=".length);
      continue;
    }

    if (token === "--decision-backend" && index + 1 < argv.length) {
      options.decisionBackend = argv[index + 1];
      index += 1;
      continue;
    }

    if (token === "--command" && index + 1 < argv.length) {
      options.command = argv[index + 1];
      index += 1;
      continue;
    }

    if (token === "--codex-command" && index + 1 < argv.length) {
      options.codexCommand = argv[index + 1];
      index += 1;
      continue;
    }

    if (token === "--model" && index + 1 < argv.length) {
      options.model = argv[index + 1];
      index += 1;
      continue;
    }

    if (token === "--effort" && index + 1 < argv.length) {
      options.effort = argv[index + 1];
      index += 1;
      continue;
    }

    if (token === "--command-arg" && index + 1 < argv.length) {
      options.commandArgs.push(argv[index + 1]);
      index += 1;
      continue;
    }

    if (token === "--run-log-dir" && index + 1 < argv.length) {
      options.runLogDir = argv[index + 1];
      index += 1;
      continue;
    }

    if (token === "--play-session-id" && index + 1 < argv.length) {
      options.playSessionId = argv[index + 1];
      index += 1;
      continue;
    }

    if (token === "--scenario-id" && index + 1 < argv.length) {
      options.scenarioId = argv[index + 1];
      index += 1;
      continue;
    }

    if (token === "--max-decisions" && index + 1 < argv.length) {
      options.maxDecisions = readPositiveInteger(argv[index + 1], options.maxDecisions);
      index += 1;
      continue;
    }

    if (token === "--run-count" && index + 1 < argv.length) {
      options.runCount = readPositiveInteger(argv[index + 1], options.runCount);
      index += 1;
      continue;
    }

    if (token === "--poll-ms" && index + 1 < argv.length) {
      options.pollMs = readPositiveInteger(argv[index + 1], options.pollMs);
      index += 1;
      continue;
    }

    if (token === "--timeout-ms" && index + 1 < argv.length) {
      options.timeoutMs = readPositiveInteger(argv[index + 1], options.timeoutMs);
      index += 1;
      continue;
    }

    if (token === "--result-timeout-ms" && index + 1 < argv.length) {
      options.resultTimeoutMs = readPositiveInteger(argv[index + 1], options.resultTimeoutMs);
      index += 1;
      continue;
    }

    if (token === "--auto-trivial") {
      options.autoTrivial = true;
      continue;
    }

    if (token === "--dismiss-terminal") {
      options.dismissTerminal = true;
      continue;
    }

    if (token === "--dry-run") {
      options.dryRun = true;
      continue;
    }

    if (token === "--self-test") {
      options.selfTest = true;
    }
  }

  if (options.commandArgs.length === 0) {
    options.commandArgs.push(path.join(__dirname, "codex_decider.js"));
  }

  return options;
}

function showHelp() {
  process.stdout.write([
    "SpireMind 상주 에이전트 데몬",
    "",
    "Usage:",
    "  node scripts/spiremind_agent_daemon.js --max-decisions 30 --run-log-dir <dir>",
    "  node scripts/spiremind_agent_daemon.js --dry-run --max-decisions 1",
    "  node scripts/spiremind_agent_daemon.js --auto-trivial --max-decisions 30",
    "",
    "기본 동작:",
    "  모든 판단 가능한 상태를 command 판단기, 기본 scripts/codex_decider.js, 로 보낸다.",
    "  판단이 필요 없어 보이는 행동도 기본값에서는 LLM 벤치마크에 포함한다.",
    "",
    "Options:",
    "  --bridge-url <url>          브리지 주소. 기본값 http://127.0.0.1:17832",
    "  --decision-backend <name>   command 또는 app-server. 기본값 command",
    "  --command <program>         판단기 실행 파일. 기본값 node",
    "  --command-arg <arg>         판단기 인자. 여러 번 지정 가능",
    "  --codex-command <program>   app-server 백엔드에서 사용할 Codex CLI. 기본값 codex.cmd",
    "  --model <model>             app-server 백엔드 모델. 기본값 gpt-5.4-mini",
    "  --effort <effort>           app-server 백엔드 추론 강도. 기본값 low",
    "  --run-log-dir <dir>         agent_config.json, agent_events.jsonl, metrics.json 기록 위치",
    "  --max-decisions <n>         최대 판단 수. 기본값 20",
    "  --run-count <n>             종료 리포트 후 다음 런을 이어 실행할 횟수. 기본값 1",
    "  --auto-trivial              단일 합법 행동 같은 단순 상태를 LLM 없이 처리한다",
    "  --dismiss-terminal          game_over/run_finished의 종료 확인 행동도 자동 또는 제출 대상으로 허용한다",
    "  --dry-run                   판단만 하고 브리지에 제출하지 않는다",
    "  --self-test                 브리지 없이 내부 분기만 점검한다",
    ""
  ].join("\n"));
}

function readPositiveInteger(value, fallbackValue) {
  const parsed = Number.parseInt(String(value), 10);
  return Number.isFinite(parsed) && parsed > 0 ? parsed : fallbackValue;
}

function sleep(ms) {
  return new Promise((resolve) => setTimeout(resolve, ms));
}

function isPlainObject(value) {
  return typeof value === "object" && value !== null && !Array.isArray(value);
}

function safeJsonParse(text) {
  try {
    return JSON.parse(String(text).replace(/^\uFEFF/, ""));
  } catch {
    return null;
  }
}

function nowIsoString() {
  return new Date().toISOString();
}

function appendUrl(baseUrlString, pathname) {
  return new URL(pathname, baseUrlString).toString();
}

function httpRequestJson(method, targetUrlString, body, timeoutMs) {
  return new Promise((resolve, reject) => {
    const targetUrl = new URL(targetUrlString);
    const transport = targetUrl.protocol === "https:" ? https : http;
    const bodyText = body === undefined ? "" : JSON.stringify(body);
    const request = transport.request(
      targetUrl,
      {
        method,
        headers: {
          Accept: "application/json",
          ...(body === undefined
            ? {}
            : {
                "Content-Type": "application/json; charset=utf-8",
                "Content-Length": Buffer.byteLength(bodyText, "utf8")
              })
        }
      },
      (response) => {
        const chunks = [];
        response.on("data", (chunk) => chunks.push(chunk));
        response.on("end", () => {
          const rawBody = Buffer.concat(chunks).toString("utf8");
          const parsed = rawBody.trim() === "" ? null : safeJsonParse(rawBody);
          if (rawBody.trim() !== "" && parsed === null) {
            reject(new Error(`브리지 응답이 JSON이 아닙니다: ${rawBody}`));
            return;
          }

          resolve({
            statusCode: typeof response.statusCode === "number" ? response.statusCode : 0,
            body: parsed
          });
        });
      }
    );

    request.setTimeout(timeoutMs, () => {
      request.destroy(new Error(`브리지 요청이 ${timeoutMs}ms 안에 끝나지 않았습니다.`));
    });
    request.on("error", reject);

    if (body !== undefined) {
      request.write(bodyText);
    }

    request.end();
  });
}

async function getCurrentState(options) {
  const response = await httpRequestJson("GET", appendUrl(options.bridgeUrl, "/state/current"), undefined, options.timeoutMs);
  if (response.statusCode !== 200 || !isPlainObject(response.body)) {
    throw new Error(`브리지 상태 조회 실패: HTTP ${response.statusCode}`);
  }

  return response.body;
}

async function getLatestAction(options) {
  const response = await httpRequestJson("GET", appendUrl(options.bridgeUrl, "/action/latest"), undefined, options.timeoutMs);
  if (response.statusCode !== 200 || !isPlainObject(response.body)) {
    throw new Error(`브리지 행동 조회 실패: HTTP ${response.statusCode}`);
  }

  return response.body;
}

async function submitDecision(options, decision, stateVersion, decisionSource) {
  const body = {
    ...decision,
    source: `spiremind_agent_daemon:${decisionSource}`,
    expected_state_version: stateVersion
  };
  const response = await httpRequestJson("POST", appendUrl(options.bridgeUrl, "/action/submit"), body, options.timeoutMs);
  if (response.statusCode < 200 || response.statusCode >= 300 || !isPlainObject(response.body)) {
    throw new Error(`행동 제출 실패: HTTP ${response.statusCode}`);
  }

  return response.body;
}

function hasPendingAction(snapshot) {
  const phase = getPhase(snapshot);
  if (phase === "adapter_card_selection" && getLegalActions(snapshot).length > 0) {
    return false;
  }

  const latestAction = isPlainObject(snapshot.latest_action) ? snapshot.latest_action : null;
  if (!latestAction) {
    return false;
  }

  const executionStatus = typeof latestAction.execution_status === "string" ? latestAction.execution_status : "";
  if (executionStatus === "pending" || executionStatus === "claimed" || executionStatus === "running") {
    return true;
  }

  const plan = isPlainObject(snapshot.action_plan) ? snapshot.action_plan : null;
  if (!plan) {
    return false;
  }

  return plan.status === "pending" || plan.status === "running";
}

function canRetrySameStateAfterStatus(status) {
  return status === "failed"
    || status === "unsupported"
    || status === "invalid"
    || status === "stale"
    || status === "stale_decision";
}

function getLegalActions(snapshot) {
  const state = isPlainObject(snapshot.state) ? snapshot.state : {};
  return Array.isArray(state.legal_actions) ? state.legal_actions.filter(isPlainObject) : [];
}

function getPhase(snapshot) {
  const state = isPlainObject(snapshot.state) ? snapshot.state : {};
  return typeof state.phase === "string" ? state.phase : "";
}

function getActionId(action) {
  return typeof action.action_id === "string" ? action.action_id : "";
}

function isTerminalPhase(phase) {
  return phase === "game_over" || phase === "run_finished";
}

function isTerminalDismissAction(actionId) {
  return actionId === "dismiss_game_over" || actionId === "dismiss_run_finished";
}

function findTerminalDismissAction(snapshot) {
  return getLegalActions(snapshot).find((action) => isTerminalDismissAction(getActionId(action))) || null;
}

function chooseTrivialDecision(snapshot, options) {
  const phase = getPhase(snapshot);
  const legalActions = getLegalActions(snapshot);
  if (legalActions.length === 0) {
    return null;
  }

  if (isTerminalPhase(phase) && !options.dismissTerminal) {
    return null;
  }

  if (legalActions.length === 1) {
    const actionId = getActionId(legalActions[0]);
    if (actionId === "") {
      return null;
    }

    if (isTerminalDismissAction(actionId) && !options.dismissTerminal) {
      return null;
    }

    return {
      selected_action_id: actionId,
      reason: "합법 행동이 하나뿐이라 자동 처리했습니다."
    };
  }

  const allDismiss = legalActions
    .map(getActionId)
    .filter((actionId) => actionId !== "")
    .every(isTerminalDismissAction);
  if (allDismiss && options.dismissTerminal) {
    return {
      selected_action_id: getActionId(legalActions[0]),
      reason: "종료 확인 화면의 닫기 행동을 자동 처리했습니다."
    };
  }

  return null;
}

function validateDecision(decision, snapshot) {
  if (!isPlainObject(decision)) {
    throw new Error("판단 결과가 JSON 객체가 아닙니다.");
  }

  const selectedActionId = typeof decision.selected_action_id === "string" ? decision.selected_action_id : "";
  if (selectedActionId === "") {
    throw new Error("selected_action_id가 비어 있습니다.");
  }

  const legalActionIds = new Set(getLegalActions(snapshot).map(getActionId).filter((actionId) => actionId !== ""));
  if (!legalActionIds.has(selectedActionId)) {
    throw new Error(`판단 결과가 현재 legal_actions에 없습니다: ${selectedActionId}`);
  }
}

function isDecisionLegalInSnapshot(decision, snapshot) {
  const selectedActionId = typeof decision.selected_action_id === "string" ? decision.selected_action_id : "";
  if (selectedActionId === "") {
    return false;
  }

  const legalActionIds = new Set(getLegalActions(snapshot).map(getActionId).filter((actionId) => actionId !== ""));
  return legalActionIds.has(selectedActionId);
}

async function refreshSnapshotForSubmission(options, snapshot, decision) {
  const refreshed = await getCurrentState(options);
  const selectedActionId = typeof decision.selected_action_id === "string" ? decision.selected_action_id : "";
  const refreshedActionIds = new Set(getLegalActions(refreshed).map(getActionId).filter((actionId) => actionId !== ""));

  if (selectedActionId !== "" && refreshedActionIds.has(selectedActionId) && !hasPendingAction(refreshed)) {
    return refreshed;
  }

  return null;
}

async function waitForReadySnapshot(options, lastSeenVersion) {
  const deadline = Date.now() + options.timeoutMs;
  let lastSnapshot = null;
  while (Date.now() <= deadline) {
    const snapshot = await getCurrentState(options);
    lastSnapshot = snapshot;
    const stateVersion = Number(snapshot.state_version);
    if (snapshot.status === "ready"
      && Number.isFinite(stateVersion)
      && stateVersion >= lastSeenVersion
      && !hasPendingAction(snapshot)
      && getLegalActions(snapshot).length > 0) {
      return snapshot;
    }

    await sleep(options.pollMs);
  }

  throw new Error(`결정 가능한 상태를 기다리다 시간 초과했습니다. 마지막 상태=${JSON.stringify(lastSnapshot)}`);
}

async function waitForSubmittedResult(options, planId, submissionId) {
  const deadline = Date.now() + options.resultTimeoutMs;
  let lastLatest = null;
  while (Date.now() <= deadline) {
    const latest = await getLatestAction(options);
    lastLatest = latest;
    const plan = isPlainObject(latest.action_plan) ? latest.action_plan : null;
    const latestAction = isPlainObject(latest.latest_action) ? latest.latest_action : null;
    if (planId && plan && plan.plan_id === planId && (plan.status === "completed" || plan.status === "failed")) {
      return latest;
    }

    if (submissionId && latestAction && latestAction.submission_id === submissionId) {
      const status = typeof latestAction.execution_status === "string" ? latestAction.execution_status : "";
      if (status === "completed"
        || status === "failed"
        || status === "invalid"
        || status === "stale"
        || status === "applied"
        || status === "unsupported"
        || status === "terminal_transition") {
        return latest;
      }
    }

    await sleep(options.pollMs);
  }

  throw new Error(`제출 결과 대기 시간이 초과되었습니다. latest=${JSON.stringify(lastLatest)}`);
}

async function retryInvalidDecisionIfStillLegal(options, decision, decisionSource, latestAction) {
  if (!isPlainObject(latestAction) || latestAction.execution_status !== "invalid") {
    return null;
  }

  const snapshot = await getCurrentState(options);
  const selectedActionId = typeof decision.selected_action_id === "string" ? decision.selected_action_id : "";
  const legalActionIds = new Set(getLegalActions(snapshot).map(getActionId).filter((actionId) => actionId !== ""));
  const stateVersion = Number(snapshot.state_version);
  if (selectedActionId === ""
    || !legalActionIds.has(selectedActionId)
    || !Number.isFinite(stateVersion)
    || hasPendingAction(snapshot)) {
    return null;
  }

  const submitted = await submitDecision(options, decision, stateVersion, decisionSource);
  const retriedLatestAction = isPlainObject(submitted.latest_action) ? submitted.latest_action : null;
  const retriedActionPlan = isPlainObject(submitted.action_plan) ? submitted.action_plan : null;
  const planId = retriedActionPlan && typeof retriedActionPlan.plan_id === "string" ? retriedActionPlan.plan_id : "";
  const submissionId = retriedLatestAction && typeof retriedLatestAction.submission_id === "string" ? retriedLatestAction.submission_id : "";
  return await waitForSubmittedResult(options, planId, submissionId);
}

function ensureRunLogDir(runLogDir) {
  if (typeof runLogDir !== "string" || runLogDir.trim() === "") {
    return null;
  }

  const resolved = path.resolve(runLogDir);
  fs.mkdirSync(resolved, { recursive: true });
  return resolved;
}

function writeJsonFile(filePath, value) {
  fs.writeFileSync(filePath, `${JSON.stringify(value, null, 2)}\n`, "utf8");
}

function appendJsonLine(filePath, value) {
  fs.appendFileSync(filePath, `${JSON.stringify(value)}\n`, "utf8");
}

function createMetrics() {
  return {
    started_at: nowIsoString(),
    finished_at: null,
    decisions: 0,
    llm_decisions: 0,
    auto_trivial_decisions: 0,
    dry_run_decisions: 0,
    submit_failures: 0,
    result_timeouts: 0,
    completed_runs: 0,
    total_decision_ms: 0,
    max_decision_ms: 0
  };
}

function updateMetrics(metrics, record) {
  metrics.decisions += 1;
  if (record.decision_source === "llm" || record.decision_source === "llm_app_server") {
    metrics.llm_decisions += 1;
  }

  if (record.decision_source === "auto_trivial") {
    metrics.auto_trivial_decisions += 1;
  }

  if (record.status === "dry_run") {
    metrics.dry_run_decisions += 1;
  }

  if (record.status === "submit_failed") {
    metrics.submit_failures += 1;
  }

  if (record.status === "result_timeout") {
    metrics.result_timeouts += 1;
  }

  metrics.total_decision_ms += record.decision_ms;
  metrics.max_decision_ms = Math.max(metrics.max_decision_ms, record.decision_ms);
}

function writeRunConfig(runLogDir, options) {
  if (!runLogDir) {
    return;
  }

  writeJsonFile(path.join(runLogDir, "agent_config.json"), {
    bridge_url: options.bridgeUrl,
    decision_backend: options.decisionBackend,
    command: options.command,
    command_args: options.commandArgs,
    codex_command: options.codexCommand,
    model: options.model,
    effort: options.effort,
    scenario_id: options.scenarioId,
    play_session_id: options.playSessionId,
    max_decisions: options.maxDecisions,
    run_count: options.runCount,
    poll_ms: options.pollMs,
    timeout_ms: options.timeoutMs,
    result_timeout_ms: options.resultTimeoutMs,
    benchmark_mode: !options.autoTrivial,
    auto_trivial: options.autoTrivial,
    dismiss_terminal: options.dismissTerminal,
    dry_run: options.dryRun,
    created_at: nowIsoString()
  });
}

function writeEvent(runLogDir, record) {
  if (!runLogDir) {
    return;
  }

  appendJsonLine(path.join(runLogDir, "agent_events.jsonl"), record);
}

function summarizeDecisionRecord(record) {
  const latestAction = isPlainObject(record.latest_action) ? record.latest_action : {};
  const preSummary = isPlainObject(latestAction.pre_action_state_summary)
    ? latestAction.pre_action_state_summary
    : {};
  const postSummary = isPlainObject(latestAction.post_result_state_summary)
    ? latestAction.post_result_state_summary
    : {};
  return {
    at: record.at,
    phase: record.phase,
    state_version: record.state_version,
    status: record.status,
    selected_action_id: record.selected_action_id,
    reason: record.reason,
    legal_action_ids: Array.isArray(latestAction.legal_action_ids) ? latestAction.legal_action_ids : [],
    pre: {
      player: isPlainObject(preSummary.player) ? preSummary.player : null,
      hand: Array.isArray(preSummary.hand) ? preSummary.hand : [],
      enemies: Array.isArray(preSummary.enemies) ? preSummary.enemies : []
    },
    post: {
      player: isPlainObject(postSummary.player) ? postSummary.player : null,
      hand: Array.isArray(postSummary.hand) ? postSummary.hand : [],
      enemies: Array.isArray(postSummary.enemies) ? postSummary.enemies : []
    },
    error: record.error
  };
}

function buildPostRunReportRequest(metrics, records, terminalRecord, terminalSnapshot, runIndex) {
  const recentRecords = records.slice(-30).map(summarizeDecisionRecord);
  return {
    task: "post_run_report",
    run_index: runIndex,
    instruction: [
      "You are the same Slay the Spire 2 play agent that made these decisions.",
      "Write the report in Korean.",
      "Be candid about why the run ended, what you did well, what you did poorly, and what to change next run.",
      "Separate adapter observations from strategic mistakes. Do not blame the adapter unless the logs show an execution or legality problem."
    ],
    metrics,
    terminal: {
      record: terminalRecord,
      snapshot: terminalSnapshot
    },
    recent_decisions: recentRecords
  };
}

async function writePostRunReport(options, runLogDir, metrics, records, terminalRecord, terminalSnapshot, runIndex) {
  if (!options.decider || typeof options.decider.report !== "function" || !runLogDir) {
    return null;
  }

  const startedAt = Date.now();
  const request = buildPostRunReportRequest(metrics, records, terminalRecord, terminalSnapshot, runIndex);
  try {
    const report = await options.decider.report(request);
    const record = {
      type: "post_run_report",
      at: nowIsoString(),
      status: "completed",
      run_index: runIndex,
      report_ms: Date.now() - startedAt,
      report
    };
    writeJsonFile(path.join(runLogDir, `post_run_report_run_${runIndex}.json`), record);
    writeJsonFile(path.join(runLogDir, "post_run_report.json"), record);
    writeEvent(runLogDir, record);
    process.stdout.write(`${JSON.stringify(record, null, 2)}\n`);
    return record;
  } catch (error) {
    const record = {
      type: "post_run_report",
      at: nowIsoString(),
      status: "failed",
      run_index: runIndex,
      report_ms: Date.now() - startedAt,
      error: error instanceof Error ? error.message : String(error)
    };
    writeJsonFile(path.join(runLogDir, `post_run_report_run_${runIndex}.failed.json`), record);
    writeJsonFile(path.join(runLogDir, "post_run_report.failed.json"), record);
    writeEvent(runLogDir, record);
    process.stdout.write(`${JSON.stringify(record, null, 2)}\n`);
    return record;
  }
}

async function dismissTerminalForNextRun(options, snapshot, runIndex) {
  const action = findTerminalDismissAction(snapshot);
  const actionId = getActionId(action);
  if (!actionId) {
    throw new Error("다음 런을 시작하려 했지만 종료 화면 닫기 행동이 없습니다.");
  }

  const stateVersion = Number(snapshot.state_version);
  const decision = {
    selected_action_id: actionId,
    reason: `런 ${runIndex} 종료 리포트를 작성했으므로 다음 런을 위해 종료 화면을 닫습니다.`
  };
  const submitted = await submitDecision(
    options,
    decision,
    Number.isFinite(stateVersion) ? stateVersion : 0,
    "auto_terminal_repeat");
  const latestAction = isPlainObject(submitted.latest_action) ? submitted.latest_action : null;
  const actionPlan = isPlainObject(submitted.action_plan) ? submitted.action_plan : null;
  const planId = actionPlan && typeof actionPlan.plan_id === "string" ? actionPlan.plan_id : "";
  const submissionId = latestAction && typeof latestAction.submission_id === "string" ? latestAction.submission_id : "";
  const finalLatest = await waitForSubmittedResult(options, planId, submissionId);
  return {
    type: "terminal_dismissed_for_next_run",
    at: nowIsoString(),
    run_index: runIndex,
    selected_action_id: actionId,
    latest_action: isPlainObject(finalLatest.latest_action) ? finalLatest.latest_action : latestAction,
    action_plan: isPlainObject(finalLatest.action_plan) ? finalLatest.action_plan : actionPlan
  };
}

async function decide(options, snapshot) {
  if (options.autoTrivial) {
    const trivialDecision = chooseTrivialDecision(snapshot, options);
    if (trivialDecision) {
      return {
        decision: trivialDecision,
        decisionSource: "auto_trivial"
      };
    }
  }

  const decision = await options.decider.decide(snapshot);
  return {
    decision,
    decisionSource: options.decider.source
  };
}

async function runLoop(options) {
  const runLogDir = ensureRunLogDir(options.runLogDir);
  writeRunConfig(runLogDir, options);
  const metrics = createMetrics();
  const records = [];
  let completedRuns = 0;
  let lastSeenVersion = 0;
  options.decider = createDecider(options);
  await options.decider.start();

  try {
    for (let index = 0; index < options.maxDecisions; index += 1) {
    const snapshot = await waitForReadySnapshot(options, lastSeenVersion);
    const stateVersion = Number(snapshot.state_version);
    const phase = getPhase(snapshot);
    if (isTerminalPhase(phase) && !options.dismissTerminal) {
      completedRuns += 1;
      metrics.completed_runs = completedRuns;
      const record = {
        type: "terminal_stop",
        at: nowIsoString(),
        run_index: completedRuns,
        state_version: stateVersion,
        phase,
        reason: completedRuns < options.runCount
          ? `런 ${completedRuns} 종료 화면입니다. 사후 리포트 작성 후 다음 런으로 넘어갑니다.`
          : "종료 화면입니다. 요청한 런 수를 채웠으므로 멈춥니다."
      };
      writeEvent(runLogDir, record);
      process.stdout.write(`${JSON.stringify(record, null, 2)}\n`);
      await writePostRunReport(options, runLogDir, metrics, records, record, snapshot, completedRuns);
      if (completedRuns >= options.runCount) {
        break;
      }

      const dismissed = await dismissTerminalForNextRun(options, snapshot, completedRuns);
      writeEvent(runLogDir, dismissed);
      process.stdout.write(`${JSON.stringify(dismissed, null, 2)}\n`);
      lastSeenVersion = stateVersion + 1;
      records.length = 0;
      continue;
    }

    const startedAt = Date.now();
    const { decision, decisionSource } = await decide(options, snapshot);
    const decisionMs = Date.now() - startedAt;
    let decisionSnapshot = snapshot;
    if (!isDecisionLegalInSnapshot(decision, snapshot)) {
      const refreshedSnapshot = await getCurrentState(options);
      if (!isDecisionLegalInSnapshot(decision, refreshedSnapshot)) {
        const record = {
          type: "stale_decision",
          at: nowIsoString(),
          status: "stale_decision",
          decision_source: decisionSource,
          state_version: Number(refreshedSnapshot.state_version),
          phase: getPhase(refreshedSnapshot),
          selected_action_id: decision.selected_action_id,
          reason: "LLM 판단 도착 전에 합법 행동 목록이 바뀌어 이번 판단을 버리고 최신 상태에서 다시 판단합니다.",
          decision_ms: decisionMs,
          latest_action: null,
          action_plan: null,
          error: null
        };
        updateMetrics(metrics, record);
        records.push(record);
        writeEvent(runLogDir, record);
        process.stdout.write(`${JSON.stringify(record, null, 2)}\n`);
        lastSeenVersion = Number.isFinite(Number(refreshedSnapshot.state_version))
          ? Number(refreshedSnapshot.state_version)
          : lastSeenVersion;
        continue;
      }

      decisionSnapshot = refreshedSnapshot;
    }

    validateDecision(decision, decisionSnapshot);

    let status = "dry_run";
    let latestAction = null;
    let actionPlan = null;
    let errorText = null;
    if (!options.dryRun) {
      try {
        const submissionSnapshot = await refreshSnapshotForSubmission(options, decisionSnapshot, decision);
        if (!submissionSnapshot) {
          status = "stale_decision";
        } else {
          const submissionStateVersion = Number(submissionSnapshot.state_version);
          const submitted = await submitDecision(
            options,
            decision,
            Number.isFinite(submissionStateVersion) ? submissionStateVersion : stateVersion,
            decisionSource);
          latestAction = isPlainObject(submitted.latest_action) ? submitted.latest_action : null;
          actionPlan = isPlainObject(submitted.action_plan) ? submitted.action_plan : null;
          const planId = actionPlan && typeof actionPlan.plan_id === "string" ? actionPlan.plan_id : "";
          const submissionId = latestAction && typeof latestAction.submission_id === "string" ? latestAction.submission_id : "";
          const finalLatest = await waitForSubmittedResult(options, planId, submissionId);
          latestAction = isPlainObject(finalLatest.latest_action) ? finalLatest.latest_action : latestAction;
          actionPlan = isPlainObject(finalLatest.action_plan) ? finalLatest.action_plan : actionPlan;
          const retriedLatest = await retryInvalidDecisionIfStillLegal(options, decision, decisionSource, latestAction);
          if (retriedLatest) {
            latestAction = isPlainObject(retriedLatest.latest_action) ? retriedLatest.latest_action : latestAction;
            actionPlan = isPlainObject(retriedLatest.action_plan) ? retriedLatest.action_plan : actionPlan;
          }
          status = actionPlan && typeof actionPlan.status === "string"
            ? actionPlan.status
            : latestAction && typeof latestAction.execution_status === "string"
            ? latestAction.execution_status
            : "submitted";
        }
      } catch (error) {
        errorText = error instanceof Error ? error.message : String(error);
        status = errorText.includes("대기 시간이 초과") ? "result_timeout" : "submit_failed";
      }
    }

    const record = {
      type: "decision",
      at: nowIsoString(),
      status,
      decision_source: decisionSource,
      state_version: stateVersion,
      phase,
      selected_action_id: decision.selected_action_id,
      reason: typeof decision.reason === "string" ? decision.reason : null,
      decision_ms: decisionMs,
      latest_action: latestAction,
      action_plan: actionPlan,
      error: errorText
    };
    updateMetrics(metrics, record);
    records.push(record);
    writeEvent(runLogDir, record);
    process.stdout.write(`${JSON.stringify(record, null, 2)}\n`);

    if (errorText) {
      break;
    }

    lastSeenVersion = canRetrySameStateAfterStatus(status) ? stateVersion : stateVersion + 1;
    }
  } finally {
    if (options.decider) {
      options.decider.stop();
    }
  }

  metrics.finished_at = nowIsoString();
  if (runLogDir) {
    writeJsonFile(path.join(runLogDir, "metrics.json"), metrics);
  }
}

function runSelfTest() {
  const snapshot = {
    status: "ready",
    state_version: 7,
    state: {
      phase: "reward",
      legal_actions: [
        { action_id: "proceed_rewards", action_type: "proceed_rewards" }
      ]
    }
  };
  const autoChoice = chooseTrivialDecision(snapshot, { autoTrivial: true, dismissTerminal: false });
  if (!autoChoice || autoChoice.selected_action_id !== "proceed_rewards") {
    throw new Error("auto-trivial 모드의 단일 행동 선택이 실패했습니다.");
  }

  const terminalSnapshot = {
    status: "ready",
    state_version: 8,
    state: {
      phase: "game_over",
      legal_actions: [
        { action_id: "dismiss_game_over", action_type: "dismiss_game_over" }
      ]
    }
  };
  const terminalChoice = chooseTrivialDecision(terminalSnapshot, { autoTrivial: true, dismissTerminal: false });
  if (terminalChoice !== null) {
    throw new Error("종료 화면은 --dismiss-terminal 없이 자동 선택하면 안 됩니다.");
  }

  process.stdout.write(`${JSON.stringify({ status: "self_test_passed" }, null, 2)}\n`);
}

async function main() {
  const options = parseArgs(process.argv.slice(2));
  if (options.help) {
    showHelp();
    return;
  }

  if (options.selfTest) {
    runSelfTest();
    return;
  }

  await runLoop(options);
}

main().catch((error) => {
  process.stderr.write(`${error instanceof Error ? error.stack || error.message : String(error)}\n`);
  process.exitCode = 1;
});
