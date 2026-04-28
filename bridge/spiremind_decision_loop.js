#!/usr/bin/env node
"use strict";

const { spawn } = require("child_process");
const fs = require("fs");
const http = require("http");
const https = require("https");
const path = require("path");

const DEFAULT_BRIDGE_URL = "http://127.0.0.1:17832";
const DEFAULT_POLL_MS = 1000;
const DEFAULT_TIMEOUT_MS = 30000;

function parseArgs(argv) {
  const options = {
    bridgeUrl: process.env.SPIREMIND_BRIDGE_URL || DEFAULT_BRIDGE_URL,
    mode: process.env.SPIREMIND_DECIDER_MODE || "heuristic",
    command: process.env.SPIREMIND_DECIDER_COMMAND || "",
    commandArgs: [],
    once: false,
    untilCombatEnd: false,
    dryRun: false,
    waitResult: false,
    maxDecisions: 1,
    maxDecisionsWasSet: false,
    maxActionsPerTurn: 3,
    recentHistoryLimit: parsePositiveInt(process.env.SPIREMIND_RECENT_HISTORY_LIMIT, 8),
    pollMs: DEFAULT_POLL_MS,
    timeoutMs: DEFAULT_TIMEOUT_MS,
    resultTimeoutMs: 60000,
    runLogDir: process.env.SPIREMIND_RUN_LOG_DIR || "",
    scenarioId: process.env.SPIREMIND_SCENARIO_ID || "",
    playSessionId: process.env.SPIREMIND_PLAY_SESSION_ID || "",
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

    if (token === "--mode" && index + 1 < argv.length) {
      options.mode = argv[index + 1];
      index += 1;
      continue;
    }

    if (token.startsWith("--mode=")) {
      options.mode = token.slice("--mode=".length);
      continue;
    }

    if (token === "--command" && index + 1 < argv.length) {
      options.command = argv[index + 1];
      index += 1;
      continue;
    }

    if (token === "--command-arg" && index + 1 < argv.length) {
      options.commandArgs.push(argv[index + 1]);
      index += 1;
      continue;
    }

    if (token === "--once") {
      options.once = true;
      options.maxDecisions = 1;
      options.maxDecisionsWasSet = true;
      continue;
    }

    if (token === "--until-combat-end") {
      options.untilCombatEnd = true;
      if (!options.maxDecisionsWasSet) {
        options.maxDecisions = 50;
      }
      continue;
    }

    if (token === "--dry-run") {
      options.dryRun = true;
      continue;
    }

    if (token === "--wait-result") {
      options.waitResult = true;
      continue;
    }

    if (token === "--run-log-dir" && index + 1 < argv.length) {
      options.runLogDir = argv[index + 1];
      index += 1;
      continue;
    }

    if (token.startsWith("--run-log-dir=")) {
      options.runLogDir = token.slice("--run-log-dir=".length);
      continue;
    }

    if (token === "--scenario-id" && index + 1 < argv.length) {
      options.scenarioId = argv[index + 1];
      index += 1;
      continue;
    }

    if (token.startsWith("--scenario-id=")) {
      options.scenarioId = token.slice("--scenario-id=".length);
      continue;
    }

    if (token === "--play-session-id" && index + 1 < argv.length) {
      options.playSessionId = argv[index + 1];
      index += 1;
      continue;
    }

    if (token.startsWith("--play-session-id=")) {
      options.playSessionId = token.slice("--play-session-id=".length);
      continue;
    }

    if (token === "--max-decisions" && index + 1 < argv.length) {
      options.maxDecisions = parsePositiveInt(argv[index + 1], options.maxDecisions);
      options.maxDecisionsWasSet = true;
      index += 1;
      continue;
    }

    if (token === "--max-actions-per-turn" && index + 1 < argv.length) {
      options.maxActionsPerTurn = parsePositiveInt(argv[index + 1], options.maxActionsPerTurn);
      index += 1;
      continue;
    }

    if (token === "--recent-history-limit" && index + 1 < argv.length) {
      options.recentHistoryLimit = parsePositiveInt(argv[index + 1], options.recentHistoryLimit);
      index += 1;
      continue;
    }

    if (token === "--poll-ms" && index + 1 < argv.length) {
      options.pollMs = parsePositiveInt(argv[index + 1], options.pollMs);
      index += 1;
      continue;
    }

    if (token === "--timeout-ms" && index + 1 < argv.length) {
      options.timeoutMs = parsePositiveInt(argv[index + 1], options.timeoutMs);
      index += 1;
      continue;
    }

    if (token === "--result-timeout-ms" && index + 1 < argv.length) {
      options.resultTimeoutMs = parsePositiveInt(argv[index + 1], options.resultTimeoutMs);
      index += 1;
    }
  }

  return options;
}

function parsePositiveInt(value, fallbackValue) {
  const parsed = Number.parseInt(String(value), 10);
  return Number.isFinite(parsed) && parsed > 0 ? parsed : fallbackValue;
}

function showHelp() {
  process.stdout.write([
    "SpireMind decision loop",
    "",
    "Usage:",
    "  node bridge/spiremind_decision_loop.js --mode heuristic --once",
    "  node bridge/spiremind_decision_loop.js --mode command --command <program> --command-arg <arg>",
    "  node bridge/spiremind_decision_loop.js --mode heuristic --once --wait-result --run-log-dir <dir>",
    "",
    "Modes:",
    "  heuristic  현재 상태에서 사용 가능한 공격 카드 묶음과 end_turn을 제출한다.",
    "  command    외부 명령에 JSON 프롬프트를 stdin으로 보내고 JSON 결정을 stdout에서 읽는다.",
    "",
    "Run record options:",
    "  --run-log-dir <dir>   decisions.jsonl, metrics.json, decider_config.json을 기록한다.",
    "  --wait-result         제출한 계획이 completed 또는 failed가 될 때까지 기다린다.",
    "  --until-combat-end    전투 턴을 반복 판단하고, 전투 종료나 비전투 상태를 감지하면 멈춘다.",
    "  --recent-history-limit <n> command 모드 판단기에 전달할 최근 기록 수. 기본값 8.",
    "  --scenario-id <id>    내부 기록용 시나리오 식별자다. 외부 판단기에는 보내지 않는다.",
    "  --play-session-id <id> 외부 판단기에 보내도 되는 플레이 세션 식별자다.",
    "",
    "Command mode output contract:",
    "  { \"actions\": [{ \"type\": \"play_card\", \"combat_card_id\": 0, \"target_combat_id\": 1 }], \"reason\": \"...\" }",
    "  또는 { \"selected_action_id\": \"end_turn\", \"reason\": \"...\" }",
    ""
  ].join("\n"));
}

