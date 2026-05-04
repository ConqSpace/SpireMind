#!/usr/bin/env node
"use strict";

const { spawn } = require("child_process");
const fs = require("fs");
const http = require("http");
const https = require("https");
const path = require("path");

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

function buildDecisionRequest(options, snapshot) {
  return {
    task: "Slay the Spire 2 상태를 보고 현재 legal_actions 중 실행할 행동 하나를 JSON으로만 결정하세요.",
    play_session_id: options.playSessionId,
    scenario_id: options.scenarioId,
    response_contract: {
      selected_action_id: "legal_actions 배열에 있는 action_id 문자열",
      reason: "선택 이유 한 문장"
    },
    rules: [
      "반드시 legal_actions에 있는 action_id 하나만 selected_action_id로 선택하세요.",
      "게임 화면을 직접 클릭하지 말고, 브리지에 노출된 합법 행동만 선택하세요.",
      "전투에서도 한 번에 카드 한 장, 포션 하나, 또는 턴 종료 하나만 선택하세요.",
      "지도에서는 state.map.path_options_summary.options를 읽고 전체 경로 정보를 비교한 뒤 legal_actions의 현재 선택 가능 노드 action_id를 선택하세요.",
      "판단이 쉬운 continue_run, proceed_rewards, 단일 지도 선택도 벤치마크 모드에서는 직접 판단하세요.",
      "game_over나 run_finished 같은 종료 화면은 실행 옵션에서 허용된 경우에만 legal_actions로 전달됩니다.",
      "설명 없이 JSON 객체만 출력하세요."
    ],
    state_version: snapshot.state_version,
    state_id: snapshot.state_id || null,
    state: snapshot.state
  };
}

function buildAppServerDecisionRequest(options, snapshot) {
  const baseRequest = buildDecisionRequest(options, snapshot);
  return {
    ...baseRequest,
    state: compactStateForDecision(snapshot.state)
  };
}

function compactStateForDecision(state) {
  const source = isPlainObject(state) ? state : {};
  const piles = isPlainObject(source.piles) ? source.piles : {};
  const map = isPlainObject(source.map) ? source.map : {};
  return {
    phase: typeof source.phase === "string" ? source.phase : null,
    state_id: typeof source.state_id === "string" ? source.state_id : null,
    run: isPlainObject(source.run) ? source.run : null,
    player: isPlainObject(source.player) ? source.player : null,
    hand: Array.isArray(piles.hand) ? piles.hand : [],
    draw_pile_count: Array.isArray(piles.draw_pile) ? piles.draw_pile.length : null,
    discard_pile_count: Array.isArray(piles.discard_pile) ? piles.discard_pile.length : null,
    exhaust_pile_count: Array.isArray(piles.exhaust_pile) ? piles.exhaust_pile.length : null,
    enemies: Array.isArray(source.enemies) ? source.enemies : [],
    relics: Array.isArray(source.relics) ? source.relics : [],
    rewards: isPlainObject(source.rewards) ? source.rewards : null,
    shop: isPlainObject(source.shop) ? source.shop : null,
    card_selection: isPlainObject(source.card_selection) ? source.card_selection : null,
    event: isPlainObject(source.event) ? source.event : null,
    map: {
      current: isPlainObject(map.current) ? map.current : null,
      available_next_nodes: Array.isArray(map.available_next_nodes) ? map.available_next_nodes : [],
      path_options_summary: isPlainObject(map.path_options_summary) ? map.path_options_summary : null
    },
    legal_actions: Array.isArray(source.legal_actions) ? source.legal_actions : []
  };
}

function runCommandDecision(options, snapshot) {
  const requestText = JSON.stringify(buildDecisionRequest(options, snapshot), null, 2);
  return new Promise((resolve, reject) => {
    const child = spawn(options.command, options.commandArgs, {
      cwd: process.cwd(),
      stdio: ["pipe", "pipe", "pipe"],
      windowsHide: true
    });
    let stdout = "";
    let stderr = "";
    let settled = false;
    const timeout = setTimeout(() => {
      if (settled) {
        return;
      }

      settled = true;
      child.kill();
      reject(new Error(`판단기 실행 시간이 ${options.timeoutMs}ms를 넘었습니다.`));
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
      if (settled) {
        return;
      }

      settled = true;
      clearTimeout(timeout);
      reject(error);
    });
    child.on("close", (code) => {
      if (settled) {
        return;
      }

      settled = true;
      clearTimeout(timeout);
      if (code !== 0) {
        reject(new Error(`판단기가 종료 코드 ${code}로 끝났습니다. stderr=${stderr.trim()}`));
        return;
      }

      const parsed = safeJsonParse(stdout.trim());
      if (!isPlainObject(parsed)) {
        reject(new Error(`판단기 출력이 JSON 객체가 아닙니다. stdout=${stdout.trim()}, stderr=${stderr.trim()}`));
        return;
      }

      resolve(parsed);
    });

    child.stdin.end(requestText, "utf8");
  });
}

