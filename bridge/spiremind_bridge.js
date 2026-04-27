#!/usr/bin/env node
"use strict";

const crypto = require("crypto");
const fs = require("fs");
const http = require("http");
const os = require("os");
const path = require("path");

const DEFAULT_HTTP_HOST = "127.0.0.1";
const DEFAULT_HTTP_PORT = 17832;
const MAX_HTTP_BODY_BYTES = 5 * 1024 * 1024;

function parseArgs(argv) {
  const options = {
    httpHost: process.env.SPIREMIND_BRIDGE_HTTP_HOST || DEFAULT_HTTP_HOST,
    httpPort: parseIntEnv(process.env.SPIREMIND_BRIDGE_HTTP_PORT, DEFAULT_HTTP_PORT),
    runRoot: process.env.SPIREMIND_BRIDGE_RUN_ROOT || "",
    help: false
  };

  for (let index = 0; index < argv.length; index += 1) {
    const token = argv[index];
    if (token === "--help" || token === "-h") {
      options.help = true;
      continue;
    }

    if (token === "--http-host" && index + 1 < argv.length) {
      options.httpHost = argv[index + 1];
      index += 1;
      continue;
    }

    if (token.startsWith("--http-host=")) {
      options.httpHost = token.slice("--http-host=".length);
      continue;
    }

    if (token === "--http-port" && index + 1 < argv.length) {
      options.httpPort = parseIntEnv(argv[index + 1], DEFAULT_HTTP_PORT);
      index += 1;
      continue;
    }

    if (token.startsWith("--http-port=")) {
      options.httpPort = parseIntEnv(token.slice("--http-port=".length), DEFAULT_HTTP_PORT);
      continue;
    }

    if (token === "--run-root" && index + 1 < argv.length) {
      options.runRoot = argv[index + 1];
      index += 1;
      continue;
    }

    if (token.startsWith("--run-root=")) {
      options.runRoot = token.slice("--run-root=".length);
    }
  }

  return options;
}

function parseIntEnv(value, fallbackValue) {
  if (value === undefined || value === null || String(value).trim() === "") {
    return fallbackValue;
  }

  const parsedValue = Number.parseInt(String(value), 10);
  if (!Number.isFinite(parsedValue) || parsedValue <= 0) {
    return fallbackValue;
  }

  return parsedValue;
}

function ensureDirectoryExists(directoryPath) {
  fs.mkdirSync(directoryPath, { recursive: true });
}

function formatTimestampForFile(dateValue) {
  const year = String(dateValue.getFullYear());
  const month = String(dateValue.getMonth() + 1).padStart(2, "0");
  const day = String(dateValue.getDate()).padStart(2, "0");
  const hours = String(dateValue.getHours()).padStart(2, "0");
  const minutes = String(dateValue.getMinutes()).padStart(2, "0");
  const seconds = String(dateValue.getSeconds()).padStart(2, "0");
  const milliseconds = String(dateValue.getMilliseconds()).padStart(3, "0");

  return `${year}${month}${day}_${hours}${minutes}${seconds}_${milliseconds}`;
}

function getAppDataRoot() {
  if (typeof process.env.APPDATA === "string" && process.env.APPDATA.trim() !== "") {
    return process.env.APPDATA;
  }

  return path.join(os.homedir(), "AppData", "Roaming");
}

function makeRunDirectory(runRoot) {
  ensureDirectoryExists(runRoot);

  const timestamp = formatTimestampForFile(new Date());
  const suffix = crypto.randomBytes(3).toString("hex");
  const runDirectory = path.join(runRoot, `${timestamp}_${process.pid}_${suffix}`);
  ensureDirectoryExists(runDirectory);

  return runDirectory;
}

function safeJsonParse(text) {
  try {
    return JSON.parse(text);
  } catch (error) {
    return null;
  }
}

function writeJsonFile(filePath, value) {
  fs.writeFileSync(filePath, `${JSON.stringify(value, null, 2)}\n`, { encoding: "utf8" });
}

function appendJsonLine(filePath, value) {
  fs.appendFileSync(filePath, `${JSON.stringify(value)}\n`, { encoding: "utf8" });
}

function toIsoString(dateValue) {
  return dateValue.toISOString();
}

