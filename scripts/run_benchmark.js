#!/usr/bin/env node
"use strict";

const { spawn } = require("child_process");
const fs = require("fs");
const http = require("http");
const https = require("https");
const os = require("os");
const path = require("path");

const DEFAULT_BRIDGE_URL = "http://127.0.0.1:17832";
const DEFAULT_POLL_MS = 500;
const DEFAULT_MAX_RUNTIME_MS = 15 * 60 * 1000;
const DEFAULT_SHUTDOWN_GRACE_MS = 2500;

function parseArgs(argv) {
  const options = {
    benchmarkDir: "",
    deciderId: "llm_current",
    seedId: "seed_0001",
    repeatIndex: 1,
    bridgeUrl: process.env.SPIREMIND_BRIDGE_URL || DEFAULT_BRIDGE_URL,
    outputRoot: "",
    maxRuntimeMs: null,
    allowStartInCombat: false,
    dryRun: false,
    noDaemon: false,
    selfTest: false,
    help: false
  };

  for (let index = 0; index < argv.length; index += 1) {
    const token = argv[index];
    if (token === "--help" || token === "-h") {
      options.help = true;
      continue;
    }

    if (token === "--self-test") {
      options.selfTest = true;
      continue;
    }

    if (token === "--dry-run") {
      options.dryRun = true;
      continue;
    }

    if (token === "--no-daemon") {
      options.noDaemon = true;
      continue;
    }

    if (token === "--allow-start-in-combat") {
      options.allowStartInCombat = true;
      continue;
    }

    if (token === "--benchmark-dir" && index + 1 < argv.length) {
      options.benchmarkDir = argv[index + 1];
      index += 1;
      continue;
    }

    if (token.startsWith("--benchmark-dir=")) {
      options.benchmarkDir = token.slice("--benchmark-dir=".length);
      continue;
    }

    if (token === "--decider" && index + 1 < argv.length) {
      options.deciderId = argv[index + 1];
      index += 1;
      continue;
    }

    if (token.startsWith("--decider=")) {
      options.deciderId = token.slice("--decider=".length);
      continue;
    }

    if (token === "--seed" && index + 1 < argv.length) {
      options.seedId = argv[index + 1];
      index += 1;
      continue;
    }

    if (token.startsWith("--seed=")) {
      options.seedId = token.slice("--seed=".length);
      continue;
    }

    if (token === "--repeat-index" && index + 1 < argv.length) {
      options.repeatIndex = readPositiveInteger(argv[index + 1], options.repeatIndex);
      index += 1;
      continue;
    }

    if (token.startsWith("--repeat-index=")) {
      options.repeatIndex = readPositiveInteger(token.slice("--repeat-index=".length), options.repeatIndex);
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

    if (token === "--output-root" && index + 1 < argv.length) {
      options.outputRoot = argv[index + 1];
      index += 1;
      continue;
    }

    if (token.startsWith("--output-root=")) {
      options.outputRoot = token.slice("--output-root=".length);
      continue;
    }

    if (token === "--max-runtime-ms" && index + 1 < argv.length) {
      options.maxRuntimeMs = readPositiveInteger(argv[index + 1], null);
      index += 1;
      continue;
    }

    if (token.startsWith("--max-runtime-ms=")) {
      options.maxRuntimeMs = readPositiveInteger(token.slice("--max-runtime-ms=".length), null);
    }
  }

  return options;
}

function showHelp() {
  process.stdout.write([
    "SpireMind 벤치마크 실행 관리자",
    "",
    "Usage:",
    "  node scripts/run_benchmark.js --benchmark-dir .\\benchmarks\\B0_NEOW_FIRST_COMBAT --decider llm_current --seed seed_0001 --repeat-index 1",
    "",
    "Options:",
    "  --benchmark-dir <dir>   scenario_config.json이 있는 벤치마크 폴더",
    "  --decider <id>          deciders/<id>.json을 사용한다. 기본값 llm_current",
    "  --seed <id>             seeds.json의 seed_id. 기본값 seed_0001",
    "  --repeat-index <n>      반복 실행 번호. 기본값 1",
    "  --bridge-url <url>      브리지 주소. 기본값 http://127.0.0.1:17832",
    "  --output-root <dir>     실행 산출물 루트. 기본값 <benchmark-dir>/runs",
    "  --max-runtime-ms <n>    이번 실행의 최대 감시 시간. 설정 파일 값을 덮어씀",
    "  --allow-start-in-combat 현재 전투 상태에서 시작해 전투 종료를 감시함",
    "  --dry-run               데몬과 브리지 없이 폴더와 요약만 생성",
    "  --no-daemon             브리지만 감시하고 데몬은 실행하지 않음",
    "  --self-test             브리지 없이 종료 조건 판정 로직 점검",
    ""
  ].join("\n"));
}

function readPositiveInteger(value, fallbackValue) {
  const parsed = Number.parseInt(String(value), 10);
  return Number.isFinite(parsed) && parsed > 0 ? parsed : fallbackValue;
}

function isPlainObject(value) {
  return typeof value === "object" && value !== null && !Array.isArray(value);
}

function nowIsoString() {
  return new Date().toISOString();
}

function sleep(ms) {
  return new Promise((resolve) => setTimeout(resolve, ms));
}

function ensureDirectory(directoryPath) {
  fs.mkdirSync(directoryPath, { recursive: true });
}

function readJsonFile(filePath) {
  const text = fs.readFileSync(filePath, "utf8").replace(/^\uFEFF/, "");
  return JSON.parse(text);
}

function writeJsonFile(filePath, value) {
  fs.writeFileSync(filePath, `${JSON.stringify(value, null, 2)}\n`, "utf8");
}

function copyJsonFile(sourcePath, targetPath) {
  const value = readJsonFile(sourcePath);
  writeJsonFile(targetPath, value);
}

function appendText(filePath, text) {
  fs.appendFileSync(filePath, text, "utf8");
}

function appendUrl(baseUrlString, pathname) {
  return new URL(pathname, baseUrlString).toString();
}

function httpRequestJson(method, targetUrlString, timeoutMs) {
  return new Promise((resolve, reject) => {
    const targetUrl = new URL(targetUrlString);
    const transport = targetUrl.protocol === "https:" ? https : http;
    const request = transport.request(
      targetUrl,
      {
        method,
        headers: { Accept: "application/json" }
      },
      (response) => {
        const chunks = [];
        response.on("data", (chunk) => chunks.push(chunk));
        response.on("end", () => {
          const rawBody = Buffer.concat(chunks).toString("utf8");
          let parsedBody = null;
          if (rawBody.trim() !== "") {
            try {
              parsedBody = JSON.parse(rawBody);
            } catch (error) {
              reject(new Error(`브리지 응답이 JSON이 아닙니다: ${rawBody}`));
              return;
            }
          }

          resolve({
            statusCode: typeof response.statusCode === "number" ? response.statusCode : 0,
            body: parsedBody
          });
        });
      }
    );

    request.setTimeout(timeoutMs, () => {
      request.destroy(new Error(`브리지 요청이 ${timeoutMs}ms 안에 끝나지 않았습니다.`));
    });
    request.on("error", reject);
    request.end();
  });
}

async function getBridgeJson(bridgeUrl, pathname, timeoutMs) {
  const response = await httpRequestJson("GET", appendUrl(bridgeUrl, pathname), timeoutMs);
  if (response.statusCode !== 200 || !isPlainObject(response.body)) {
    throw new Error(`브리지 조회 실패: ${pathname}, HTTP ${response.statusCode}`);
  }

  return response.body;
}

function loadBenchmarkConfig(options) {
  const benchmarkDir = options.benchmarkDir
    ? path.resolve(options.benchmarkDir)
    : path.resolve("benchmarks", "B0_NEOW_FIRST_COMBAT");
  const scenarioPath = path.join(benchmarkDir, "scenario_config.json");
  const seedsPath = path.join(benchmarkDir, "seeds.json");
  const deciderPath = path.join(benchmarkDir, "deciders", `${options.deciderId}.json`);

  if (!fs.existsSync(scenarioPath)) {
    throw new Error(`scenario_config.json을 찾지 못했습니다: ${scenarioPath}`);
  }

  if (!fs.existsSync(seedsPath)) {
    throw new Error(`seeds.json을 찾지 못했습니다: ${seedsPath}`);
  }

  if (!fs.existsSync(deciderPath)) {
    throw new Error(`decider 설정을 찾지 못했습니다: ${deciderPath}`);
  }

  const scenario = readJsonFile(scenarioPath);
  const seeds = readJsonFile(seedsPath);
  const decider = readJsonFile(deciderPath);
  const seed = Array.isArray(seeds.seeds)
    ? seeds.seeds.find((candidate) => candidate.seed_id === options.seedId)
    : null;

  if (!seed) {
    throw new Error(`seeds.json에서 seed_id '${options.seedId}'를 찾지 못했습니다.`);
  }

  if (decider.decider_id !== options.deciderId) {
    throw new Error(`decider_id가 파일명과 다릅니다. expected=${options.deciderId}, actual=${decider.decider_id}`);
  }

  return {
    benchmarkDir,
    scenarioPath,
    seedsPath,
    deciderPath,
    scenario,
    seeds,
    seed,
    decider
  };
}

function makeRunDirectory(config, options) {
  const outputRoot = options.outputRoot
    ? path.resolve(options.outputRoot)
    : path.join(config.benchmarkDir, "runs");
  const runDirectory = path.join(
    outputRoot,
    options.seedId,
    `${options.deciderId}_run_${String(options.repeatIndex).padStart(3, "0")}`);
  ensureDirectory(runDirectory);
  copyJsonFile(config.scenarioPath, path.join(runDirectory, "scenario_config.json"));
  copyJsonFile(config.deciderPath, path.join(runDirectory, "decider_config.json"));
  writeJsonFile(path.join(runDirectory, "seed_config.json"), config.seed);
  return runDirectory;
}

function normalizePhase(snapshot) {
  const state = isPlainObject(snapshot && snapshot.state) ? snapshot.state : {};
  const phase = typeof state.phase === "string" ? state.phase.trim() : "";
  if (phase !== "") {
    return phase;
  }

  const currentState = isPlainObject(snapshot && snapshot.currentState) ? snapshot.currentState : {};
  return typeof currentState.phase === "string" ? currentState.phase.trim() : "";
}

function getStateId(snapshot) {
  if (typeof snapshot.state_id === "string" && snapshot.state_id.trim() !== "") {
    return snapshot.state_id.trim();
  }

  const state = isPlainObject(snapshot.state) ? snapshot.state : {};
  if (typeof state.state_id === "string" && state.state_id.trim() !== "") {
    return state.state_id.trim();
  }

  return "";
}

function getLegalActions(snapshot) {
  const state = isPlainObject(snapshot.state) ? snapshot.state : {};
  return Array.isArray(state.legal_actions) ? state.legal_actions.filter(isPlainObject) : [];
}

function getActionType(action) {
  const candidate = action.type || action.action_type || action.actionType;
  return typeof candidate === "string" ? candidate.trim() : "";
}

function getActionId(action) {
  const candidate = action.action_id || action.actionId;
  return typeof candidate === "string" ? candidate.trim() : "";
}

function getActionTextKey(action) {
  const candidate = action.text_key || action.textKey;
  return typeof candidate === "string" ? candidate.trim() : "";
}

function isNeowPhase(phase) {
  const normalized = phase.toLowerCase();
  return normalized === "neow"
    || normalized === "neow_reward"
    || normalized === "neow_bonus"
    || normalized === "start_bonus";
}

function hasNeowAction(snapshot) {
  return getLegalActions(snapshot).some((action) => {
    const actionId = getActionId(action).toLowerCase();
    const actionType = getActionType(action).toLowerCase();
    const textKey = getActionTextKey(action).toLowerCase();
    return actionId.includes("neow")
      || actionType.includes("neow")
      || textKey.includes("neow")
      || actionType === "choose_start_bonus"
      || actionType === "choose_neow_reward";
  });
}

function isMapPhase(phase) {
  return phase.toLowerCase() === "map";
}

function isCombatPhase(phase) {
  const normalized = phase.toLowerCase();
  return normalized === "combat"
    || normalized === "combat_turn"
    || normalized === "combat_play";
}

function isPostCombatPhase(phase) {
  const normalized = phase.toLowerCase();
  return normalized === "reward"
    || normalized === "rewards"
    || normalized === "card_reward"
    || normalized === "game_over"
    || normalized === "run_finished";
}

function isTerminalBenchmarkPhase(phase) {
  const normalized = phase.toLowerCase();
  return normalized === "game_over" || normalized === "run_finished";
}

function createStopState(options = {}) {
  return {
    stopRule: typeof options.stopRule === "string" && options.stopRule.trim() !== ""
      ? options.stopRule.trim()
      : "first_combat_finished",
    neowSeen: false,
    mapSeenAfterNeow: false,
    firstCombatSeen: false,
    firstCombatFinished: false,
    allowStartInCombat: options.allowStartInCombat === true,
    stopReason: null,
    phaseTrace: [],
    stateTrace: [],
    agentEventCount: 0,
    lastPhase: "",
    lastStateId: ""
  };
}

function updateStopState(stopState, snapshot) {
  const phase = normalizePhase(snapshot);
  const stateId = getStateId(snapshot);
  const hadSeenCombatBeforeSnapshot = stopState.firstCombatSeen;
  if (phase !== "" && phase !== stopState.lastPhase) {
    stopState.phaseTrace.push(phase);
  }

  if (stateId !== "" && stateId !== stopState.lastStateId) {
    stopState.stateTrace.push({
      at: nowIsoString(),
      state_id: stateId,
      phase
    });
  }

  const sawNeow = isNeowPhase(phase) || hasNeowAction(snapshot);
  if (sawNeow) {
    stopState.neowSeen = true;
  }

  if (stopState.neowSeen && isMapPhase(phase)) {
    stopState.mapSeenAfterNeow = true;
  }

  if ((stopState.neowSeen || stopState.mapSeenAfterNeow) && isCombatPhase(phase)) {
    stopState.firstCombatSeen = true;
  }

  if (!stopState.neowSeen && stopState.allowStartInCombat && isCombatPhase(phase)) {
    stopState.firstCombatSeen = true;
  }

  if (stopState.stopRule === "first_combat_finished"
    && hadSeenCombatBeforeSnapshot
    && !isCombatPhase(phase)
    && isPostCombatPhase(phase)) {
    stopState.firstCombatFinished = true;
    stopState.stopReason = phase === "game_over" ? "first_combat_game_over" : "first_combat_finished";
  }

  if (stopState.stopRule !== "first_combat_finished" && isTerminalBenchmarkPhase(phase)) {
    stopState.stopReason = phase === "game_over" ? "game_over" : "run_finished";
  }

  stopState.lastPhase = phase;
  stopState.lastStateId = stateId;
  return stopState;
}

function makeSyntheticSnapshot(phase, stateId) {
  return {
    state_id: stateId || "",
    state: {
      phase: phase || "",
      state_id: stateId || "",
      legal_actions: []
    }
  };
}

function updateStopStateFromAgentEvents(stopState, runDirectory) {
  const eventPath = path.join(runDirectory, "agent_events.jsonl");
  const records = readJsonLines(eventPath);
  for (let index = stopState.agentEventCount; index < records.length; index++) {
    const record = records[index];
    if (!isPlainObject(record)) {
      continue;
    }

    const latestAction = isPlainObject(record.latest_action) ? record.latest_action : {};
    const preSummary = isPlainObject(latestAction.pre_action_state_summary)
      ? latestAction.pre_action_state_summary
      : {};
    const postSummary = isPlainObject(latestAction.post_result_state_summary)
      ? latestAction.post_result_state_summary
      : {};

    const prePhase = typeof preSummary.phase === "string" ? preSummary.phase : record.phase;
    const preStateId = typeof preSummary.state_id === "string" ? preSummary.state_id : latestAction.pre_action_state_id;
    if (typeof prePhase === "string" && prePhase.trim() !== "") {
      updateStopState(stopState, makeSyntheticSnapshot(prePhase, preStateId));
    }

    const postPhase = typeof postSummary.phase === "string" ? postSummary.phase : "";
    const postStateId = typeof postSummary.state_id === "string" ? postSummary.state_id : latestAction.post_result_state_id;
    if (postPhase.trim() !== "") {
      updateStopState(stopState, makeSyntheticSnapshot(postPhase, postStateId));
    }

    if (stopState.stopReason) {
      stopState.agentEventCount = index + 1;
      return stopState;
    }
  }

  stopState.agentEventCount = records.length;
  return stopState;
}

function createInitialSummary(config, options, runDirectory, status, stopReason) {
  return {
    benchmark_id: config.scenario.benchmark_id,
    seed_id: options.seedId,
    seed: config.seed.seed || null,
    decider_id: options.deciderId,
    repeat_index: options.repeatIndex,
    status,
    stop_reason: stopReason,
    started_at: nowIsoString(),
    finished_at: null,
    bridge_url: options.bridgeUrl,
    run_directory: runDirectory,
    allow_start_in_combat: options.allowStartInCombat,
    scenario: {
      character: config.scenario.character || null,
      ascension: config.scenario.ascension ?? null,
      stop_rule: config.scenario.stop_rule || null,
      max_decisions: config.scenario.max_decisions ?? null,
      human_intervention: config.scenario.human_intervention || null
    },
    decider: {
      decision_backend: config.decider.decision_backend || null,
      model: config.decider.model || null,
      effort: config.decider.effort || null,
      prompt_version: config.decider.prompt_version || null
    },
    phase_trace: [],
    state_trace: [],
    adapter: {
      invalid_action_count: 0,
      executor_failed_count: 0,
      stale_state_count: 0,
      timeout_count: 0
    },
    combat: {
      first_combat_seen: false,
      first_combat_finished: false,
      first_combat_won: null,
      hp_lost: null,
      turn_count: null
    },
    metrics: null,
    computed_metrics: null,
    latest_action: null,
    final_state: null,
    notes: []
  };
}

function countAgentStatuses(runDirectory) {
  const filePath = path.join(runDirectory, "agent_events.jsonl");
  const counts = {
    invalid_action_count: 0,
    executor_failed_count: 0,
    stale_state_count: 0,
    timeout_count: 0
  };

  if (!fs.existsSync(filePath)) {
    return counts;
  }

  const lines = fs.readFileSync(filePath, "utf8").split(/\r?\n/).filter((line) => line.trim() !== "");
  for (const line of lines) {
    let record = null;
    try {
      record = JSON.parse(line);
    } catch {
      continue;
    }

    const status = typeof record.status === "string" ? record.status : "";
    const error = typeof record.error === "string" ? record.error : "";
    if (status === "invalid" || status === "invalid_action") {
      counts.invalid_action_count += 1;
    }

    if (status === "failed" || status === "submit_failed" || error.includes("실패")) {
      counts.executor_failed_count += 1;
    }

    if (status === "stale" || status === "stale_decision" || status === "stale_retry_queued") {
      counts.stale_state_count += 1;
    }

    if (status === "result_timeout" || status === "timeout") {
      counts.timeout_count += 1;
    }
  }

  return counts;
}

function readOptionalJson(filePath) {
  if (!fs.existsSync(filePath)) {
    return null;
  }

  try {
    return readJsonFile(filePath);
  } catch {
    return null;
  }
}

function readJsonLines(filePath) {
  if (!fs.existsSync(filePath)) {
    return [];
  }

  const records = [];
  const lines = fs.readFileSync(filePath, "utf8").split(/\r?\n/).filter((line) => line.trim() !== "");
  for (const line of lines) {
    try {
      const parsed = JSON.parse(line);
      if (isPlainObject(parsed)) {
        records.push(parsed);
      }
    } catch {
      records.push({
        type: "parse_error",
        status: "invalid_json_line",
        raw: line
      });
    }
  }

  return records;
}

function readNumber(value, fallbackValue = null) {
  const parsed = Number(value);
  return Number.isFinite(parsed) ? parsed : fallbackValue;
}

function incrementCounter(target, key, amount = 1) {
  target[key] = readNumber(target[key], 0) + amount;
}

function createAgentEventSummary() {
  return {
    event_count: 0,
    decision_count: 0,
    llm_decision_count: 0,
    auto_trivial_decision_count: 0,
    dry_run_decision_count: 0,
    completed_count: 0,
    failed_count: 0,
    invalid_action_count: 0,
    executor_failed_count: 0,
    stale_state_count: 0,
    timeout_count: 0,
    result_timeout_count: 0,
    submit_failure_count: 0,
    terminal_stop_count: 0,
    selected_action_ids: [],
    status_counts: {},
    phase_counts: {},
    total_decision_ms: 0,
    average_decision_ms: null,
    max_decision_ms: 0
  };
}

function summarizeAgentEvents(runDirectory) {
  const records = readJsonLines(path.join(runDirectory, "agent_events.jsonl"));
  const summary = createAgentEventSummary();
  summary.event_count = records.length;

  for (const record of records) {
    const status = typeof record.status === "string" ? record.status : "";
    const type = typeof record.type === "string" ? record.type : "";
    const phase = typeof record.phase === "string" ? record.phase : "";
    const decisionSource = typeof record.decision_source === "string" ? record.decision_source : "";
    const selectedActionId = typeof record.selected_action_id === "string" ? record.selected_action_id : "";
    const error = typeof record.error === "string" ? record.error : "";
    const decisionMs = readNumber(record.decision_ms, null);

    if (status !== "") {
      incrementCounter(summary.status_counts, status);
    }

    if (phase !== "") {
      incrementCounter(summary.phase_counts, phase);
    }

    if (type === "decision") {
      summary.decision_count += 1;
    }

    if (decisionSource === "llm" || decisionSource === "llm_app_server") {
      summary.llm_decision_count += 1;
    }

    if (decisionSource === "auto_trivial" || decisionSource === "auto_terminal_repeat") {
      summary.auto_trivial_decision_count += 1;
    }

    if (status === "dry_run") {
      summary.dry_run_decision_count += 1;
    }

    if (status === "completed" || status === "applied") {
      summary.completed_count += 1;
    }

    if (status === "failed") {
      summary.failed_count += 1;
    }

    if (status === "invalid" || status === "invalid_action") {
      summary.invalid_action_count += 1;
    }

    if (status === "failed" || status === "submit_failed" || error.includes("실패")) {
      summary.executor_failed_count += 1;
    }

    if (status === "stale" || status === "stale_decision" || status === "stale_retry_queued") {
      summary.stale_state_count += 1;
    }

    if (status === "timeout" || status === "result_timeout") {
      summary.timeout_count += 1;
    }

    if (status === "result_timeout") {
      summary.result_timeout_count += 1;
    }

    if (status === "submit_failed") {
      summary.submit_failure_count += 1;
    }

    if (type === "terminal_stop") {
      summary.terminal_stop_count += 1;
    }

    if (selectedActionId !== "") {
      summary.selected_action_ids.push(selectedActionId);
    }

    if (decisionMs !== null) {
      summary.total_decision_ms += decisionMs;
      summary.max_decision_ms = Math.max(summary.max_decision_ms, decisionMs);
    }
  }

  if (summary.decision_count > 0) {
    summary.average_decision_ms = Math.round(summary.total_decision_ms / summary.decision_count);
  }

  return summary;
}

function computeBenchmarkMetrics(runDirectory, stopState, metrics) {
  const eventSummary = summarizeAgentEvents(runDirectory);
  const metricObject = isPlainObject(metrics) ? metrics : {};
  const decisionCount = readNumber(metricObject.decisions, null) ?? eventSummary.decision_count;
  const resultTimeouts = readNumber(metricObject.result_timeouts, null) ?? eventSummary.result_timeout_count;
  const submitFailures = readNumber(metricObject.submit_failures, null) ?? eventSummary.submit_failure_count;
  const invalidActions = readNumber(metricObject.invalid_actions, null) ?? eventSummary.invalid_action_count;
  const staleRetries = readNumber(metricObject.stale_retries, null) ?? eventSummary.stale_state_count;
  const actionsApplied = readNumber(metricObject.actions_applied, null) ?? eventSummary.completed_count;
  const executorFailedCount = Math.max(eventSummary.executor_failed_count, submitFailures);
  const timeoutCount = Math.max(eventSummary.timeout_count, resultTimeouts);

  return {
    decision_count: decisionCount,
    action_applied_count: actionsApplied,
    invalid_action_count: invalidActions,
    executor_failed_count: executorFailedCount,
    stale_state_count: staleRetries,
    timeout_count: timeoutCount,
    result_timeout_count: resultTimeouts,
    submit_failure_count: submitFailures,
    first_combat_seen: stopState.firstCombatSeen,
    first_combat_finished: stopState.firstCombatFinished,
    final_phase: stopState.lastPhase || null,
    phase_transition_count: stopState.phaseTrace.length,
    selected_action_count: eventSummary.selected_action_ids.length,
    average_decision_ms: readNumber(metricObject.average_decision_ms, null) ?? eventSummary.average_decision_ms,
    max_decision_ms: readNumber(metricObject.max_decision_ms, null) ?? eventSummary.max_decision_ms,
    agent_event_summary: eventSummary
  };
}

function buildDaemonArgs(config, options, runDirectory) {
  const scenario = config.scenario;
  const decider = config.decider;
  const args = [
    path.join("scripts", "spiremind_agent_daemon.js"),
    "--bridge-url", options.bridgeUrl,
    "--decision-backend", decider.decision_backend || "command",
    "--run-log-dir", runDirectory,
    "--scenario-id", scenario.benchmark_id || "benchmark",
    "--play-session-id", `${scenario.benchmark_id || "benchmark"}_${options.seedId}_${options.deciderId}_${String(options.repeatIndex).padStart(3, "0")}`,
    "--max-decisions", String(readPositiveInteger(scenario.max_decisions, 30)),
    "--timeout-ms", String(readPositiveInteger(scenario.timeout_ms, 120000)),
    "--result-timeout-ms", String(readPositiveInteger(scenario.result_timeout_ms, 60000)),
    "--poll-ms", String(readPositiveInteger(scenario.poll_ms, DEFAULT_POLL_MS))
  ];

  if (decider.model) {
    args.push("--model", String(decider.model));
  }

  if (decider.effort) {
    args.push("--effort", String(decider.effort));
  }

  if (decider.codex_command) {
    args.push("--codex-command", String(decider.codex_command));
  }

  if (decider.command) {
    args.push("--command", String(decider.command));
  }

  if (Array.isArray(decider.command_args)) {
    for (const arg of decider.command_args) {
      args.push("--command-arg", String(arg));
    }
  }

  if (scenario.auto_trivial === true) {
    args.push("--auto-trivial");
  }

  if (scenario.dismiss_terminal === true) {
    args.push("--dismiss-terminal");
  }

  return args;
}

function spawnDaemon(config, options, runDirectory) {
  const stdoutPath = path.join(runDirectory, "agent_daemon_stdout.txt");
  const stderrPath = path.join(runDirectory, "agent_daemon_stderr.txt");
  const args = buildDaemonArgs(config, options, runDirectory);
  const child = spawn(process.execPath, args, {
    cwd: path.resolve(__dirname, ".."),
    stdio: ["ignore", "pipe", "pipe"],
    windowsHide: true
  });

  child.stdout.setEncoding("utf8");
  child.stderr.setEncoding("utf8");
  child.stdout.on("data", (chunk) => appendText(stdoutPath, chunk));
  child.stderr.on("data", (chunk) => appendText(stderrPath, chunk));

  return {
    child,
    args,
    stdoutPath,
    stderrPath,
    exit: new Promise((resolve) => {
      child.on("close", (code, signal) => resolve({ code, signal }));
    })
  };
}

async function stopDaemon(daemon, shutdownGraceMs) {
  if (!daemon || !daemon.child || daemon.child.killed || daemon.child.exitCode !== null) {
    return null;
  }

  daemon.child.kill("SIGTERM");
  const timeout = new Promise((resolve) => setTimeout(() => resolve({ timeout: true }), shutdownGraceMs));
  const result = await Promise.race([daemon.exit, timeout]);
  if (result && result.timeout) {
    daemon.child.kill("SIGKILL");
    return await daemon.exit;
  }

  return result;
}

async function monitorBridge(config, options, runDirectory, stopState, getDaemonExit) {
  const scenario = config.scenario;
  const pollMs = readPositiveInteger(scenario.poll_ms, DEFAULT_POLL_MS);
  const timeoutMs = readPositiveInteger(scenario.timeout_ms, 120000);
  const maxRuntimeMs = options.maxRuntimeMs ?? readPositiveInteger(scenario.max_runtime_ms, DEFAULT_MAX_RUNTIME_MS);
  const startedAt = Date.now();
  let finalSnapshot = null;

  while (Date.now() - startedAt <= maxRuntimeMs) {
    updateStopStateFromAgentEvents(stopState, runDirectory);
    if (stopState.stopReason) {
      return {
        status: "completed",
        stopReason: stopState.stopReason,
        finalSnapshot
      };
    }

    const snapshot = await getBridgeJson(options.bridgeUrl, "/state/current", timeoutMs);
    finalSnapshot = snapshot;
    updateStopState(stopState, snapshot);
    if (stopState.stopReason) {
      return {
        status: "completed",
        stopReason: stopState.stopReason,
        finalSnapshot
      };
    }

    const daemonExit = typeof getDaemonExit === "function" ? getDaemonExit() : null;
    if (daemonExit) {
      return {
        status: stopState.stopRule === "first_combat_finished" || daemonExit.code !== 0 ? "failed" : "completed",
        stopReason: stopState.stopRule === "first_combat_finished" || daemonExit.code !== 0
          ? "daemon_exited_before_stop_rule"
          : "max_decisions_reached",
        finalSnapshot,
        daemonExit
      };
    }

    await sleep(pollMs);
  }

  return {
    status: "failed",
    stopReason: "max_runtime_exceeded",
    finalSnapshot
  };
}

async function readFinalBridgeSnapshots(options, timeoutMs) {
  const result = {
    latest_action: null,
    final_state: null
  };

  try {
    result.latest_action = await getBridgeJson(options.bridgeUrl, "/action/latest", timeoutMs);
  } catch (error) {
    result.latest_action = { ok: false, error: error.message };
  }

  try {
    result.final_state = await getBridgeJson(options.bridgeUrl, "/state/current", timeoutMs);
  } catch (error) {
    result.final_state = { ok: false, error: error.message };
  }

  return result;
}

function finalizeSummary(summary, runDirectory, stopState, monitorResult, finalBridge) {
  summary.finished_at = nowIsoString();
  summary.status = monitorResult.status;
  summary.stop_reason = monitorResult.stopReason;
  summary.phase_trace = stopState.phaseTrace;
  summary.state_trace = stopState.stateTrace;
  summary.combat.first_combat_seen = stopState.firstCombatSeen;
  summary.combat.first_combat_finished = stopState.firstCombatFinished;
  summary.combat.first_combat_won = stopState.stopReason === "first_combat_finished"
    ? true
    : stopState.stopReason === "first_combat_game_over"
    ? false
    : null;
  summary.metrics = readOptionalJson(path.join(runDirectory, "metrics.json"));
  summary.computed_metrics = computeBenchmarkMetrics(runDirectory, stopState, summary.metrics);
  summary.adapter = {
    invalid_action_count: summary.computed_metrics.invalid_action_count,
    executor_failed_count: summary.computed_metrics.executor_failed_count,
    stale_state_count: summary.computed_metrics.stale_state_count,
    timeout_count: summary.computed_metrics.timeout_count
  };
  summary.latest_action = finalBridge.latest_action;
  summary.final_state = finalBridge.final_state;

  if (summary.combat.hp_lost === null) {
    summary.notes.push("hp_lost는 아직 자동 계산하지 않습니다. 상태 스냅샷 기반 계산은 후속 단계에서 추가합니다.");
  }

  if (summary.combat.turn_count === null) {
    summary.notes.push("turn_count는 아직 자동 계산하지 않습니다. combat_log 또는 상태 스냅샷 기반 계산이 필요합니다.");
  }

  return summary;
}

async function runBenchmark(options) {
  const config = loadBenchmarkConfig(options);
  const runDirectory = makeRunDirectory(config, options);
  const stopState = createStopState({
    ...options,
    stopRule: config.scenario.stop_rule
  });
  const summary = createInitialSummary(config, options, runDirectory, "running", null);
  const summaryPath = path.join(runDirectory, "benchmark_run_summary.json");
  writeJsonFile(summaryPath, summary);

  if (options.dryRun) {
    summary.status = "dry_run";
    summary.stop_reason = "dry_run";
    summary.finished_at = nowIsoString();
    summary.notes.push("dry-run 모드라 브리지 감시와 데몬 실행을 건너뛰었습니다.");
    writeJsonFile(summaryPath, summary);
    return summary;
  }

  let daemon = null;
  let daemonExitResult = null;
  if (!options.noDaemon) {
    daemon = spawnDaemon(config, options, runDirectory);
    daemon.exit.then((exitResult) => {
      daemonExitResult = exitResult;
    });
    writeJsonFile(path.join(runDirectory, "agent_daemon_invocation.json"), {
      command: process.execPath,
      args: daemon.args,
      stdout_path: daemon.stdoutPath,
      stderr_path: daemon.stderrPath,
      started_at: nowIsoString()
    });
  }

  let monitorResult = null;
  try {
    monitorResult = await monitorBridge(config, options, runDirectory, stopState, () => daemonExitResult);
  } catch (error) {
    monitorResult = {
      status: "failed",
      stopReason: "bridge_monitor_failed",
      finalSnapshot: null,
      error: error.message
    };
    summary.notes.push(error.message);
  } finally {
    if (daemon) {
      const shutdownGraceMs = readPositiveInteger(config.scenario.shutdown_grace_ms, DEFAULT_SHUTDOWN_GRACE_MS);
      const daemonExit = daemonExitResult || await stopDaemon(daemon, shutdownGraceMs);
      writeJsonFile(path.join(runDirectory, "agent_daemon_exit.json"), {
        ...daemonExit,
        stopped_at: nowIsoString()
      });
    }
  }

  const finalBridge = await readFinalBridgeSnapshots(
    options,
    readPositiveInteger(config.scenario.timeout_ms, 120000));
  const finalSummary = finalizeSummary(summary, runDirectory, stopState, monitorResult, finalBridge);
  if (monitorResult.error) {
    finalSummary.error = monitorResult.error;
  }

  writeJsonFile(summaryPath, finalSummary);
  return finalSummary;
}

function runSelfTest() {
  const stopState = createStopState();
  const snapshots = [
    {
      state_id: "neow_1",
      state: {
        phase: "neow",
        legal_actions: [{ action_id: "choose_neow_reward_0", type: "choose_neow_reward" }]
      }
    },
    {
      state_id: "map_1",
      state: {
        phase: "map",
        legal_actions: [{ action_id: "map_node_0", type: "choose_map_node" }]
      }
    },
    {
      state_id: "combat_1",
      state: {
        phase: "combat",
        legal_actions: [{ action_id: "end_turn", type: "end_turn" }]
      }
    },
    {
      state_id: "reward_1",
      state: {
        phase: "reward",
        legal_actions: [{ action_id: "proceed_rewards", type: "proceed_rewards" }]
      }
    }
  ];

  for (const snapshot of snapshots) {
    updateStopState(stopState, snapshot);
  }

  if (!stopState.neowSeen || !stopState.mapSeenAfterNeow || !stopState.firstCombatSeen || !stopState.firstCombatFinished) {
    throw new Error(`B0 종료 조건 판정 실패: ${JSON.stringify(stopState)}`);
  }

  if (stopState.stopReason !== "first_combat_finished") {
    throw new Error(`예상하지 않은 stopReason입니다: ${stopState.stopReason}`);
  }

  const tempDirectory = fs.mkdtempSync(path.join(os.tmpdir(), "spiremind-benchmark-self-test-"));
  try {
    const eventRecords = [
      {
        type: "decision",
        status: "completed",
        decision_source: "llm_app_server",
        phase: "neow",
        selected_action_id: "choose_neow_reward_0",
        decision_ms: 1000
      },
      {
        type: "decision",
        status: "stale_decision",
        decision_source: "llm_app_server",
        phase: "combat",
        selected_action_id: "play_card_1",
        decision_ms: 2000
      },
      {
        type: "decision",
        status: "result_timeout",
        decision_source: "llm_app_server",
        phase: "combat",
        selected_action_id: "end_turn",
        decision_ms: 3000
      }
    ];
    fs.writeFileSync(
      path.join(tempDirectory, "agent_events.jsonl"),
      `${eventRecords.map((record) => JSON.stringify(record)).join("\n")}\n`,
      "utf8");
    writeJsonFile(path.join(tempDirectory, "metrics.json"), {
      decisions: 3,
      actions_applied: 1,
      invalid_actions: 0,
      stale_retries: 1,
      result_timeouts: 1,
      submit_failures: 0,
      average_decision_ms: 2000
    });

    const computedMetrics = computeBenchmarkMetrics(
      tempDirectory,
      stopState,
      readJsonFile(path.join(tempDirectory, "metrics.json")));
    if (computedMetrics.decision_count !== 3
      || computedMetrics.stale_state_count !== 1
      || computedMetrics.timeout_count < 1
      || computedMetrics.final_phase !== "reward"
      || computedMetrics.agent_event_summary.llm_decision_count !== 3) {
      throw new Error(`요약 지표 계산 실패: ${JSON.stringify(computedMetrics)}`);
    }

    const eventStopState = createStopState();
    const stopEventRecords = [
      {
        type: "decision",
        phase: "event",
        latest_action: {
          pre_action_state_summary: { phase: "event", state_id: "event_1" },
          post_result_state_summary: { phase: "map", state_id: "map_1" }
        }
      },
      {
        type: "decision",
        phase: "map",
        latest_action: {
          pre_action_state_summary: { phase: "map", state_id: "map_1" },
          post_result_state_summary: { phase: "combat_turn", state_id: "combat_1" }
        }
      },
      {
        type: "decision",
        phase: "combat_turn",
        latest_action: {
          pre_action_state_summary: { phase: "combat_turn", state_id: "combat_1" },
          post_result_state_summary: { phase: "reward", state_id: "reward_1" }
        }
      }
    ];
    const stopEventPath = path.join(tempDirectory, "agent_events.jsonl");
    fs.writeFileSync(
      stopEventPath,
      `${stopEventRecords.slice(0, 2).map((record) => JSON.stringify(record)).join("\n")}\n`,
      "utf8");
    updateStopState(eventStopState, {
      state_id: "event_1",
      state: {
        phase: "event",
        legal_actions: [{ action_id: "choose_event_option_0", type: "choose_event_option", text_key: "NEOW.pages.INITIAL.options.TEST" }]
      }
    });
    updateStopStateFromAgentEvents(eventStopState, tempDirectory);
    updateStopState(eventStopState, makeSyntheticSnapshot("combat_turn", "combat_1"));
    updateStopState(eventStopState, makeSyntheticSnapshot("map", "map_stale_after_combat"));
    if (eventStopState.stopReason) {
      throw new Error(`agent_events combat entry false positive: ${JSON.stringify(eventStopState)}`);
    }

    fs.appendFileSync(stopEventPath, `${JSON.stringify(stopEventRecords[2])}\n`, "utf8");
    updateStopStateFromAgentEvents(eventStopState, tempDirectory);
    if (eventStopState.stopReason !== "first_combat_finished") {
      throw new Error(`agent_events 종료 조건 판정 실패: ${JSON.stringify(eventStopState)}`);
    }
  } finally {
    fs.rmSync(tempDirectory, { recursive: true, force: true });
  }

  process.stdout.write(`${JSON.stringify({
    status: "self_test_passed",
    phase_trace: stopState.phaseTrace,
    stop_reason: stopState.stopReason,
    metrics_summary: "passed"
  }, null, 2)}\n`);
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

  const summary = await runBenchmark(options);
  process.stdout.write(`${JSON.stringify({
    status: summary.status,
    stop_reason: summary.stop_reason,
    run_directory: summary.run_directory,
    phase_trace: summary.phase_trace
  }, null, 2)}\n`);
}

main().catch((error) => {
  process.stderr.write(`${error instanceof Error ? error.stack || error.message : String(error)}\n`);
  process.exitCode = 1;
});