class JsonRpcLineClient {
  constructor(command, args, timeoutMs) {
    this.command = command;
    this.args = args;
    this.timeoutMs = timeoutMs;
    this.nextId = 1;
    this.pending = new Map();
    this.notifications = [];
    this.notificationWaiters = [];
    this.buffer = "";
    this.closed = false;
    this.child = null;
  }

  start() {
    this.debug = process.env.SPIREMIND_DEBUG_APP_SERVER === "1";
    this.child = spawn(this.command, this.args, {
      cwd: process.cwd(),
      stdio: ["pipe", "pipe", "pipe"],
      windowsHide: true,
      shell: process.platform === "win32" && /\.cmd$/i.test(this.command)
    });
    this.child.stdout.setEncoding("utf8");
    this.child.stderr.setEncoding("utf8");
    this.child.stdout.on("data", (chunk) => this.handleStdout(chunk));
    this.child.stderr.on("data", (chunk) => {
      const text = String(chunk).trim();
      if (this.debug && text !== "") {
        const clipped = text.length > 1000 ? `${text.slice(0, 1000)}...` : text;
        process.stderr.write(`[codex app-server] ${clipped}\n`);
      }
    });
    this.child.on("close", () => {
      this.closed = true;
      for (const { reject, timeout } of this.pending.values()) {
        clearTimeout(timeout);
        reject(new Error("Codex app-server가 종료되었습니다."));
      }
      this.pending.clear();
      for (const waiter of this.notificationWaiters) {
        clearTimeout(waiter.timeout);
        waiter.reject(new Error("Codex app-server가 종료되었습니다."));
      }
      this.notificationWaiters = [];
    });
  }

  stop() {
    if (this.child && !this.child.killed) {
      this.child.kill();
    }
  }

  handleStdout(chunk) {
    this.buffer += chunk;
    while (true) {
      const newlineIndex = this.buffer.indexOf("\n");
      if (newlineIndex < 0) {
        break;
      }

      const line = this.buffer.slice(0, newlineIndex).trim();
      this.buffer = this.buffer.slice(newlineIndex + 1);
      if (line === "") {
        continue;
      }

      const message = safeJsonParse(line);
      if (!isPlainObject(message)) {
        process.stderr.write(`[codex app-server] JSON-RPC가 아닌 출력: ${line}\n`);
        continue;
      }

      this.handleMessage(message);
    }
  }

  handleMessage(message) {
    if (this.debug) {
      const methodOrId = typeof message.method === "string" ? message.method : `response:${message.id}`;
      process.stderr.write(`[codex app-server message] ${methodOrId}\n`);
    }

    if (Object.prototype.hasOwnProperty.call(message, "id")
      && (Object.prototype.hasOwnProperty.call(message, "result")
        || Object.prototype.hasOwnProperty.call(message, "error"))) {
      const key = String(message.id);
      const pending = this.pending.get(key);
      if (!pending) {
        return;
      }

      clearTimeout(pending.timeout);
      this.pending.delete(key);
      if (message.error) {
        pending.reject(new Error(`Codex app-server 오류: ${JSON.stringify(message.error)}`));
      } else {
        pending.resolve(message.result);
      }
      return;
    }

    if (typeof message.method === "string" && !Object.prototype.hasOwnProperty.call(message, "id")) {
      this.notifications.push(message);
      this.resolveNotificationWaiters();
      return;
    }

    if (typeof message.method === "string" && Object.prototype.hasOwnProperty.call(message, "id")) {
      this.sendRaw({
        id: message.id,
        error: {
          code: -32601,
          message: `지원하지 않는 서버 요청입니다: ${message.method}`,
          data: null
        }
      });
    }
  }

  resolveNotificationWaiters() {
    const remaining = [];
    for (const waiter of this.notificationWaiters) {
      const index = this.notifications.findIndex(waiter.predicate);
      if (index >= 0) {
        const [notification] = this.notifications.splice(index, 1);
        clearTimeout(waiter.timeout);
        waiter.resolve(notification);
      } else {
        remaining.push(waiter);
      }
    }

    this.notificationWaiters = remaining;
  }

  sendRaw(message) {
    if (this.closed || !this.child || !this.child.stdin.writable) {
      throw new Error("Codex app-server에 쓸 수 없습니다.");
    }

    this.child.stdin.write(`${JSON.stringify(message)}\n`, "utf8");
  }