function isPlainObject(value) {
  return typeof value === "object" && value !== null && !Array.isArray(value);
}

function getStateId(stateObject) {
  if (!isPlainObject(stateObject)) {
    return "";
  }

  const candidate = stateObject.state_id || stateObject.stateId || stateObject.id;
  if (typeof candidate !== "string") {
    return "";
  }

  return candidate.trim();
}

function getLegalActionIds(stateObject) {
  const legalActions = isPlainObject(stateObject) ? stateObject.legal_actions : null;
  if (!Array.isArray(legalActions)) {
    return [];
  }

  const actionIds = [];
  for (let index = 0; index < legalActions.length; index += 1) {
    const legalAction = legalActions[index];
    if (typeof legalAction === "string") {
      const trimmedActionId = legalAction.trim();
      if (trimmedActionId !== "") {
        actionIds.push(trimmedActionId);
      }
      continue;
    }

    if (isPlainObject(legalAction)) {
      const candidate = legalAction.action_id || legalAction.actionId;
      if (typeof candidate === "string" && candidate.trim() !== "") {
        actionIds.push(candidate.trim());
      }
    }
  }

  return actionIds;
}

function getLegalActionById(stateObject, actionId) {
  const legalActions = isPlainObject(stateObject) ? stateObject.legal_actions : null;
  if (!Array.isArray(legalActions) || typeof actionId !== "string" || actionId.trim() === "") {
    return null;
  }

  const trimmedActionId = actionId.trim();
  for (let index = 0; index < legalActions.length; index += 1) {
    const legalAction = legalActions[index];
    if (!isPlainObject(legalAction)) {
      continue;
    }

    const candidate = legalAction.action_id || legalAction.actionId;
    if (typeof candidate === "string" && candidate.trim() === trimmedActionId) {
      return legalAction;
    }
  }

  return null;
}

function getLatestStateSummary(stateEnvelope) {
  if (!stateEnvelope) {
    return null;
  }

  return {
    state_version: stateEnvelope.state_version,
    received_at: stateEnvelope.received_at,
    state_id: stateEnvelope.state_id,
    legal_action_ids: stateEnvelope.legal_action_ids,
    state: stateEnvelope.state
  };
}

function createServerState(runDirectory, httpHost, httpPort) {
  const logFilePath = path.join(runDirectory, "bridge.log");
  const eventsFilePath = path.join(runDirectory, "events.jsonl");
  const currentStateFilePath = path.join(runDirectory, "current_state.json");
  const latestActionFilePath = path.join(runDirectory, "latest_action.json");
  const runInfoFilePath = path.join(runDirectory, "run_info.json");

  const state = {
    runDirectory,
    logFilePath,
    eventsFilePath,
    currentStateFilePath,
    latestActionFilePath,
    runInfoFilePath,
    httpHost,
    httpPort,
    startedAt: toIsoString(new Date()),
    stateVersion: 0,
    currentState: null,
    currentStateReceivedAt: null,
    currentStateId: "",
    legalActionIds: [],
    latestAction: null,
    httpServer: null
  };

  writeJsonFile(runInfoFilePath, {
    started_at: state.startedAt,
    process_id: process.pid,
    http_host: httpHost,
    http_port: httpPort,
    run_directory: runDirectory
  });

  appendEvent(state, "startup", {
    started_at: state.startedAt,
    process_id: process.pid,
    http_host: httpHost,
    http_port: httpPort
  });

  return state;
}

function appendEvent(state, eventType, details) {
  const entry = Object.assign(
    {
      timestamp: toIsoString(new Date()),
      event_type: eventType
    },
    details || {}
  );

  appendJsonLine(state.eventsFilePath, entry);
  fs.appendFileSync(state.logFilePath, `[${entry.timestamp}] ${eventType} ${JSON.stringify(details || {})}\n`, {
    encoding: "utf8"
  });
}

function buildStateEnvelope(state) {
  return {
    state_version: state.stateVersion,
    received_at: state.currentStateReceivedAt,
    state_id: state.currentStateId,
    legal_action_ids: state.legalActionIds,
    state: state.currentState
  };
}