function sleep(ms) {
  return new Promise((resolve) => setTimeout(resolve, ms));
}

function appendPath(baseUrlString, pathname) {
  return new URL(pathname, baseUrlString).toString();
}

function safeJsonParse(text) {
  try {
    return JSON.parse(text);
  } catch {
    return null;
  }
}

function isPlainObject(value) {
  return typeof value === "object" && value !== null && !Array.isArray(value);
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
    request.on("error", (error) => reject(error));

    if (body !== undefined) {
      request.write(bodyText);
    }

    request.end();
  });
}

async function getCurrentState(bridgeUrl, timeoutMs) {
  const response = await httpRequestJson("GET", appendPath(bridgeUrl, "/state/current"), undefined, timeoutMs);
  if (response.statusCode !== 200 || !isPlainObject(response.body)) {
    throw new Error(`브리지 상태 조회 실패: HTTP ${response.statusCode}`);
  }

  return response.body;
}

async function getLatestAction(bridgeUrl, timeoutMs) {
  const response = await httpRequestJson("GET", appendPath(bridgeUrl, "/action/latest"), undefined, timeoutMs);
  if (response.statusCode !== 200 || !isPlainObject(response.body)) {
    throw new Error(`브리지 행동 조회 실패: HTTP ${response.statusCode}`);
  }

  return response.body;
}