  call(method, params) {
    const id = this.nextId;
    this.nextId += 1;
    if (this.debug) {
      process.stderr.write(`[codex app-server call] ${method} id=${id}\n`);
    }
    return new Promise((resolve, reject) => {
      const timeout = setTimeout(() => {
        this.pending.delete(String(id));
        reject(new Error(`Codex app-server 요청 시간이 초과되었습니다: ${method}`));
      }, this.timeoutMs);
      this.pending.set(String(id), { resolve, reject, timeout });
      try {
        this.sendRaw({ method, id, params });
      } catch (error) {
        clearTimeout(timeout);
        this.pending.delete(String(id));
        reject(error);
      }
    });
  }

  waitForNotification(predicate, timeoutMs) {
    const index = this.notifications.findIndex(predicate);
    if (index >= 0) {
      const [notification] = this.notifications.splice(index, 1);
      return Promise.resolve(notification);
    }

    return new Promise((resolve, reject) => {
      const timeout = setTimeout(() => {
        this.notificationWaiters = this.notificationWaiters.filter((waiter) => waiter.timeout !== timeout);
        reject(new Error("Codex app-server 알림 대기 시간이 초과되었습니다."));
      }, timeoutMs);
      this.notificationWaiters.push({ predicate, resolve, reject, timeout });
    });
  }
}

class CodexAppServerDecider {
  constructor(options) {
    this.options = options;
    this.client = new JsonRpcLineClient(
      options.codexCommand,
      ["app-server", "--listen", "stdio://"],
      options.timeoutMs
    );
    this.threadId = null;
  }

  async start() {
    this.client.start();
    await this.client.call("initialize", {
      clientInfo: {
        name: "spiremind-agent-daemon",
        title: "SpireMind Agent Daemon",
        version: "0.1.0"
      },
      capabilities: {
        experimentalApi: true,
        optOutNotificationMethods: []
      }
    });

    const threadStart = await this.client.call("thread/start", {
      model: this.options.model,
      cwd: process.cwd(),
      approvalPolicy: "never",
      sandbox: "read-only",
      ephemeral: true,
      environments: [],
      experimentalRawEvents: false,
      persistExtendedHistory: false,
      baseInstructions: "너는 Slay the Spire 2 자동 플레이 판단기다. 항상 입력 JSON의 legal_actions 중 하나만 선택한다.",
      developerInstructions: "응답은 JSON 객체 하나만 작성한다. 설명 문장, 마크다운, 코드블록을 출력하지 않는다."
    });

    const thread = isPlainObject(threadStart.thread) ? threadStart.thread : null;
    this.threadId = thread && typeof thread.id === "string" ? thread.id : null;
    if (!this.threadId) {
      throw new Error(`thread/start 응답에서 thread.id를 찾지 못했습니다: ${JSON.stringify(threadStart)}`);
    }
  }

  stop() {
    this.client.stop();
  }

  async decide(snapshot) {
    if (!this.threadId) {
      throw new Error("Codex app-server thread가 아직 준비되지 않았습니다.");
    }

    const prompt = JSON.stringify(buildAppServerDecisionRequest(this.options, snapshot), null, 2);
    const turnStart = await this.client.call("turn/start", {
      threadId: this.threadId,
      input: [
        {
          type: "text",
          text: prompt,
          text_elements: []
        }
      ],
      environments: [],
      model: this.options.model,
      effort: this.options.effort,
      outputSchema: decisionOutputSchema()
    });
    const turn = isPlainObject(turnStart.turn) ? turnStart.turn : null;
    const turnId = turn && typeof turn.id === "string" ? turn.id : null;
    if (!turnId) {
      throw new Error(`turn/start 응답에서 turn.id를 찾지 못했습니다: ${JSON.stringify(turnStart)}`);
    }

    let lastAgentText = "";
    const turnDeadline = Date.now() + this.options.timeoutMs;
    while (true) {
      const remainingMs = turnDeadline - Date.now();
      if (remainingMs <= 0) {
        throw new Error(`Codex app-server 턴이 ${this.options.timeoutMs}ms 안에 완료되지 않았습니다.`);
      }

      const notification = await this.client.waitForNotification((message) => {
        const params = isPlainObject(message.params) ? message.params : {};
        return params.turnId === turnId || (isPlainObject(params.turn) && params.turn.id === turnId);
      }, remainingMs);

      if (notification.method === "item/completed") {
        const params = isPlainObject(notification.params) ? notification.params : {};
        const item = isPlainObject(params.item) ? params.item : {};
        if (item.type === "agentMessage" && typeof item.text === "string") {
          lastAgentText = item.text;
        }
      }

      if (notification.method === "item/agentMessage/delta") {
        const params = isPlainObject(notification.params) ? notification.params : {};
        if (typeof params.delta === "string") {
          lastAgentText += params.delta;
        }
      }

      if (notification.method === "turn/completed") {
        break;
      }
    }

    const parsed = extractJsonObject(lastAgentText);
    if (!isPlainObject(parsed)) {
      throw new Error(`Codex app-server 최종 응답이 JSON 객체가 아닙니다: ${lastAgentText}`);
    }

    return parsed;
  }