function updateCurrentState(state, nextState) {
  state.stateVersion += 1;
  state.currentState = nextState;
  state.currentStateReceivedAt = toIsoString(new Date());
  state.currentStateId = getStateId(nextState);
  state.legalActionIds = getLegalActionIds(nextState);

  const envelope = buildStateEnvelope(state);
  writeJsonFile(state.currentStateFilePath, envelope);
  appendEvent(state, "state_received", {
    state_version: envelope.state_version,
    received_at: envelope.received_at,
    state_id: envelope.state_id,
    legal_action_count: envelope.legal_action_ids.length
  });
  return envelope;
}

function setLatestAction(state, actionObject) {
  state.latestAction = actionObject;
  writeJsonFile(state.latestActionFilePath, actionObject);
  appendEvent(state, "action_submitted", {
    submission_id: actionObject.submission_id,
    state_version: actionObject.state_version,
    state_id: actionObject.state_id,
    selected_action_id: actionObject.selected_action_id,
    valid: actionObject.valid
  });
}

function updateLatestAction(state, eventType, details) {
  if (state.latestAction) {
    writeJsonFile(state.latestActionFilePath, state.latestAction);
  }

  appendEvent(state, eventType, details);
}

function createActionSubmission(state, actionArguments) {
  const selectedActionId = typeof actionArguments.selected_action_id === "string" ? actionArguments.selected_action_id.trim() : "";
  const expectedStateVersion = normalizeStateVersion(actionArguments.expected_state_version, state.stateVersion);
  const source = typeof actionArguments.source === "string" ? actionArguments.source.trim() : "";
  const note = typeof actionArguments.note === "string" ? actionArguments.note.trim() : "";
  const legalActionIds = state.legalActionIds.slice();
  const stateEnvelope = buildStateEnvelope(state);
  const hasExpectedStateVersion = Object.prototype.hasOwnProperty.call(actionArguments, "expected_state_version");
  const stateVersionMatches = !hasExpectedStateVersion || expectedStateVersion === stateEnvelope.state_version;
  const actionIdExists = selectedActionId !== "" && legalActionIds.includes(selectedActionId);
  const isValid = actionIdExists && stateVersionMatches;

  return {
    submission_id: crypto.randomUUID(),
    submitted_at: toIsoString(new Date()),
    state_version: stateEnvelope.state_version,
    expected_state_version: expectedStateVersion,
    state_version_matches: stateVersionMatches,
    state_id: stateEnvelope.state_id,
    selected_action_id: selectedActionId,
    legal_action_ids: legalActionIds,
    valid: isValid,
    execution_status: isValid ? "pending" : "invalid",
    claim_token: null,
    claimed_at: null,
    claimed_by: null,
    result: null,
    result_reported_at: null,
    source: source || null,
    note: note || null,
    reason: isValid
      ? "최신 state_version의 legal_actions 안에 있습니다."
      : "selected_action_id가 없거나, expected_state_version이 최신 state_version과 다릅니다."
  };
}

function getSupportedActionTypes(claimArguments) {
  if (!Array.isArray(claimArguments.supported_action_types)) {
    return [];
  }

  return claimArguments.supported_action_types
    .filter((value) => typeof value === "string")
    .map((value) => value.trim())
    .filter((value) => value !== "");
}