async function submitDecision(bridgeUrl, decision, stateVersion, source, timeoutMs) {
  const body = {
    ...decision,
    source,
    expected_state_version: stateVersion
  };
  const response = await httpRequestJson("POST", appendPath(bridgeUrl, "/action/submit"), body, timeoutMs);
  if (response.statusCode < 200 || response.statusCode >= 300 || !isPlainObject(response.body)) {
    throw new Error(`행동 제출 실패: HTTP ${response.statusCode}`);
  }

  return response.body;
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

function readJsonLines(filePath) {
  if (!fs.existsSync(filePath)) {
    return [];
  }

  return fs.readFileSync(filePath, "utf8")
    .split(/\r?\n/)
    .map((line) => line.trim())
    .filter((line) => line !== "")
    .map((line) => safeJsonParse(line))
    .filter(isPlainObject);
}

function nowIsoString() {
  return new Date().toISOString();
}

function createDecisionId() {
  return `decision_${Date.now().toString(36)}_${Math.random().toString(36).slice(2, 10)}`;
}

function createRecordId(prefix) {
  return `${prefix}_${Date.now().toString(36)}_${Math.random().toString(36).slice(2, 8)}`;
}

function summarizeState(snapshot) {
  const state = isPlainObject(snapshot.state) ? snapshot.state : {};
  const player = isPlainObject(state.player) ? state.player : {};
  const piles = isPlainObject(state.piles) ? state.piles : {};
  const hand = Array.isArray(piles.hand) ? piles.hand.filter(isPlainObject) : [];
  const enemies = Array.isArray(state.enemies) ? state.enemies.filter(isPlainObject) : [];
  const legalActions = Array.isArray(state.legal_actions) ? state.legal_actions.filter(isPlainObject) : [];

  return {
    state_version: readNumber(snapshot.state_version),
    state_id: typeof snapshot.state_id === "string" ? snapshot.state_id : state.state_id || null,
    phase: typeof state.phase === "string" ? state.phase : null,
    player: {
      hp: readNumber(player.hp),
      max_hp: readNumber(player.max_hp),
      block: readNumber(player.block),
      energy: readNumber(player.energy)
    },
    hand: hand.map((card) => ({
      combat_card_id: readNumber(card.combat_card_id),
      name: typeof card.name === "string" ? card.name : null,
      cost: readNumber(card.cost),
      playable: card.playable === true,
      type: typeof card.type === "string" ? card.type : null
    })),
    enemies: enemies.map((enemy) => ({
      combat_id: readNumber(enemy.combat_id),
      name: typeof enemy.name === "string" ? enemy.name : null,
      hp: readNumber(enemy.hp),
      max_hp: readNumber(enemy.max_hp),
      block: readNumber(enemy.block),
      intent: isPlainObject(enemy.intent)
        ? {
            type: typeof enemy.intent.type === "string" ? enemy.intent.type : null,
            total_damage: readNumber(enemy.intent.total_damage)
          }
        : null
    })),
    legal_action_count: legalActions.length,
    legal_action_ids: legalActions
      .map((action) => (typeof action.action_id === "string" ? action.action_id : null))
      .filter((actionId) => actionId !== null)
  };
}

function countLiveEnemies(stateSummary) {
  if (!isPlainObject(stateSummary) || !Array.isArray(stateSummary.enemies)) {
    return 0;
  }

  return stateSummary.enemies.filter((enemy) => {
    const hp = readNumber(enemy.hp);
    const combatId = readNumber(enemy.combat_id);
    return hp !== null ? hp > 0 : combatId !== null;
  }).length;
}

function sumKnownEnemyHp(enemies) {
  if (!Array.isArray(enemies)) {
    return null;
  }

  let total = 0;
  let foundKnownHp = false;
  for (const enemy of enemies) {
    if (!isPlainObject(enemy)) {
      continue;
    }

    const hp = readNumber(enemy.hp);
    if (hp !== null) {
      total += Math.max(0, hp);
      foundKnownHp = true;
    }
  }

  return foundKnownHp ? total : null;
}

function createEnemyStateKey(enemy, index) {
  if (!isPlainObject(enemy)) {
    return `index:${index}`;
  }

  const combatId = readNumber(enemy.combat_id);
  if (combatId !== null) {
    return `combat:${combatId}`;
  }

  const name = typeof enemy.name === "string" && enemy.name.trim() !== ""
    ? enemy.name.trim()
    : "unknown";
  return `name:${name}:${index}`;
}

function summarizeEnemyHpChanges(beforeEnemies, afterEnemies) {
  const afterByKey = new Map();
  const normalizedAfterEnemies = Array.isArray(afterEnemies) ? afterEnemies : [];
  normalizedAfterEnemies.forEach((enemy, index) => {
    afterByKey.set(createEnemyStateKey(enemy, index), enemy);
  });

  const changes = [];
  const normalizedBeforeEnemies = Array.isArray(beforeEnemies) ? beforeEnemies : [];
  normalizedBeforeEnemies.forEach((beforeEnemy, index) => {
    if (!isPlainObject(beforeEnemy)) {
      return;
    }

    const beforeCombatId = readNumber(beforeEnemy.combat_id);
    const afterEnemy = afterByKey.get(createEnemyStateKey(beforeEnemy, index)) || null;
    const afterCombatId = afterEnemy ? readNumber(afterEnemy.combat_id) : null;
    const beforeHp = readNumber(beforeEnemy.hp);
    const rawAfterHp = afterEnemy ? readNumber(afterEnemy.hp) : null;
    if (beforeCombatId === null && afterCombatId === null && beforeHp === null && rawAfterHp === null) {
      return;
    }

    const afterHp = afterEnemy ? rawAfterHp : (beforeHp !== null ? 0 : null);
    const hpLost = beforeHp !== null && afterHp !== null
      ? Math.max(0, beforeHp - afterHp)
      : null;

    changes.push({
      combat_id: beforeCombatId,
      name: typeof beforeEnemy.name === "string" ? beforeEnemy.name : null,
      hp_before: beforeHp,
      hp_after: afterHp,
      hp_lost: hpLost,
      defeated: beforeHp !== null && beforeHp > 0 && afterHp !== null && afterHp <= 0
    });
  });

  return changes;
}

function buildStateDelta(beforeSummary, afterSummary) {
  if (!isPlainObject(beforeSummary) || !isPlainObject(afterSummary)) {
    return null;
  }

  const playerHpBefore = readNumber(beforeSummary.player && beforeSummary.player.hp);
  const playerHpAfter = readNumber(afterSummary.player && afterSummary.player.hp);
  const playerBlockBefore = readNumber(beforeSummary.player && beforeSummary.player.block);
  const playerBlockAfter = readNumber(afterSummary.player && afterSummary.player.block);
  const playerEnergyBefore = readNumber(beforeSummary.player && beforeSummary.player.energy);
  const playerEnergyAfter = readNumber(afterSummary.player && afterSummary.player.energy);
  const totalEnemyHpBefore = sumKnownEnemyHp(beforeSummary.enemies);
  const totalEnemyHpAfter = sumKnownEnemyHp(afterSummary.enemies);
  const enemyHpChanges = summarizeEnemyHpChanges(beforeSummary.enemies, afterSummary.enemies);

  return {
    state_version_before: beforeSummary.state_version,
    state_version_after: afterSummary.state_version,
    state_id_before: beforeSummary.state_id,
    state_id_after: afterSummary.state_id,
    phase_before: beforeSummary.phase,
    phase_after: afterSummary.phase,
    player: {
      hp_before: playerHpBefore,
      hp_after: playerHpAfter,
      hp_lost: playerHpBefore !== null && playerHpAfter !== null
        ? Math.max(0, playerHpBefore - playerHpAfter)
        : null,
      block_before: playerBlockBefore,
      block_after: playerBlockAfter,
      block_delta: playerBlockBefore !== null && playerBlockAfter !== null
        ? playerBlockAfter - playerBlockBefore
        : null,
      energy_before: playerEnergyBefore,
      energy_after: playerEnergyAfter,
      energy_spent: playerEnergyBefore !== null && playerEnergyAfter !== null
        ? Math.max(0, playerEnergyBefore - playerEnergyAfter)
        : null
    },
    hand_count_before: Array.isArray(beforeSummary.hand) ? beforeSummary.hand.length : null,
    hand_count_after: Array.isArray(afterSummary.hand) ? afterSummary.hand.length : null,
    live_enemy_count_before: countLiveEnemies(beforeSummary),
    live_enemy_count_after: countLiveEnemies(afterSummary),
    total_enemy_hp_before: totalEnemyHpBefore,
    total_enemy_hp_after: totalEnemyHpAfter,
    enemy_hp_lost: totalEnemyHpBefore !== null && totalEnemyHpAfter !== null
      ? Math.max(0, totalEnemyHpBefore - totalEnemyHpAfter)
      : null,
    enemies: enemyHpChanges
  };
}

function classifyCombatReadiness(snapshot) {
  const stateSummary = summarizeState(snapshot);
  const phase = typeof stateSummary.phase === "string" ? stateSummary.phase.trim() : "";
  const playerHp = readNumber(stateSummary.player.hp);
  const liveEnemyCount = countLiveEnemies(stateSummary);
  const hasLegalActions = stateSummary.legal_action_count > 0;

  if (!isPlainObject(snapshot.state)) {
    return {
      stateSummary,
      readyForDecision: false,
      shouldStop: false,
      reason: "state_missing"
    };
  }

  if (playerHp !== null && playerHp <= 0) {
    return {
      stateSummary,
      readyForDecision: false,
      shouldStop: true,
      reason: "player_defeated"
    };
  }

  if (liveEnemyCount <= 0) {
    return {
      stateSummary,
      readyForDecision: false,
      shouldStop: true,
      reason: "no_live_enemies"
    };
  }

  if (phase !== "" && phase !== "combat_turn") {
    return {
      stateSummary,
      readyForDecision: false,
      shouldStop: !phase.startsWith("combat"),
      reason: phase.startsWith("combat") ? `waiting_${phase}` : `non_combat_phase:${phase}`
    };
  }

  if (!hasLegalActions) {
    return {
      stateSummary,
      readyForDecision: false,
      shouldStop: false,
      reason: "legal_actions_missing"
    };
  }

  return {
    stateSummary,
    readyForDecision: true,
    shouldStop: false,
    reason: "combat_turn_ready"
  };
}

function readMetrics(metricsPath) {
  if (!fs.existsSync(metricsPath)) {
    return {
      decisions: 0,
      submitted_decisions: 0,
      dry_run_decisions: 0,
      plans_completed: 0,
      plans_failed: 0,
      actions_applied: 0,
      stale_retries: 0,
      invalid_actions: 0,
      submit_failures: 0,
      result_timeouts: 0,
      stale_before_submit: 0,
      observed_combats: 0,
      combat_turn_decisions: 0,
      end_turns_applied: 0,
      combat_stop_events: 0,
      total_decision_ms: 0,
      average_decision_ms: 0,
      last_updated_at: null
    };
  }

  const parsed = safeJsonParse(fs.readFileSync(metricsPath, "utf8"));
  return isPlainObject(parsed) ? parsed : {};
}

function countObjectValues(value) {
  if (!isPlainObject(value)) {
    return 0;
  }

  return Object.values(value).reduce((sum, item) => {
    const numberValue = readNumber(item);
    return sum + (numberValue === null ? 0 : numberValue);
  }, 0);
}

function updateMetrics(runLogDir, record) {
  if (!runLogDir) {
    return null;
  }

  const metricsPath = path.join(runLogDir, "metrics.json");
  const metrics = {
    decisions: 0,
    submitted_decisions: 0,
    dry_run_decisions: 0,
    plans_completed: 0,
    plans_failed: 0,
    actions_applied: 0,
    stale_retries: 0,
    invalid_actions: 0,
    submit_failures: 0,
    result_timeouts: 0,
    stale_before_submit: 0,
    total_decision_ms: 0,
    average_decision_ms: 0,
    combat_stop_events: 0,
    last_updated_at: null,
    ...readMetrics(metricsPath)
  };

  metrics.decisions += 1;
  metrics.total_decision_ms += readNumber(record.decision_ms) ?? 0;
  metrics.average_decision_ms = metrics.decisions > 0
    ? Math.round(metrics.total_decision_ms / metrics.decisions)
    : 0;

  if (record.status === "dry_run") {
    metrics.dry_run_decisions += 1;
  }

  if (record.status === "submitted" || record.status === "completed" || record.status === "failed" || record.status === "applied") {
    metrics.submitted_decisions += 1;
  }

  if (record.combat_observed === true) {
    metrics.observed_combats += 1;
  }

  if (isPlainObject(record.state_summary) && record.state_summary.phase === "combat_turn") {
    metrics.combat_turn_decisions += 1;
  }

  const finalPlan = isPlainObject(record.final_action_plan)
    ? record.final_action_plan
    : (isPlainObject(record.action_plan) ? record.action_plan : null);
  if (finalPlan) {
    if (finalPlan.status === "completed") {
      metrics.plans_completed += 1;
    } else if (finalPlan.status === "failed") {
      metrics.plans_failed += 1;
    }

    metrics.actions_applied += Array.isArray(finalPlan.completed) ? finalPlan.completed.length : 0;
    metrics.stale_retries += countObjectValues(finalPlan.stale_retries);
  }

  const latestAction = isPlainObject(record.latest_action) ? record.latest_action : null;
  if (latestAction && latestAction.valid === false) {
    metrics.invalid_actions += 1;
  }

  if (record.status === "result_timeout") {
    metrics.result_timeouts = (readNumber(metrics.result_timeouts) ?? 0) + 1;
  }

  if (record.status === "submit_failed") {
    metrics.submit_failures = (readNumber(metrics.submit_failures) ?? 0) + 1;
  }

  if (record.status === "decision_stale_before_submit") {
    metrics.stale_before_submit = (readNumber(metrics.stale_before_submit) ?? 0) + 1;
  }

  const finalLatestAction = isPlainObject(record.final_latest_action) ? record.final_latest_action : null;
  if (!finalPlan && finalLatestAction && finalLatestAction.result === "applied") {
    metrics.actions_applied += 1;
  }

  if (finalLatestAction
    && finalLatestAction.result === "applied"
    && finalLatestAction.selected_action_id === "end_turn") {
    metrics.end_turns_applied += 1;
  }

  metrics.last_updated_at = nowIsoString();
  writeJsonFile(metricsPath, metrics);
  return metrics;
}

function updateCombatStopMetrics(runLogDir) {
  if (!runLogDir) {
    return null;
  }

  const metricsPath = path.join(runLogDir, "metrics.json");
  const metrics = {
    decisions: 0,
    submitted_decisions: 0,
    dry_run_decisions: 0,
    plans_completed: 0,
    plans_failed: 0,
    actions_applied: 0,
    stale_retries: 0,
    invalid_actions: 0,
    submit_failures: 0,
    result_timeouts: 0,
    stale_before_submit: 0,
    total_decision_ms: 0,
    average_decision_ms: 0,
    combat_stop_events: 0,
    last_updated_at: null,
    ...readMetrics(metricsPath)
  };

  metrics.combat_stop_events = (readNumber(metrics.combat_stop_events) ?? 0) + 1;
  metrics.last_updated_at = nowIsoString();
  writeJsonFile(metricsPath, metrics);
  return metrics;
}

function ensureScenarioConfigFile(runLogDir, options) {
  if (!runLogDir) {
    return;
  }

  const scenarioConfigPath = path.join(runLogDir, "scenario_config.json");
  if (fs.existsSync(scenarioConfigPath)) {
    return;
  }

  writeJsonFile(scenarioConfigPath, {
    scenario_id: options.scenarioId,
    play_session_id: options.playSessionId,
    description: "SpireMind combat play session",
    character: null,
    ascension: null,
    seed: null,
    start_point: "current_game_state",
    allowed_systems: ["combat"],
    decision_timeout_ms: options.timeoutMs,
    result_timeout_ms: options.resultTimeoutMs,
    max_retries: 2,
    human_intervention: "setup_only",
    created_at: nowIsoString()
  });
}

function ensureRunRecordFiles(runLogDir, options) {
  if (!runLogDir) {
    return;
  }

  ensureScenarioConfigFile(runLogDir, options);

  const deciderConfigPath = path.join(runLogDir, "decider_config.json");
  if (!fs.existsSync(deciderConfigPath)) {
    writeJsonFile(deciderConfigPath, {
      play_session_id: options.playSessionId,
      decider_type: options.mode,
      command: options.mode === "command" ? options.command : null,
      command_args_count: options.mode === "command" ? options.commandArgs.length : 0,
      max_actions_per_turn: options.maxActionsPerTurn,
      recent_history_limit: options.recentHistoryLimit,
      wait_result: options.waitResult,
      decision_timeout_ms: options.timeoutMs,
      result_timeout_ms: options.resultTimeoutMs,
      created_at: nowIsoString()
    });
  }
}

function appendCombatLog(runLogDir, event) {
  if (!runLogDir) {
    return;
  }

  appendJsonLine(path.join(runLogDir, "combat_log.jsonl"), event);
}

function createCombatObservedEvent(options, decisionId, stateSummary) {
  return {
    event_type: "combat_observed",
    recorded_at: nowIsoString(),
    play_session_id: options.playSessionId,
    decision_id: decisionId,
    state_version: stateSummary.state_version,
    state_id: stateSummary.state_id,
    phase: stateSummary.phase,
    player: stateSummary.player,
    hand_count: stateSummary.hand.length,
    enemies: stateSummary.enemies
  };
}

function createDecisionSubmittedEvent(options, decisionId, stateSummary, decision, submitted) {
  return {
    event_type: "decision_submitted",
    recorded_at: nowIsoString(),
    play_session_id: options.playSessionId,
    decision_id: decisionId,
    state_version: stateSummary.state_version,
    state_id: stateSummary.state_id,
    decider_type: options.mode,
    decision,
    selected_action_id: isPlainObject(submitted.latest_action) ? submitted.latest_action.selected_action_id || null : null,
    plan_id: isPlainObject(submitted.action_plan) ? submitted.action_plan.plan_id || null : null
  };
}

function createActionResultObservedEvent(options, decisionId, finalLatest, finalStatus, afterStateSummary, stateDelta) {
  const latestAction = finalLatest && isPlainObject(finalLatest.latest_action) ? finalLatest.latest_action : null;
  const actionPlan = finalLatest && isPlainObject(finalLatest.action_plan) ? finalLatest.action_plan : null;
  return {
    event_type: "action_result_observed",
    recorded_at: nowIsoString(),
    play_session_id: options.playSessionId,
    decision_id: decisionId,
    status: finalStatus,
    selected_action_id: latestAction ? latestAction.selected_action_id || null : null,
    result: latestAction ? latestAction.result || null : null,
    result_note: latestAction ? latestAction.result_note || null : null,
    action_type: latestAction ? latestAction.action_type || null : null,
    plan_id: actionPlan ? actionPlan.plan_id || null : null,
    plan_status: actionPlan ? actionPlan.status || null : null,
    completed_count: actionPlan && Array.isArray(actionPlan.completed) ? actionPlan.completed.length : null,
    after_state_summary: afterStateSummary || null,
    state_delta: stateDelta || null
  };
}

function createCombatStoppedEvent(options, stateSummary, reason, decisions) {
  return {
    event_type: "combat_loop_stopped",
    recorded_at: nowIsoString(),
    play_session_id: options.playSessionId,
    reason,
    decisions,
    state_version: stateSummary.state_version,
    state_id: stateSummary.state_id,
    phase: stateSummary.phase,
    player: stateSummary.player,
    live_enemy_count: countLiveEnemies(stateSummary),
    hand_count: stateSummary.hand.length
  };
}

function pickRecentEventFields(event) {
  const eventType = typeof event.event_type === "string" ? event.event_type : null;
  return {
    event_type: eventType,
    recorded_at: typeof event.recorded_at === "string" ? event.recorded_at : null,
    decision_id: typeof event.decision_id === "string" ? event.decision_id : null,
    status: typeof event.status === "string" ? event.status : null,
    result: typeof event.result === "string" ? event.result : null,
    reason: typeof event.reason === "string" ? event.reason : null,
    selected_action_id: typeof event.selected_action_id === "string" ? event.selected_action_id : null,
    plan_status: typeof event.plan_status === "string" ? event.plan_status : null,
    completed_count: readNumber(event.completed_count),
    state_delta: isPlainObject(event.state_delta) ? event.state_delta : null
  };
}

function pickRecentDecisionFields(record) {
  return {
    recorded_at: typeof record.recorded_at === "string" ? record.recorded_at : null,
    decision_id: typeof record.decision_id === "string" ? record.decision_id : null,
    status: typeof record.status === "string" ? record.status : null,
    decision: isPlainObject(record.decision) ? record.decision : null,
    state_delta: isPlainObject(record.state_delta) ? record.state_delta : null,
    wait_error: typeof record.wait_error === "string" ? record.wait_error : null,
    submit_error: typeof record.submit_error === "string" ? record.submit_error : null
  };
}

function buildRecentHistory(runLogDir, limit) {
  if (!runLogDir) {
    return null;
  }

  const normalizedLimit = Math.max(0, readNumber(limit) ?? 0);
  if (normalizedLimit <= 0) {
    return null;
  }

  const combatEvents = readJsonLines(path.join(runLogDir, "combat_log.jsonl"))
    .map(pickRecentEventFields)
    .slice(-normalizedLimit);
  const decisions = readJsonLines(path.join(runLogDir, "decisions.jsonl"))
    .map(pickRecentDecisionFields)
    .slice(-normalizedLimit);

  if (combatEvents.length === 0 && decisions.length === 0) {
    return null;
  }

  return {
    limit: normalizedLimit,
    combat_events: combatEvents,
    decisions
  };
}

function hasStateChangedSinceDecision(snapshot, latestSnapshot) {
  const originalVersion = readNumber(snapshot.state_version);
  const latestVersion = readNumber(latestSnapshot.state_version);
  const originalStateId = typeof snapshot.state_id === "string" ? snapshot.state_id : "";
  const latestStateId = typeof latestSnapshot.state_id === "string" ? latestSnapshot.state_id : "";

  if (originalVersion !== null && latestVersion !== null && originalVersion !== latestVersion) {
    return true;
  }

  return originalStateId !== "" && latestStateId !== "" && originalStateId !== latestStateId;
}

async function waitForSubmittedResult(options, planId, submissionId) {
  const deadline = Date.now() + options.resultTimeoutMs;
  let lastLatest = null;
  while (Date.now() <= deadline) {
    const latest = await getLatestAction(options.bridgeUrl, options.timeoutMs);
    lastLatest = latest;
    const plan = isPlainObject(latest.action_plan) ? latest.action_plan : null;
    if (planId && plan && plan.plan_id === planId && (plan.status === "completed" || plan.status === "failed")) {
      return latest;
    }

    const latestAction = isPlainObject(latest.latest_action) ? latest.latest_action : null;
    if (planId
      && plan
      && latestAction
      && latestAction.plan_id === planId
      && latestAction.result !== null
      && Array.isArray(plan.actions)) {
      const actionIndex = readNumber(latestAction.plan_action_index);
      if (actionIndex !== null && actionIndex >= plan.actions.length - 1) {
        return latest;
      }
    }

    if (!planId
      && latestAction
      && latestAction.submission_id === submissionId
      && latestAction.result !== null) {
      return latest;
    }

    await sleep(options.pollMs);
  }

  throw new Error(`제출 결과를 기다리다 시간 초과했습니다. plan_id=${planId || ""}, submission_id=${submissionId || ""}, latest=${JSON.stringify(lastLatest)}`);
}

function hasPendingAction(snapshot) {
  const latestAction = snapshot.latest_action;
  if (!isPlainObject(latestAction)) {
    return false;
  }

  return latestAction.result === null
    && latestAction.execution_status !== "invalid"
    && latestAction.execution_status !== "failed"
    && latestAction.execution_status !== "stale"
    && latestAction.execution_status !== "unsupported";
}

function readNumber(value) {
  if (value === null || value === undefined || value === "") {
    return null;
  }

  const parsed = Number(value);
  return Number.isFinite(parsed) ? parsed : null;
}

function chooseHeuristicDecision(snapshot, maxActionsPerTurn) {
  const state = snapshot.state;
  if (!isPlainObject(state)) {
    return null;
  }

  const player = isPlainObject(state.player) ? state.player : {};
  let energy = readNumber(player.energy);
  const legalActions = Array.isArray(state.legal_actions)
    ? state.legal_actions.filter(isPlainObject)
    : [];
  const enemies = Array.isArray(state.enemies)
    ? state.enemies.filter((enemy) => isPlainObject(enemy) && (readNumber(enemy.hp) ?? 1) > 0)
    : [];
  const primaryEnemy = enemies[0] || null;
  const targetCombatId = primaryEnemy ? readNumber(primaryEnemy.combat_id) : null;

  const actions = [];
  for (const legalAction of legalActions) {
    if (actions.length >= maxActionsPerTurn) {
      break;
    }

    if (legalAction.type !== "play_card" || legalAction.target_id === null || legalAction.target_id === undefined) {
      continue;
    }

    const cost = readNumber(legalAction.energy_cost);
    if (energy !== null && cost !== null && cost > energy) {
      continue;
    }

    const combatCardId = readNumber(legalAction.combat_card_id);
    const actionTargetCombatId = readNumber(legalAction.target_combat_id) ?? targetCombatId;
    if (combatCardId === null || actionTargetCombatId === null) {
      continue;
    }

    actions.push({
      type: "play_card",
      combat_card_id: combatCardId,
      target_combat_id: actionTargetCombatId
    });

    if (energy !== null && cost !== null && cost > 0) {
      energy -= cost;
    }
  }

  if (actions.length === 0) {
    const endTurn = legalActions.find((legalAction) => legalAction.type === "end_turn");
    if (!endTurn) {
      return null;
    }

    return {
      selected_action_id: endTurn.action_id,
      note: "사용할 공격 카드가 없어 턴을 종료합니다."
    };
  }

  actions.push({ type: "end_turn" });
  return {
    actions,
    reason: "휴리스틱 정책: 사용 가능한 공격 카드를 에너지 범위 안에서 사용한 뒤 턴을 종료합니다."
  };
}

function buildCommandPrompt(options, snapshot) {
  const recentHistory = buildRecentHistory(options.resolvedRunLogDir, options.recentHistoryLimit);
  return JSON.stringify(
    {
      task: "Slay the Spire 2 전투 상태를 보고 다음 행동을 JSON으로만 결정하세요.",
      play_session_id: options.playSessionId,
      recent_history: recentHistory,
      response_contract: {
        actions: [
          {
            type: "play_card",
            combat_card_id: "number",
            target_combat_id: "number|null"
          }
        ],
        selected_action_id: "string 선택 사항",
        reason: "짧은 이유"
      },
      rules: [
        "legal_actions에 있는 행동만 선택하세요.",
        "여러 카드를 쓰려면 actions 배열을 사용하세요.",
        "손패 순서 대신 combat_card_id를 우선 사용하세요.",
        "대상은 target_combat_id를 우선 사용하세요.",
        "설명 없이 JSON 객체만 출력하세요."
      ],
      state_version: snapshot.state_version,
      state: snapshot.state
    },
    null,
    2
  );
}

function runCommandDecision(options, snapshot) {
  if (options.command.trim() === "") {
    throw new Error("--mode command에는 --command가 필요합니다.");
  }

  const prompt = buildCommandPrompt(options, snapshot);
  return new Promise((resolve, reject) => {
    const child = spawn(options.command, options.commandArgs, {
      stdio: ["pipe", "pipe", "pipe"],
      windowsHide: true
    });

    let stdout = "";
    let stderr = "";
    const timer = setTimeout(() => {
      child.kill();
      reject(new Error(`의사결정 명령이 ${options.timeoutMs}ms 안에 끝나지 않았습니다.`));
    }, options.timeoutMs);

    child.stdout.setEncoding("utf8");
    child.stderr.setEncoding("utf8");
    child.stdout.on("data", (chunk) => {
      stdout += chunk;
    });
    child.stderr.on("data", (chunk) => {
      stderr += chunk;
    });
    child.on("error", (error) => {
      clearTimeout(timer);
      reject(error);
    });
    child.on("close", (code) => {
      clearTimeout(timer);
      if (code !== 0) {
        reject(new Error(`의사결정 명령 실패: exit=${code}, stderr=${stderr.trim()}`));
        return;
      }

      const parsed = safeJsonParse(stdout.trim());
      if (!isPlainObject(parsed)) {
        reject(new Error(`의사결정 명령 출력이 JSON 객체가 아닙니다: ${stdout}`));
        return;
      }

      resolve(parsed);
    });

    child.stdin.end(prompt);
  });
}

async function chooseDecision(options, snapshot) {
  if (options.mode === "heuristic") {
    return chooseHeuristicDecision(snapshot, options.maxActionsPerTurn);
  }

  if (options.mode === "command") {
    return runCommandDecision(options, snapshot);
  }

  throw new Error(`지원하지 않는 의사결정 모드입니다: ${options.mode}`);
}

async function waitForReadySnapshot(options, lastSeenVersion) {
  const deadline = Date.now() + options.timeoutMs;
  let lastSnapshot = null;
  while (Date.now() <= deadline) {
    const snapshot = await getCurrentState(options.bridgeUrl, options.timeoutMs);
    lastSnapshot = snapshot;
    const stateVersion = readNumber(snapshot.state_version) ?? 0;
    if (snapshot.status === "ready" && stateVersion >= lastSeenVersion && !hasPendingAction(snapshot)) {
      return snapshot;
    }

    await sleep(options.pollMs);
  }

  throw new Error(`결정 가능한 상태를 기다리다 시간 초과했습니다. 마지막 상태: ${JSON.stringify(lastSnapshot)}`);
}

async function waitForCombatDecisionOrStop(options, lastSeenVersion) {
  const deadline = Date.now() + options.timeoutMs;
  let lastSnapshot = null;
  let lastReadiness = null;
  while (Date.now() <= deadline) {
    const snapshot = await getCurrentState(options.bridgeUrl, options.timeoutMs);
    lastSnapshot = snapshot;
    const stateVersion = readNumber(snapshot.state_version) ?? 0;
    if (snapshot.status === "ready" && stateVersion >= lastSeenVersion && !hasPendingAction(snapshot)) {
      const readiness = classifyCombatReadiness(snapshot);
      lastReadiness = readiness;
      if (readiness.readyForDecision || readiness.shouldStop) {
        return {
          snapshot,
          stateSummary: readiness.stateSummary,
          stopReason: readiness.shouldStop ? readiness.reason : null
        };
      }
    }

    await sleep(options.pollMs);
  }

  throw new Error(`전투 판단 가능 상태를 기다리다 시간 초과했습니다. 마지막 판정=${JSON.stringify(lastReadiness)}, 마지막 상태=${JSON.stringify(lastSnapshot)}`);
}

async function main() {
  const options = parseArgs(process.argv.slice(2));
  if (options.help) {
    showHelp();
    return;
  }

  if (typeof options.scenarioId !== "string" || options.scenarioId.trim() === "") {
    options.scenarioId = createRecordId("scenario");
  }
  if (typeof options.playSessionId !== "string" || options.playSessionId.trim() === "") {
    options.playSessionId = createRecordId("session");
  }

  const runLogDir = ensureRunLogDir(options.runLogDir);
  options.resolvedRunLogDir = runLogDir;
  ensureRunRecordFiles(runLogDir, options);

  let decisions = 0;
  let lastSeenVersion = 0;
  let combatLoopStopped = false;
  const observedCombatIds = new Set();
  while (decisions < options.maxDecisions) {
    const readiness = options.untilCombatEnd
      ? await waitForCombatDecisionOrStop(options, lastSeenVersion)
      : null;
    if (readiness && readiness.stopReason) {
      combatLoopStopped = true;
      appendCombatLog(runLogDir, createCombatStoppedEvent(options, readiness.stateSummary, readiness.stopReason, decisions));
      updateCombatStopMetrics(runLogDir);
      process.stdout.write(`${JSON.stringify({
        status: "combat_loop_stopped",
        reason: readiness.stopReason,
        decisions,
        state_version: readiness.stateSummary.state_version,
        state_id: readiness.stateSummary.state_id,
        phase: readiness.stateSummary.phase,
        live_enemy_count: countLiveEnemies(readiness.stateSummary)
      }, null, 2)}\n`);
      break;
    }

    const snapshot = readiness ? readiness.snapshot : await waitForReadySnapshot(options, lastSeenVersion);
    const stateVersion = readNumber(snapshot.state_version) ?? 0;
    const decisionId = createDecisionId();
    const stateSummary = readiness ? readiness.stateSummary : summarizeState(snapshot);
    const combatKey = stateSummary.state_id || `state_version_${stateVersion}`;
    const combatObserved = stateSummary.phase === "combat_turn" && !observedCombatIds.has(combatKey);
    if (combatObserved) {
      observedCombatIds.add(combatKey);
      appendCombatLog(runLogDir, createCombatObservedEvent(options, decisionId, stateSummary));
    }
    const decisionStartedAt = Date.now();
    const decision = await chooseDecision(options, snapshot);
    const decisionMs = Date.now() - decisionStartedAt;
    if (!isPlainObject(decision)) {
      throw new Error("의사결정 결과가 비어 있습니다.");
    }

    if (options.dryRun) {
      const record = {
        event_type: "decision_recorded",
        decision_id: decisionId,
        recorded_at: nowIsoString(),
        status: "dry_run",
        decider_type: options.mode,
        state_version: stateVersion,
        state_summary: stateSummary,
        play_session_id: options.playSessionId,
        combat_observed: combatObserved,
        decision,
        decision_ms: decisionMs
      };
      if (runLogDir) {
        appendJsonLine(path.join(runLogDir, "decisions.jsonl"), record);
        updateMetrics(runLogDir, record);
      }
      process.stdout.write(`${JSON.stringify({ status: "dry_run", state_version: stateVersion, decision }, null, 2)}\n`);
    } else {
      if (options.untilCombatEnd) {
        const latestBeforeSubmit = await getCurrentState(options.bridgeUrl, options.timeoutMs);
        if (hasStateChangedSinceDecision(snapshot, latestBeforeSubmit)) {
          const latestBeforeSubmitSummary = summarizeState(latestBeforeSubmit);
          const record = {
            event_type: "decision_recorded",
            decision_id: decisionId,
            recorded_at: nowIsoString(),
            status: "decision_stale_before_submit",
            decider_type: options.mode,
            state_version: stateVersion,
            state_summary: stateSummary,
            latest_state_summary: latestBeforeSubmitSummary,
            latest_state_delta: buildStateDelta(stateSummary, latestBeforeSubmitSummary),
            play_session_id: options.playSessionId,
            combat_observed: combatObserved,
            decision,
            decision_ms: decisionMs
          };
          if (runLogDir) {
            appendJsonLine(path.join(runLogDir, "decisions.jsonl"), record);
            updateMetrics(runLogDir, record);
          }
          process.stdout.write(`${JSON.stringify({
            status: "decision_stale_before_submit",
            state_version: stateVersion,
            latest_state_version: readNumber(latestBeforeSubmit.state_version),
            decision
          }, null, 2)}\n`);
          decisions += 1;
          lastSeenVersion = readNumber(latestBeforeSubmit.state_version) ?? stateVersion;
          continue;
        }
      }

      let submitted = null;
      try {
        submitted = await submitDecision(
          options.bridgeUrl,
          decision,
          stateVersion,
          `spiremind-decision-loop:${options.mode}`,
          options.timeoutMs
        );
      } catch (error) {
        const record = {
          event_type: "decision_recorded",
          decision_id: decisionId,
          recorded_at: nowIsoString(),
          status: "submit_failed",
          decider_type: options.mode,
          state_version: stateVersion,
          state_summary: stateSummary,
          play_session_id: options.playSessionId,
          combat_observed: combatObserved,
          decision,
          submit_error: error instanceof Error ? error.message : String(error),
          decision_ms: decisionMs
        };
        if (runLogDir) {
          appendJsonLine(path.join(runLogDir, "decisions.jsonl"), record);
          updateMetrics(runLogDir, record);
        }
        throw error;
      }
      appendCombatLog(runLogDir, createDecisionSubmittedEvent(options, decisionId, stateSummary, decision, submitted));
      const submittedPlan = isPlainObject(submitted.action_plan) ? submitted.action_plan : null;
      const planId = submittedPlan ? submittedPlan.plan_id : null;
      const submittedAction = isPlainObject(submitted.latest_action) ? submitted.latest_action : null;
      const submissionId = submittedAction ? submittedAction.submission_id : null;
      const shouldWaitResult = options.waitResult
        && submittedAction
        && submittedAction.valid === true
        && submittedAction.execution_status !== "invalid";
      let waitError = null;
      let finalLatest = null;
      if (shouldWaitResult) {
        try {
          finalLatest = await waitForSubmittedResult(options, planId, submissionId);
        } catch (error) {
          waitError = error;
          finalLatest = { latest_action: submitted.latest_action || null, action_plan: submitted.action_plan || null };
        }
      } else if (options.waitResult) {
        finalLatest = { latest_action: submitted.latest_action || null, action_plan: submitted.action_plan || null };
      }
      const finalPlan = finalLatest && isPlainObject(finalLatest.action_plan) ? finalLatest.action_plan : null;
      const finalLatestAction = finalLatest && isPlainObject(finalLatest.latest_action) ? finalLatest.latest_action : null;
      const finalStatus = waitError
        ? "result_timeout"
        : finalPlan && (finalPlan.status === "completed" || finalPlan.status === "failed")
        ? finalPlan.status
        : (finalLatestAction && typeof finalLatestAction.result === "string" ? finalLatestAction.result : null)
          || (finalLatestAction && typeof finalLatestAction.execution_status === "string" ? finalLatestAction.execution_status : null)
          || "submitted";
      let afterStateSummary = null;
      let stateDelta = null;
      let afterStateError = null;
      if (finalLatest) {
        try {
          const afterSnapshot = await getCurrentState(options.bridgeUrl, options.timeoutMs);
          afterStateSummary = summarizeState(afterSnapshot);
          stateDelta = buildStateDelta(stateSummary, afterStateSummary);
        } catch (error) {
          afterStateError = error instanceof Error ? error.message : String(error);
        }
      }
      const record = {
        event_type: "decision_recorded",
        decision_id: decisionId,
        recorded_at: nowIsoString(),
        status: finalStatus,
        decider_type: options.mode,
        state_version: stateVersion,
        state_summary: stateSummary,
        play_session_id: options.playSessionId,
        combat_observed: combatObserved,
        decision,
        latest_action: submitted.latest_action || null,
        action_plan: submitted.action_plan || null,
        final_latest_action: finalLatest ? finalLatest.latest_action || null : null,
        final_action_plan: finalPlan,
        after_state_summary: afterStateSummary,
        state_delta: stateDelta,
        after_state_error: afterStateError,
        wait_error: waitError ? waitError.message : null,
        decision_ms: decisionMs
      };
      if (runLogDir) {
        appendJsonLine(path.join(runLogDir, "decisions.jsonl"), record);
        updateMetrics(runLogDir, record);
      }
      if (finalLatest) {
        appendCombatLog(runLogDir, createActionResultObservedEvent(options, decisionId, finalLatest, finalStatus, afterStateSummary, stateDelta));
      }
      if (waitError) {
        throw waitError;
      }
      process.stdout.write(`${JSON.stringify({
        status: finalStatus,
        state_version: stateVersion,
        decision,
        latest_action: submitted.latest_action || null,
        action_plan: submitted.action_plan || null,
        final_latest_action: finalLatest ? finalLatest.latest_action || null : null,
        final_action_plan: finalPlan
      }, null, 2)}\n`);
    }

    decisions += 1;
    lastSeenVersion = stateVersion + 1;
    if (options.once) {
      break;
    }
  }

  if (options.untilCombatEnd && !combatLoopStopped && decisions >= options.maxDecisions) {
    process.stdout.write(`${JSON.stringify({
      status: "max_decisions_reached",
      decisions,
      max_decisions: options.maxDecisions
    }, null, 2)}\n`);
  }
}

main().catch((error) => {
  process.stderr.write(`${error.stack || error.message || String(error)}\n`);
  process.exit(1);
});
