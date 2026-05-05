#!/usr/bin/env node
"use strict";

const { spawn } = require("child_process");
const fs = require("fs");
const http = require("http");
const path = require("path");

const DEFAULT_BRIDGE_URL = "http://127.0.0.1:17832";
const DEFAULT_TIMEOUT_MS = 30000;
const DEFAULT_ACTION_TIMEOUT_MS = 90000;
const DEFAULT_COMBAT_TIMEOUT_MS = 240000;

function parseArgs(argv) {
  const options = {
    bridgeUrl: process.env.SPIREMIND_BRIDGE_URL || DEFAULT_BRIDGE_URL,
    mode: process.env.SPIREMIND_DECIDER_MODE || "heuristic",
    maxRooms: 3,
    maxSteps: 80,
    actionTimeoutMs: DEFAULT_ACTION_TIMEOUT_MS,
    combatTimeoutMs: DEFAULT_COMBAT_TIMEOUT_MS,
    requestTimeoutMs: DEFAULT_TIMEOUT_MS,
    outDir: path.resolve(process.cwd(), "logs", "room_runner"),
    label: "",
    stopOnFailure: true,
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
    if (token === "--max-rooms" && index + 1 < argv.length) {
      options.maxRooms = parsePositiveInt(argv[index + 1], options.maxRooms);
      index += 1;
      continue;
    }
    if (token.startsWith("--max-rooms=")) {
      options.maxRooms = parsePositiveInt(token.slice("--max-rooms=".length), options.maxRooms);
      continue;
    }
    if (token === "--max-steps" && index + 1 < argv.length) {
      options.maxSteps = parsePositiveInt(argv[index + 1], options.maxSteps);
      index += 1;
      continue;
    }
    if (token.startsWith("--max-steps=")) {
      options.maxSteps = parsePositiveInt(token.slice("--max-steps=".length), options.maxSteps);
      continue;
    }
    if (token === "--action-timeout-ms" && index + 1 < argv.length) {
      options.actionTimeoutMs = parsePositiveInt(argv[index + 1], options.actionTimeoutMs);
      index += 1;
      continue;
    }
    if (token.startsWith("--action-timeout-ms=")) {
      options.actionTimeoutMs = parsePositiveInt(token.slice("--action-timeout-ms=".length), options.actionTimeoutMs);
      continue;
    }
    if (token === "--combat-timeout-ms" && index + 1 < argv.length) {
      options.combatTimeoutMs = parsePositiveInt(argv[index + 1], options.combatTimeoutMs);
      index += 1;
      continue;
    }
    if (token.startsWith("--combat-timeout-ms=")) {
      options.combatTimeoutMs = parsePositiveInt(token.slice("--combat-timeout-ms=".length), options.combatTimeoutMs);
      continue;
    }
    if (token === "--out-dir" && index + 1 < argv.length) {
      options.outDir = path.resolve(argv[index + 1]);
      index += 1;
      continue;
    }
    if (token.startsWith("--out-dir=")) {
      options.outDir = path.resolve(token.slice("--out-dir=".length));
      continue;
    }
    if (token === "--label" && index + 1 < argv.length) {
      options.label = argv[index + 1];
      index += 1;
      continue;
    }
    if (token.startsWith("--label=")) {
      options.label = token.slice("--label=".length);
      continue;
    }
    if (token === "--keep-going") {
      options.stopOnFailure = false;
    }
  }

  return options;
}

function parsePositiveInt(value, fallback) {
  const parsed = Number.parseInt(String(value), 10);
  return Number.isFinite(parsed) && parsed > 0 ? parsed : fallback;
}

function printHelp() {
  process.stdout.write(`Usage: node bridge/spiremind_room_runner.js [options]\n\n`);
  process.stdout.write(`Options:\n`);
  process.stdout.write(`  --max-rooms N          지도 노드/방 단위 진행 수. 기본 3\n`);
  process.stdout.write(`  --max-steps N          안전용 전체 루프 제한. 기본 80\n`);
  process.stdout.write(`  --mode heuristic       decision_loop 모드. 기본 heuristic\n`);
  process.stdout.write(`  --out-dir PATH         JSONL 로그 폴더. 기본 logs/room_runner\n`);
  process.stdout.write(`  --label TEXT           실행 라벨\n`);
  process.stdout.write(`  --keep-going           실패가 나도 가능한 경우 계속 진행\n`);
}

function nowIsoString() {
  return new Date().toISOString();
}

function makeRunDir(options) {
  const stamp = new Date().toISOString().replace(/[:.]/g, "").replace("T", "_").replace("Z", "");
  const suffix = options.label ? `_${sanitizeFilePart(options.label)}` : "";
  const runDir = path.join(options.outDir, `${stamp}${suffix}`);
  fs.mkdirSync(runDir, { recursive: true });
  return runDir;
}