function createActionClaim(state, claimArguments) {
  const latestAction = state.latestAction;
  const executorId = typeof claimArguments.executor_id === "string" ? claimArguments.executor_id.trim() : "";
  const observedStateId = typeof claimArguments.observed_state_id === "string" ? claimArguments.observed_state_id.trim() : "";
  const observedStateVersion = normalizeStateVersion(claimArguments.observed_state_version, -1);
  const supportedActionTypes = getSupportedActionTypes(claimArguments);

  if (!latestAction || latestAction.valid !== true || latestAction.execution_status === "invalid") {
    return {
      ok: true,
      status: "empty",
      reason: "실행할 유효 행동이 없습니다."
    };
  }

  if (latestAction.result !== null || latestAction.execution_status === "applied" || latestAction.execution_status === "failed") {
    return {
      ok: true,
      status: "empty",
      reason: "이미 결과가 보고된 행동입니다."
    };
  }

  if (latestAction.execution_status === "claimed" && latestAction.claim_token) {
    return {
      ok: true,
      status: "empty",
      reason: "이미 다른 claim이 처리 중입니다."
    };
  }

  const stateMatches = latestAction.state_id === state.currentStateId
    && latestAction.state_version === state.stateVersion
    && latestAction.state_id === observedStateId
    && latestAction.state_version === observedStateVersion;

  if (!stateMatches) {
    latestAction.execution_status = "stale";
    latestAction.result = "stale";
    latestAction.result_reported_at = toIsoString(new Date());
    latestAction.result_note = "claim 시점의 상태가 제출 시점 상태와 다릅니다.";
    updateLatestAction(state, "action_marked_stale", {
      submission_id: latestAction.submission_id,
      state_id: latestAction.state_id,
      state_version: latestAction.state_version,
      observed_state_id: observedStateId,
      observed_state_version: observedStateVersion
    });

    return {
      ok: true,
      status: "stale",
      reason: "행동의 상태와 모드가 보고 있는 상태가 다릅니다.",
      action: latestAction
    };
  }

  const legalAction = getLegalActionById(state.currentState, latestAction.selected_action_id);
  if (!legalAction) {
    latestAction.execution_status = "stale";
    latestAction.result = "stale";
    latestAction.result_reported_at = toIsoString(new Date());
    latestAction.result_note = "현재 legal_actions에서 selected_action_id를 다시 찾지 못했습니다.";
    updateLatestAction(state, "action_marked_stale", {
      submission_id: latestAction.submission_id,
      selected_action_id: latestAction.selected_action_id
    });

    return {
      ok: true,
      status: "stale",
      reason: "현재 legal_actions에서 행동을 다시 찾지 못했습니다.",
      action: latestAction
    };
  }

  const actionType = typeof legalAction.type === "string" ? legalAction.type.trim() : "";
  if (supportedActionTypes.length > 0 && !supportedActionTypes.includes(actionType)) {
    latestAction.execution_status = "unsupported";
    latestAction.result = "unsupported";
    latestAction.result_reported_at = toIsoString(new Date());
    latestAction.result_note = `실행자가 ${actionType} 행동을 지원하지 않습니다.`;
    updateLatestAction(state, "action_claim_rejected", {
      submission_id: latestAction.submission_id,
      selected_action_id: latestAction.selected_action_id,
      action_type: actionType,
      reason: "unsupported"
    });

    return {
      ok: true,
      status: "unsupported",
      reason: "실행자가 지원하지 않는 행동 타입입니다.",
      action: latestAction
    };
  }

  const claimedAt = toIsoString(new Date());
  const claimToken = crypto.randomUUID();
  latestAction.execution_status = "claimed";
  latestAction.claim_token = claimToken;
  latestAction.claimed_at = claimedAt;
  latestAction.claimed_by = executorId || null;
  latestAction.action_type = actionType || null;
  latestAction.action = legalAction;
  updateLatestAction(state, "action_claimed", {
    submission_id: latestAction.submission_id,
    claim_token: claimToken,
    executor_id: executorId || null,
    state_id: latestAction.state_id,
    state_version: latestAction.state_version,
    selected_action_id: latestAction.selected_action_id,
    action_type: actionType || null
  });

  return {
    ok: true,
    status: "claimed",
    claim_token: claimToken,
    action: latestAction
  };
}

function createActionResult(state, resultArguments) {
  const latestAction = state.latestAction;
  const submissionId = typeof resultArguments.submission_id === "string" ? resultArguments.submission_id.trim() : "";
  const claimToken = typeof resultArguments.claim_token === "string" ? resultArguments.claim_token.trim() : "";
  const executorId = typeof resultArguments.executor_id === "string" ? resultArguments.executor_id.trim() : "";
  const result = typeof resultArguments.result === "string" ? resultArguments.result.trim() : "";
  const note = typeof resultArguments.note === "string" ? resultArguments.note.trim() : "";
  const observedStateId = typeof resultArguments.observed_state_id === "string" ? resultArguments.observed_state_id.trim() : "";
  const observedStateVersion = normalizeStateVersion(resultArguments.observed_state_version, -1);
  const allowedResults = new Set(["applied", "stale", "unsupported", "failed", "ignored_duplicate"]);

  if (!latestAction || latestAction.submission_id !== submissionId) {
    return {
      ok: false,
      status: "rejected",
      error: "현재 제출 행동과 submission_id가 맞지 않습니다."
    };
  }

  if (latestAction.claim_token !== claimToken) {
    return {
      ok: false,
      status: "rejected",
      error: "claim_token이 맞지 않습니다."
    };
  }

  if (!allowedResults.has(result)) {
    return {
      ok: false,
      status: "rejected",
      error: "지원하지 않는 실행 결과입니다."
    };
  }

  const reportedAt = toIsoString(new Date());
  latestAction.execution_status = result;
  latestAction.result = result;
  latestAction.result_reported_at = reportedAt;
  latestAction.result_executor_id = executorId || null;
  latestAction.result_observed_state_id = observedStateId || null;
  latestAction.result_observed_state_version = observedStateVersion >= 0 ? observedStateVersion : null;
  latestAction.result_note = note || null;
  updateLatestAction(state, "action_result_reported", {
    submission_id: latestAction.submission_id,
    claim_token: latestAction.claim_token,
    executor_id: executorId || null,
    result,
    observed_state_id: observedStateId || null,
    observed_state_version: observedStateVersion >= 0 ? observedStateVersion : null
  });

  return {
    ok: true,
    status: result,
    latest_action: latestAction
  };
}