  async report(request) {
    if (!this.threadId) {
      throw new Error("Codex app-server thread is not ready.");
    }

    const prompt = JSON.stringify(request, null, 2);
    const turnStart = await this.client.call("turn/start", {
      threadId: this.threadId,
      input: [
        {
          type: "text",
          text: prompt,
          text_elements: []
        }
      ],
      environments: [],
      model: this.options.model,
      effort: this.options.effort,
      outputSchema: postRunReportOutputSchema()
    });
    const turn = isPlainObject(turnStart.turn) ? turnStart.turn : null;
    const turnId = turn && typeof turn.id === "string" ? turn.id : null;
    if (!turnId) {
      throw new Error(`turn/start response did not include turn.id: ${JSON.stringify(turnStart)}`);
    }

    let lastAgentText = "";
    const turnDeadline = Date.now() + this.options.timeoutMs;
    while (true) {
      const remainingMs = turnDeadline - Date.now();
      if (remainingMs <= 0) {
        throw new Error(`Codex app-server post-run report did not complete within ${this.options.timeoutMs}ms.`);
      }

      const notification = await this.client.waitForNotification((message) => {
        const params = isPlainObject(message.params) ? message.params : {};
        return params.turnId === turnId || (isPlainObject(params.turn) && params.turn.id === turnId);
      }, remainingMs);

      if (notification.method === "item/completed") {
        const params = isPlainObject(notification.params) ? notification.params : {};
        const item = isPlainObject(params.item) ? params.item : {};
        if (item.type === "agentMessage" && typeof item.text === "string") {
          lastAgentText = item.text;
        }
      }

      if (notification.method === "item/agentMessage/delta") {
        const params = isPlainObject(notification.params) ? notification.params : {};
        if (typeof params.delta === "string") {
          lastAgentText += params.delta;
        }
      }

      if (notification.method === "turn/completed") {
        break;
      }
    }

    const parsed = extractJsonObject(lastAgentText);
    if (!isPlainObject(parsed)) {
      throw new Error(`Codex app-server post-run report was not a JSON object: ${lastAgentText}`);
    }

    return parsed;
  }
}

function decisionOutputSchema() {
  return {
    type: "object",
    properties: {
      selected_action_id: { type: "string" },
      reason: { type: "string" }
    },
    required: ["selected_action_id", "reason"],
    additionalProperties: false
  };
}

function postRunReportOutputSchema() {
  return {
    type: "object",
    properties: {
      death_cause: { type: "string" },
      what_i_did_well: {
        type: "array",
        items: { type: "string" }
      },
      what_i_did_poorly: {
        type: "array",
        items: { type: "string" }
      },
      key_mistakes: {
        type: "array",
        items: { type: "string" }
      },
      next_run_adjustments: {
        type: "array",
        items: { type: "string" }
      },
      adapter_observations: {
        type: "array",
        items: { type: "string" }
      }
    },
    required: [
      "death_cause",
      "what_i_did_well",
      "what_i_did_poorly",
      "key_mistakes",
      "next_run_adjustments",
      "adapter_observations"
    ],
    additionalProperties: false
  };
}

function extractJsonObject(text) {
  const trimmed = String(text || "").trim();
  const parsed = safeJsonParse(trimmed);
  if (isPlainObject(parsed)) {
    return parsed;
  }

  const first = trimmed.indexOf("{");
  const last = trimmed.lastIndexOf("}");
  if (first >= 0 && last > first) {
    const extracted = safeJsonParse(trimmed.slice(first, last + 1));
    if (isPlainObject(extracted)) {
      return extracted;
    }
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
  if (!options.appServerDecider || !runLogDir) {
    return null;
  }

  const startedAt = Date.now();
  const request = buildPostRunReportRequest(metrics, records, terminalRecord, terminalSnapshot, runIndex);
  try {
    const report = await options.appServerDecider.report(request);
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

  const decision = options.appServerDecider
    ? await options.appServerDecider.decide(snapshot)
    : await runCommandDecision(options, snapshot);
  return {
    decision,
    decisionSource: options.appServerDecider ? "llm_app_server" : "llm"
  };
}

async function runLoop(options) {
  const runLogDir = ensureRunLogDir(options.runLogDir);
  writeRunConfig(runLogDir, options);
  const metrics = createMetrics();
  const records = [];
  let completedRuns = 0;
  let lastSeenVersion = 0;
  if (options.decisionBackend === "app-server") {
    options.appServerDecider = new CodexAppServerDecider(options);
    await options.appServerDecider.start();
  } else if (options.decisionBackend !== "command") {
    throw new Error(`지원하지 않는 decision backend입니다: ${options.decisionBackend}`);
  }

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
    if (options.appServerDecider) {
      options.appServerDecider.stop();
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