function sanitizeFilePart(value) {
  return String(value).replace(/[^a-zA-Z0-9_.-]+/g, "_").slice(0, 80);
}

function appendJsonLine(filePath, value) {
  fs.appendFileSync(filePath, `${JSON.stringify(value)}\n`, { encoding: "utf8" });
}

function httpJson(url, timeoutMs) {
  return new Promise((resolve, reject) => {
    const request = http.get(url, (response) => {
      const chunks = [];
      response.on("data", (chunk) => chunks.push(chunk));
      response.on("end", () => {
        const text = Buffer.concat(chunks).toString("utf8");
        if (response.statusCode < 200 || response.statusCode >= 300) {
          reject(new Error(`HTTP ${response.statusCode}: ${text.slice(0, 500)}`));
          return;
        }
        try {
          resolve(JSON.parse(text));
        } catch (error) {
          reject(error);
        }
      });
    });
    request.setTimeout(timeoutMs, () => {
      request.destroy(new Error(`HTTP timeout after ${timeoutMs}ms`));
    });
    request.on("error", reject);
  });
}

async function getCurrentState(options) {
  return await httpJson(`${options.bridgeUrl.replace(/\/$/, "")}/state/current`, options.requestTimeoutMs);
}

async function getLatestAction(options) {
  return await httpJson(`${options.bridgeUrl.replace(/\/$/, "")}/action/latest`, options.requestTimeoutMs);
}

function summarizeState(snapshot) {
  const state = snapshot && typeof snapshot.state === "object" && snapshot.state !== null ? snapshot.state : {};
  const player = state.player && typeof state.player === "object" ? state.player : {};
  const roomContext = snapshot && typeof snapshot.room_context_summary === "object" && snapshot.room_context_summary !== null
    ? snapshot.room_context_summary
    : {};
  const legalActionIds = Array.isArray(snapshot.legal_action_ids) ? snapshot.legal_action_ids : [];
  const enemies = Array.isArray(state.enemies) ? state.enemies : [];
  const liveEnemies = enemies.filter((enemy) => enemy && enemy.hp !== 0);
  return {
    state_version: snapshot ? snapshot.state_version : null,
    state_id: snapshot ? snapshot.state_id : null,
    phase: typeof state.phase === "string" ? state.phase : null,
    room_kind: roomContext.room_kind || null,
    map_row: roomContext.map_row ?? null,
    hp: player.hp ?? null,
    max_hp: player.max_hp ?? null,
    gold: player.gold ?? null,
    legal_action_count: legalActionIds.length,
    legal_action_ids: legalActionIds.slice(0, 8),
    live_enemy_count: liveEnemies.length
  };
}

function summarizeLatestAction(payload) {
  const action = payload && typeof payload.latest_action === "object" && payload.latest_action !== null
    ? payload.latest_action
    : null;
  if (!action) {
    return null;
  }

  return {
    selected_action_id: action.selected_action_id || null,
    action_type: action.action_type || (action.action && action.action.type) || null,
    result: action.result || null,
    execution_status: action.execution_status || null,
    result_note: action.result_note || null
  };
}

function shouldUseCombatLoop(summary) {
  return summary.phase === "combat_turn" && summary.live_enemy_count > 0;
}

function isTerminal(summary) {
  return summary.phase === "game_over" || summary.phase === "run_finished";
}

function isMap(summary) {
  return summary.phase === "map";
}

function runDecisionLoop(options, combatMode) {
  const args = [
    path.join(__dirname, "spiremind_decision_loop.js"),
    "--mode", options.mode,
    "--wait-result",
    "--timeout-ms", String(options.requestTimeoutMs),
    "--result-timeout-ms", combatMode ? String(options.combatTimeoutMs) : String(options.actionTimeoutMs)
  ];
  if (combatMode) {
    args.push("--until-combat-end", "--max-decisions", "70");
  } else {
    args.push("--once");
  }

  const timeoutMs = combatMode ? options.combatTimeoutMs + 15000 : options.actionTimeoutMs + 15000;
  return new Promise((resolve) => {
    const child = spawn(process.execPath, args, {
      cwd: path.resolve(__dirname, ".."),
      windowsHide: true,
      stdio: ["ignore", "pipe", "pipe"]
    });
    let stdout = "";
    let stderr = "";
    let timedOut = false;
    const timer = setTimeout(() => {
      timedOut = true;
      child.kill("SIGTERM");
      setTimeout(() => child.kill("SIGKILL"), 2000).unref();
    }, timeoutMs);

    child.stdout.on("data", (chunk) => {
      stdout += chunk.toString("utf8");
      if (stdout.length > 200000) {
        stdout = stdout.slice(-100000);
      }
    });
    child.stderr.on("data", (chunk) => {
      stderr += chunk.toString("utf8");
      if (stderr.length > 20000) {
        stderr = stderr.slice(-10000);
      }
    });
    child.on("close", (code, signal) => {
      clearTimeout(timer);
      resolve({
        ok: code === 0 && !timedOut,
        code,
        signal,
        timed_out: timedOut,
        parsed_tail: parseLastJsonObject(stdout),
        stdout_tail: stdout.slice(-2000),
        stderr_tail: stderr.slice(-2000)
      });
    });
  });
}