function getCurrentStatePayload(state) {
  return {
    state_version: state.stateVersion,
    received_at: state.currentStateReceivedAt,
    state_id: state.currentStateId,
    legal_action_ids: state.legalActionIds,
    state: state.currentState,
    latest_action: state.latestAction
  };
}

function getCurrentStateHttpPayload(state) {
  return {
    ok: true,
    status: state.currentState === null ? "empty" : "ready",
    ...getCurrentStatePayload(state)
  };
}

function normalizeStateVersion(value, defaultValue) {
  const parsedValue = Number(value);
  if (!Number.isFinite(parsedValue) || parsedValue < 0) {
    return defaultValue;
  }

  return Math.trunc(parsedValue);
}

function readJsonRequestBody(req, maxBytes) {
  return new Promise((resolve, reject) => {
    let totalBytes = 0;
    const chunks = [];

    req.on("data", (chunk) => {
      totalBytes += chunk.length;
      if (totalBytes > maxBytes) {
        reject(new Error("HTTP 요청 본문이 너무 큽니다."));
        req.destroy();
        return;
      }

      chunks.push(chunk);
    });

    req.on("end", () => {
      const rawBody = Buffer.concat(chunks).toString("utf8");
      const parsedBody = safeJsonParse(rawBody);
      if (parsedBody === null) {
        reject(new Error("HTTP 요청 본문이 JSON이 아닙니다."));
        return;
      }

      resolve(parsedBody);
    });

    req.on("error", (error) => reject(error));
  });
}

function sendJson(res, statusCode, value) {
  const payload = Buffer.from(`${JSON.stringify(value, null, 2)}\n`, "utf8");
  res.statusCode = statusCode;
  res.setHeader("Content-Type", "application/json; charset=utf-8");
  res.setHeader("Content-Length", String(payload.length));
  res.end(payload);
}

