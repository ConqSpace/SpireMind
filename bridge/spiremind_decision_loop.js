#!/usr/bin/env node
"use strict";

const { spawn } = require("child_process");
const http = require("http");
const https = require("https");

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
    dryRun: false,
    maxDecisions: 1,
    maxActionsPerTurn: 3,
    pollMs: DEFAULT_POLL_MS,
    timeoutMs: DEFAULT_TIMEOUT_MS,
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
      continue;
    }

    if (token === "--dry-run") {
      options.dryRun = true;
      continue;
    }

    if (token === "--max-decisions" && index + 1 < argv.length) {
      options.maxDecisions = parsePositiveInt(argv[index + 1], options.maxDecisions);
      index += 1;
      continue;
    }

    if (token === "--max-actions-per-turn" && index + 1 < argv.length) {
      options.maxActionsPerTurn = parsePositiveInt(argv[index + 1], options.maxActionsPerTurn);
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
    "",
    "Modes:",
    "  heuristic  현재 상태에서 사용 가능한 공격 카드 묶음과 end_turn을 제출한다.",
    "  command    외부 명령에 JSON 프롬프트를 stdin으로 보내고 JSON 결정을 stdout에서 읽는다.",
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

function buildCommandPrompt(snapshot) {
  return JSON.stringify(
    {
      task: "Slay the Spire 2 전투 상태를 보고 다음 행동을 JSON으로만 결정하세요.",
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

  const prompt = buildCommandPrompt(snapshot);
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

async function main() {
  const options = parseArgs(process.argv.slice(2));
  if (options.help) {
    showHelp();
    return;
  }

  let decisions = 0;
  let lastSeenVersion = 0;
  while (decisions < options.maxDecisions) {
    const snapshot = await waitForReadySnapshot(options, lastSeenVersion);
    const stateVersion = readNumber(snapshot.state_version) ?? 0;
    const decision = await chooseDecision(options, snapshot);
    if (!isPlainObject(decision)) {
      throw new Error("의사결정 결과가 비어 있습니다.");
    }

    if (options.dryRun) {
      process.stdout.write(`${JSON.stringify({ status: "dry_run", state_version: stateVersion, decision }, null, 2)}\n`);
    } else {
      const submitted = await submitDecision(
        options.bridgeUrl,
        decision,
        stateVersion,
        `spiremind-decision-loop:${options.mode}`,
        options.timeoutMs
      );
      process.stdout.write(`${JSON.stringify({
        status: "submitted",
        state_version: stateVersion,
        decision,
        latest_action: submitted.latest_action || null,
        action_plan: submitted.action_plan || null
      }, null, 2)}\n`);
    }

    decisions += 1;
    lastSeenVersion = stateVersion + 1;
    if (options.once) {
      break;
    }
  }
}

main().catch((error) => {
  process.stderr.write(`${error.stack || error.message || String(error)}\n`);
  process.exit(1);
});