function parseLastJsonObject(text) {
  const trimmed = String(text || "").trim();
  if (trimmed === "") {
    return null;
  }

  for (let index = trimmed.lastIndexOf("\n{"); index >= -1; index = trimmed.lastIndexOf("\n{", index - 1)) {
    const start = index >= 0 ? index + 1 : 0;
    const candidate = trimmed.slice(start).trim();
    try {
      return JSON.parse(candidate);
    } catch {
      if (index <= 0) {
        break;
      }
    }
  }

  return null;
}

async function main() {
  const options = parseArgs(process.argv.slice(2));
  if (options.help) {
    printHelp();
    return;
  }

  const runDir = makeRunDir(options);
  const eventsPath = path.join(runDir, "events.jsonl");
  const summaryPath = path.join(runDir, "summary.json");
  const startedAt = nowIsoString();
  let roomsCompleted = 0;
  let stopReason = "max_steps_reached";
  let lastSummary = null;

  appendJsonLine(eventsPath, {
    event_type: "runner_started",
    at: startedAt,
    options: {
      bridge_url: options.bridgeUrl,
      mode: options.mode,
      max_rooms: options.maxRooms,
      max_steps: options.maxSteps,
      label: options.label
    }
  });

  for (let step = 0; step < options.maxSteps; step += 1) {
    const before = summarizeState(await getCurrentState(options));
    lastSummary = before;
    appendJsonLine(eventsPath, {
      event_type: "step_started",
      at: nowIsoString(),
      step,
      rooms_completed: roomsCompleted,
      state: before
    });

    if (isTerminal(before)) {
      stopReason = "terminal_state";
      break;
    }

    if (before.legal_action_count === 0) {
      stopReason = "no_legal_actions";
      break;
    }

    const wasMap = isMap(before);
    const combatMode = shouldUseCombatLoop(before);
    const decisionResult = await runDecisionLoop(options, combatMode);
    const after = summarizeState(await getCurrentState(options));
    const latest = summarizeLatestAction(await getLatestAction(options));
    lastSummary = after;

    appendJsonLine(eventsPath, {
      event_type: "step_finished",
      at: nowIsoString(),
      step,
      combat_mode: combatMode,
      decision_result: {
        ok: decisionResult.ok,
        code: decisionResult.code,
        signal: decisionResult.signal,
        timed_out: decisionResult.timed_out,
        parsed_tail: decisionResult.parsed_tail
      },
      latest_action: latest,
      state_before: before,
      state_after: after
    });

    process.stdout.write(`${JSON.stringify({
      step,
      rooms_completed: roomsCompleted,
      before_phase: before.phase,
      after_phase: after.phase,
      hp: after.hp,
      gold: after.gold,
      latest
    })}\n`);

    if (latest && latest.result && !["applied", "terminal_transition"].includes(latest.result)) {
      stopReason = `action_${latest.result}`;
      if (options.stopOnFailure) {
        break;
      }
    }

    if (!decisionResult.ok) {
      stopReason = decisionResult.timed_out ? "decision_timeout" : "decision_process_failed";
      if (options.stopOnFailure) {
        break;
      }
    }

    if (isTerminal(after)) {
      stopReason = "terminal_state";
      break;
    }

    if (wasMap && after.phase !== "map") {
      roomsCompleted += 1;
      if (roomsCompleted >= options.maxRooms) {
        stopReason = "max_rooms_reached";
        break;
      }
    }
  }

  const finishedAt = nowIsoString();
  const latest = summarizeLatestAction(await getLatestAction(options).catch(() => null));
  const summary = {
    started_at: startedAt,
    finished_at: finishedAt,
    stop_reason: stopReason,
    rooms_completed: roomsCompleted,
    last_state: lastSummary,
    latest_action: latest,
    log_dir: runDir
  };
  fs.writeFileSync(summaryPath, `${JSON.stringify(summary, null, 2)}\n`, { encoding: "utf8" });
  process.stdout.write(`${JSON.stringify(summary, null, 2)}\n`);
}

main().catch((error) => {
  process.stderr.write(`${error && error.stack ? error.stack : String(error)}\n`);
  process.exitCode = 1;
});