function startHttpServer(state) {
  const server = http.createServer(async (req, res) => {
    const requestUrl = new URL(req.url || "/", `http://${state.httpHost}:${state.httpPort}`);

    try {
      if (req.method === "GET" && requestUrl.pathname === "/health") {
        sendJson(res, 200, {
          ok: true,
          status: "running",
          uptime_ms: Math.trunc(process.uptime() * 1000),
          http_host: state.httpHost,
          http_port: state.httpPort,
          run_directory: state.runDirectory,
          state_version: state.stateVersion,
          has_state: state.currentState !== null,
          current_state_id: state.currentStateId,
          latest_action_submitted_at: state.latestAction ? state.latestAction.submitted_at : null
        });
        return;
      }

      if (req.method === "GET" && requestUrl.pathname === "/action/latest") {
        sendJson(res, 200, {
          ok: true,
          latest_action: state.latestAction
        });
        return;
      }

      if (req.method === "GET" && requestUrl.pathname === "/state/current") {
        sendJson(res, 200, getCurrentStateHttpPayload(state));
        return;
      }

      if (req.method === "POST" && requestUrl.pathname === "/state") {
        const body = await readJsonRequestBody(req, MAX_HTTP_BODY_BYTES);
        if (!isPlainObject(body)) {
          sendJson(res, 400, {
            ok: false,
            error: "전투 상태는 JSON 객체여야 합니다."
          });
          return;
        }

        const envelope = updateCurrentState(state, body);
        sendJson(res, 200, {
          ok: true,
          state_version: envelope.state_version,
          received_at: envelope.received_at,
          state_id: envelope.state_id,
          legal_action_count: envelope.legal_action_ids.length
        });
        return;
      }

      if (req.method === "POST" && requestUrl.pathname === "/action/submit") {
        const body = await readJsonRequestBody(req, MAX_HTTP_BODY_BYTES);
        if (!isPlainObject(body)) {
          sendJson(res, 400, {
            ok: false,
            error: "행동 제출 본문은 JSON 객체여야 합니다."
          });
          return;
        }

        const submission = createActionSubmission(state, body);
        setLatestAction(state, submission);
        sendJson(res, 200, {
          ok: true,
          latest_action: submission
        });
        return;
      }

      if (req.method === "POST" && requestUrl.pathname === "/action/claim") {
        const body = await readJsonRequestBody(req, MAX_HTTP_BODY_BYTES);
        if (!isPlainObject(body)) {
          sendJson(res, 400, {
            ok: false,
            error: "행동 claim 본문은 JSON 객체여야 합니다."
          });
          return;
        }

        const claim = createActionClaim(state, body);
        sendJson(res, 200, claim);
        return;
      }

      if (req.method === "POST" && requestUrl.pathname === "/action/result") {
        const body = await readJsonRequestBody(req, MAX_HTTP_BODY_BYTES);
        if (!isPlainObject(body)) {
          sendJson(res, 400, {
            ok: false,
            error: "행동 결과 본문은 JSON 객체여야 합니다."
          });
          return;
        }

        const result = createActionResult(state, body);
        sendJson(res, result.ok ? 200 : 400, result);
        return;
      }

      sendJson(res, 404, {
        ok: false,
        error: "지원하지 않는 경로입니다."
      });
    } catch (error) {
      appendEvent(state, "http_error", {
        path: requestUrl.pathname,
        method: req.method,
        message: error instanceof Error ? error.message : String(error)
      });

      sendJson(res, 400, {
        ok: false,
        error: error instanceof Error ? error.message : "요청을 처리하지 못했습니다."
      });
    }
  });

  server.on("error", (error) => {
    appendEvent(state, "http_server_error", {
      message: error instanceof Error ? error.message : String(error)
    });
    process.stderr.write(`HTTP 서버를 시작하지 못했습니다: ${error.message}\n`);
    process.exit(1);
  });

  server.listen(state.httpPort, state.httpHost);
  state.httpServer = server;
}

function printUsage() {
  process.stdout.write([
    "사용법:",
    "  node bridge/spiremind_bridge.js [--http-host 127.0.0.1] [--http-port 17832] [--run-root <경로>]",
    "",
    "설명:",
    "  - 로컬 HTTP 서버만 실행한다.",
    "  - MCP 처리는 bridge/spiremind_mcp_proxy.js가 담당한다.",
    "  - 게임과 테스트가 전투 상태를 보내는 데 쓴다.",
    "  - /state, /state/current, /action/submit, /action/claim, /action/result, /action/latest, /health를 지원한다."
  ].join("\n"));
}

const options = parseArgs(process.argv.slice(2));
if (options.help) {
  printUsage();
  process.exit(0);
}

const appDataRoot = getAppDataRoot();
const runRoot = options.runRoot.trim() === ""
  ? path.join(appDataRoot, "SlayTheSpire2", "SpireMind", "bridge_runs")
  : path.resolve(options.runRoot);
const runDirectory = makeRunDirectory(runRoot);
const serverState = createServerState(runDirectory, options.httpHost, options.httpPort);

startHttpServer(serverState);

process.on("SIGINT", () => {
  appendEvent(serverState, "shutdown", { signal: "SIGINT" });
  if (serverState.httpServer) {
    serverState.httpServer.close(() => process.exit(0));
    return;
  }

  process.exit(0);
});

process.on("SIGTERM", () => {
  appendEvent(serverState, "shutdown", { signal: "SIGTERM" });
  if (serverState.httpServer) {
    serverState.httpServer.close(() => process.exit(0));
    return;
  }

  process.exit(0);
});
