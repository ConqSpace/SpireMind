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
const MCP_PROTOCOL_VERSION = "2024-11-05";

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
    waiters: [],
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
  notifyWaiters(state);

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

function notifyWaiters(state) {
  if (state.waiters.length === 0) {
    return;
  }

  const readyWaiters = [];
  const remainingWaiters = [];

  for (let index = 0; index < state.waiters.length; index += 1) {
    const waiter = state.waiters[index];
    if (state.stateVersion > waiter.lastSeenVersion) {
      readyWaiters.push(waiter);
    } else {
      remainingWaiters.push(waiter);
    }
  }

  state.waiters = remainingWaiters;

  for (let index = 0; index < readyWaiters.length; index += 1) {
    const waiter = readyWaiters[index];
    clearTimeout(waiter.timeoutHandle);
    waiter.resolve({
      status: "ready",
      ...getCurrentStatePayload(state)
    });
  }
}

function waitForDecisionRequest(state, lastSeenVersion, timeoutMs) {
  if (state.stateVersion > lastSeenVersion) {
    return Promise.resolve({
      status: "ready",
      ...getCurrentStatePayload(state)
    });
  }

  return new Promise((resolve) => {
    const timeoutHandle = setTimeout(() => {
      state.waiters = state.waiters.filter((waiter) => waiter.timeoutHandle !== timeoutHandle);
      resolve({
        status: "timeout",
        state_version: state.stateVersion,
        has_state: state.currentState !== null,
        received_at: state.currentStateReceivedAt,
        state_id: state.currentStateId,
        legal_action_ids: state.legalActionIds,
        latest_action: state.latestAction,
        timeout_ms: timeoutMs
      });
    }, timeoutMs);

    state.waiters.push({
      lastSeenVersion,
      timeoutHandle,
      resolve
    });
  });
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

function normalizeTimeoutMs(value, defaultValue) {
  const parsedValue = Number(value);
  if (!Number.isFinite(parsedValue) || parsedValue <= 0) {
    return defaultValue;
  }

  return Math.min(Math.trunc(parsedValue), 10 * 60 * 1000);
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

function createJsonRpcTransport() {
  let buffer = Buffer.alloc(0);

  function writeMessage(messageObject) {
    process.stdout.write(`${JSON.stringify(messageObject)}\n`);
  }

  async function handleMessage(messageObject) {
    if (!isPlainObject(messageObject)) {
      return;
    }

    const requestId = Object.prototype.hasOwnProperty.call(messageObject, "id") ? messageObject.id : undefined;
    const methodName = typeof messageObject.method === "string" ? messageObject.method : "";
    const paramsObject = isPlainObject(messageObject.params) ? messageObject.params : {};

    try {
      if (methodName === "initialize") {
        appendEvent(serverState, "mcp_initialize", {
          client_name: isPlainObject(paramsObject.clientInfo) && typeof paramsObject.clientInfo.name === "string" ? paramsObject.clientInfo.name : "",
          client_version: isPlainObject(paramsObject.clientInfo) && typeof paramsObject.clientInfo.version === "string" ? paramsObject.clientInfo.version : "",
          protocol_version: typeof paramsObject.protocolVersion === "string" ? paramsObject.protocolVersion : ""
        });

        if (requestId !== undefined) {
          writeMessage({
            jsonrpc: "2.0",
            id: requestId,
            result: {
              protocolVersion: MCP_PROTOCOL_VERSION,
              serverInfo: {
                name: "spiremind-bridge",
                version: "r4"
              },
              capabilities: {
                tools: {}
              }
            }
          });
        }
        return;
      }

      if (methodName === "notifications/initialized") {
        appendEvent(serverState, "mcp_initialized", {});
        return;
      }

      if (methodName === "tools/list") {
        if (requestId === undefined) {
          return;
        }

        writeMessage({
          jsonrpc: "2.0",
          id: requestId,
          result: {
            tools: [
              {
                name: "wait_for_decision_request",
                description: "새 전투 상태나 판단 요청이 들어올 때까지 기다린다. 지정한 버전보다 새 상태가 있으면 바로 돌려준다.",
                inputSchema: {
                  type: "object",
                  additionalProperties: false,
                  properties: {
                    last_seen_state_version: {
                      type: "integer",
                      minimum: 0,
                      description: "이 버전보다 새 상태가 들어오면 즉시 반환한다."
                    },
                    timeout_ms: {
                      type: "integer",
                      minimum: 1,
                      maximum: 600000,
                      description: "기다릴 최대 시간이다. 기본값은 30000이다."
                    }
                  }
                }
              },
              {
                name: "get_current_state",
                description: "현재 브리지에 들어온 전투 상태와 최근 제출 행동을 돌려준다.",
                inputSchema: {
                  type: "object",
                  additionalProperties: false,
                  properties: {}
                }
              },
              {
                name: "submit_action",
                description: "최근 전투 상태의 legal_actions 안에 있는 행동을 제출하고 로그에 남긴다.",
                inputSchema: {
                  type: "object",
                  additionalProperties: false,
                  required: ["selected_action_id"],
                  properties: {
                    selected_action_id: {
                      type: "string",
                      description: "선택한 행동의 action_id다."
                    },
                    source: {
                      type: "string",
                      description: "행동 제출 주체를 적는 선택 항목이다."
                    },
                    note: {
                      type: "string",
                      description: "추가 메모를 남길 수 있다."
                    },
                    expected_state_version: {
                      type: "integer",
                      minimum: 0,
                      description: "선택 사항이다. 넣으면 현재 상태 버전과 대조할 수 있다."
                    }
                  }
                }
              }
            ]
          }
        });
        return;
      }

      if (methodName === "tools/call") {
        const toolName = typeof paramsObject.name === "string" ? paramsObject.name : "";
        const toolArguments = isPlainObject(paramsObject.arguments) ? paramsObject.arguments : {};

        if (toolName === "wait_for_decision_request") {
          const lastSeenVersion = normalizeStateVersion(toolArguments.last_seen_state_version, 0);
          const timeoutMs = normalizeTimeoutMs(toolArguments.timeout_ms, 30000);
          const result = await waitForDecisionRequest(serverState, lastSeenVersion, timeoutMs);

          if (requestId !== undefined) {
            writeMessage({
              jsonrpc: "2.0",
              id: requestId,
              result: {
                content: [
                  {
                    type: "text",
                    text: `${JSON.stringify(result, null, 2)}\n`
                  }
                ]
              }
            });
          }
          return;
        }

        if (toolName === "get_current_state") {
          const result = {
            status: serverState.currentState === null ? "empty" : "ready",
            ...getCurrentStatePayload(serverState)
          };

          if (requestId !== undefined) {
            writeMessage({
              jsonrpc: "2.0",
              id: requestId,
              result: {
                content: [
                  {
                    type: "text",
                    text: `${JSON.stringify(result, null, 2)}\n`
                  }
                ]
              }
            });
          }
          return;
        }

        if (toolName === "submit_action") {
          const selectedActionId = typeof toolArguments.selected_action_id === "string" ? toolArguments.selected_action_id.trim() : "";
          const expectedStateVersion = normalizeStateVersion(toolArguments.expected_state_version, serverState.stateVersion);
          const source = typeof toolArguments.source === "string" ? toolArguments.source.trim() : "";
          const note = typeof toolArguments.note === "string" ? toolArguments.note.trim() : "";
          const legalActionIds = serverState.legalActionIds.slice();
          const stateEnvelope = buildStateEnvelope(serverState);
          const hasExpectedStateVersion = Object.prototype.hasOwnProperty.call(toolArguments, "expected_state_version");
          const stateVersionMatches = !hasExpectedStateVersion || expectedStateVersion === stateEnvelope.state_version;
          const actionIdExists = selectedActionId !== "" && legalActionIds.includes(selectedActionId);
          const isValid = actionIdExists && stateVersionMatches;
          const submission = {
            submission_id: crypto.randomUUID(),
            submitted_at: toIsoString(new Date()),
            state_version: stateEnvelope.state_version,
            expected_state_version: expectedStateVersion,
            state_version_matches: stateVersionMatches,
            state_id: stateEnvelope.state_id,
            selected_action_id: selectedActionId,
            legal_action_ids: legalActionIds,
            valid: isValid,
            source: source || null,
            note: note || null,
            reason: isValid
              ? "최신 state_version의 legal_actions 안에 있습니다."
              : "selected_action_id가 없거나, expected_state_version이 최신 state_version과 다릅니다."
          };

          setLatestAction(serverState, submission);

          if (requestId !== undefined) {
            writeMessage({
              jsonrpc: "2.0",
              id: requestId,
              result: {
                content: [
                  {
                    type: "text",
                    text: `${JSON.stringify(submission, null, 2)}\n`
                  }
                ]
              }
            });
          }
          return;
        }

        if (requestId !== undefined) {
          writeMessage({
            jsonrpc: "2.0",
            id: requestId,
            error: {
              code: -32601,
              message: `알 수 없는 도구입니다: ${toolName}`
            }
          });
        }
        return;
      }

      if (methodName === "shutdown") {
        if (requestId !== undefined) {
          writeMessage({
            jsonrpc: "2.0",
            id: requestId,
            result: null
          });
        }
        return;
      }

      if (methodName === "exit") {
        process.exit(0);
      }

      if (requestId !== undefined) {
        writeMessage({
          jsonrpc: "2.0",
          id: requestId,
          error: {
            code: -32601,
            message: `알 수 없는 메서드입니다: ${methodName}`
          }
        });
      }
    } catch (error) {
      appendEvent(serverState, "mcp_error", {
        method: methodName,
        message: error instanceof Error ? error.message : String(error)
      });

      if (requestId !== undefined) {
        writeMessage({
          jsonrpc: "2.0",
          id: requestId,
          error: {
            code: -32000,
            message: error instanceof Error ? error.message : "요청 처리 중 오류가 발생했습니다."
          }
        });
      }
    }
  }

  function parseBuffer() {
    while (true) {
      const headerSeparatorIndex = buffer.indexOf("\r\n\r\n");
      if (headerSeparatorIndex !== -1) {
        const headerText = buffer.slice(0, headerSeparatorIndex).toString("utf8");
        const headerLines = headerText.split(/\r\n/);
        const headerMap = {};

        for (let index = 0; index < headerLines.length; index += 1) {
          const line = headerLines[index];
          const colonIndex = line.indexOf(":");
          if (colonIndex === -1) {
            continue;
          }

          const headerName = line.slice(0, colonIndex).trim().toLowerCase();
          const headerValue = line.slice(colonIndex + 1).trim();
          headerMap[headerName] = headerValue;
        }

        const contentLength = Number.parseInt(headerMap["content-length"] || "", 10);
        if (Number.isFinite(contentLength) && contentLength >= 0) {
          const bodyStartIndex = headerSeparatorIndex + 4;
          const bodyEndIndex = bodyStartIndex + contentLength;
          if (buffer.length < bodyEndIndex) {
            return;
          }

          const bodyText = buffer.slice(bodyStartIndex, bodyEndIndex).toString("utf8");
          buffer = buffer.slice(bodyEndIndex);

          const messageObject = safeJsonParse(bodyText);
          if (messageObject !== null) {
            void handleMessage(messageObject);
          }
          continue;
        }

        buffer = buffer.slice(headerSeparatorIndex + 4);
        continue;
      }

      const newlineIndex = buffer.indexOf("\n");
      if (newlineIndex === -1) {
        return;
      }

      const lineText = buffer.slice(0, newlineIndex).toString("utf8").trim();
      buffer = buffer.slice(newlineIndex + 1);

      if (lineText === "") {
        continue;
      }

      const messageObject = safeJsonParse(lineText);
      if (messageObject !== null) {
        void handleMessage(messageObject);
      }
    }
  }

  process.stdin.on("data", (chunk) => {
    buffer = Buffer.concat([buffer, chunk]);
    parseBuffer();
  });

  process.stdin.on("end", () => {
    process.exit(0);
  });
}

function printUsage() {
  process.stdout.write([
    "사용법:",
    "  node bridge/spiremind_bridge.js [--http-host 127.0.0.1] [--http-port 17832] [--run-root <경로>]",
    "",
    "설명:",
    "  - stdin/stdout에서는 MCP(JSON-RPC)를 처리한다.",
    "  - 로컬 HTTP 서버는 게임과 테스트가 전투 상태를 보내는 데 쓴다.",
    "  - /state, /action/latest, /health를 지원한다."
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
createJsonRpcTransport();

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
