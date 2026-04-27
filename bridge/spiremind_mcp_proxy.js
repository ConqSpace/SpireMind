#!/usr/bin/env node
"use strict";

const http = require("http");
const https = require("https");

const DEFAULT_BRIDGE_URL = "http://127.0.0.1:17832";
const MCP_PROTOCOL_VERSION = "2024-11-05";
const DEFAULT_TIMEOUT_MS = 30000;
const MAX_TIMEOUT_MS = 10 * 60 * 1000;
const POLL_INTERVAL_MS = 1000;

function parseArgs(argv) {
  const options = {
    bridgeUrl: process.env.SPIREMIND_BRIDGE_URL || DEFAULT_BRIDGE_URL,
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
    }
  }

  return options;
}

function isPlainObject(value) {
  return typeof value === "object" && value !== null && !Array.isArray(value);
}

function safeJsonParse(text) {
  try {
    return JSON.parse(text);
  } catch (error) {
    return null;
  }
}

function normalizeTimeoutMs(value, defaultValue) {
  const parsedValue = Number(value);
  if (!Number.isFinite(parsedValue) || parsedValue <= 0) {
    return defaultValue;
  }

  return Math.min(Math.trunc(parsedValue), MAX_TIMEOUT_MS);
}

function normalizeStateVersion(value, defaultValue) {
  const parsedValue = Number(value);
  if (!Number.isFinite(parsedValue) || parsedValue < 0) {
    return defaultValue;
  }

  return Math.trunc(parsedValue);
}

function sleep(ms) {
  return new Promise((resolve) => setTimeout(resolve, ms));
}

function appendPath(baseUrlString, pathname) {
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

        response.on("data", (chunk) => {
          chunks.push(chunk);
        });

        response.on("end", () => {
          const rawBody = Buffer.concat(chunks).toString("utf8");
          const trimmedBody = rawBody.trim();
          const parsedBody = trimmedBody === "" ? null : safeJsonParse(rawBody);

          if (trimmedBody !== "" && parsedBody === null) {
            reject(new Error(`브리지 응답이 JSON이 아닙니다: ${rawBody}`));
            return;
          }

          resolve({
            statusCode: typeof response.statusCode === "number" ? response.statusCode : 0,
            body: parsedBody,
            rawBody
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

async function fetchBridgeState(bridgeUrl) {
  const response = await httpRequestJson("GET", appendPath(bridgeUrl, "/state/current"), undefined, 5000);
  if (response.statusCode !== 200 || !isPlainObject(response.body)) {
    throw new Error(`브리지 상태를 읽지 못했습니다. HTTP ${response.statusCode}`);
  }

  return response.body;
}

function buildReadyResult(stateObject) {
  return {
    status: "ready",
    ...stateObject
  };
}

async function waitForDecisionRequest(bridgeUrl, lastSeenStateVersion, timeoutMs) {
  const deadline = Date.now() + timeoutMs;
  let lastSnapshot = null;
  let lastError = null;

  while (Date.now() <= deadline) {
    try {
      const snapshot = await fetchBridgeState(bridgeUrl);
      lastSnapshot = snapshot;
      const currentVersion = normalizeStateVersion(snapshot.state_version, 0);
      if (currentVersion > lastSeenStateVersion) {
        return buildReadyResult(snapshot);
      }
    } catch (error) {
      lastError = error;
    }

    const remainingMs = deadline - Date.now();
    if (remainingMs <= 0) {
      break;
    }

    await sleep(Math.min(POLL_INTERVAL_MS, remainingMs));
  }

  if (lastSnapshot !== null) {
    return {
      status: "timeout",
      ...lastSnapshot,
      timeout_ms: timeoutMs
    };
  }

  return {
    status: "timeout",
    state_version: 0,
    has_state: false,
    received_at: null,
    state_id: "",
    legal_action_ids: [],
    state: null,
    latest_action: null,
    timeout_ms: timeoutMs,
    bridge_error: lastError instanceof Error ? lastError.message : null
  };
}

async function getCurrentState(bridgeUrl) {
  const response = await httpRequestJson("GET", appendPath(bridgeUrl, "/state/current"), undefined, 5000);
  if (response.statusCode !== 200 || !isPlainObject(response.body)) {
    throw new Error(`브리지 상태를 읽지 못했습니다. HTTP ${response.statusCode}`);
  }

  return response.body;
}

async function submitAction(bridgeUrl, actionArguments) {
  const response = await httpRequestJson("POST", appendPath(bridgeUrl, "/action/submit"), actionArguments, 5000);
  if (response.statusCode < 200 || response.statusCode >= 300 || !isPlainObject(response.body)) {
    throw new Error(`행동 제출에 실패했습니다. HTTP ${response.statusCode}`);
  }

  return response.body;
}

function writeMessage(messageObject) {
  const payload = JSON.stringify(messageObject);
  const header = `Content-Length: ${Buffer.byteLength(payload, "utf8")}\r\n\r\n`;
  process.stdout.write(`${header}${payload}`);
}

function writeToolResult(requestId, result) {
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

function writeError(requestId, code, message) {
  writeMessage({
    jsonrpc: "2.0",
    id: requestId,
    error: {
      code,
      message
    }
  });
}

function createToolsList() {
  return [
    {
      name: "wait_for_decision_request",
      description: "브리지의 /state/current를 확인하다가 새 상태가 들어오면 반환한다.",
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
            maximum: MAX_TIMEOUT_MS,
            description: "최대 대기 시간이다. 기본값은 30000이다."
          }
        }
      }
    },
    {
      name: "get_current_state",
      description: "브리지에 저장된 최신 상태와 최근 제출 행동을 즉시 돌려준다.",
      inputSchema: {
        type: "object",
        additionalProperties: false,
        properties: {}
      }
    },
    {
      name: "submit_action",
      description: "선택한 행동을 브리지의 /action/submit으로 전달한다.",
      inputSchema: {
        type: "object",
        additionalProperties: false,
        properties: {
          selected_action_id: {
            type: "string",
            description: "고른 행동의 action_id다."
          },
          actions: {
            type: "array",
            minItems: 1,
            description: "현재 상태 기준으로 한 단계씩 검증해 실행할 행동 묶음입니다.",
            items: {
              type: "object",
              additionalProperties: false,
              required: ["type"],
              properties: {
                type: {
                  type: "string",
                  enum: ["play_card", "end_turn"]
                },
                card_id: {
                  type: "string",
                  description: "combat_state.json 손패 카드의 instance_id입니다."
                },
                hand_index: {
                  type: "integer",
                  minimum: 0
                },
                card_index: {
                  type: "integer",
                  minimum: 0
                },
                target_index: {
                  type: "integer",
                  minimum: 0
                },
                target_id: {
                  type: "string"
                }
              }
            }
          },
          reason: {
            type: "string",
            description: "행동 묶음을 선택한 이유입니다."
          },
          source: {
            type: "string",
            description: "행동 제출 주체를 적는다."
          },
          note: {
            type: "string",
            description: "추가 메모를 남긴다."
          },
          expected_state_version: {
            type: "integer",
            minimum: 0,
            description: "선택 사항이다. 현재 상태 버전과 맞는지 검증할 수 있다."
          }
        }
      }
    }
  ];
}

function createJsonRpcTransport(bridgeUrl) {
  let buffer = Buffer.alloc(0);

  async function handleMessage(messageObject) {
    if (!isPlainObject(messageObject)) {
      return;
    }

    const hasRequestId = Object.prototype.hasOwnProperty.call(messageObject, "id");
    const requestId = hasRequestId ? messageObject.id : undefined;
    const methodName = typeof messageObject.method === "string" ? messageObject.method : "";
    const paramsObject = isPlainObject(messageObject.params) ? messageObject.params : {};

    try {
      if (methodName === "initialize") {
        if (hasRequestId) {
          writeMessage({
            jsonrpc: "2.0",
            id: requestId,
            result: {
              protocolVersion: MCP_PROTOCOL_VERSION,
              serverInfo: {
                name: "spiremind-mcp-proxy",
                version: "r1"
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
        return;
      }

      if (methodName === "tools/list") {
        if (hasRequestId) {
          writeMessage({
            jsonrpc: "2.0",
            id: requestId,
            result: {
              tools: createToolsList()
            }
          });
        }
        return;
      }

      if (methodName === "tools/call") {
        const toolName = typeof paramsObject.name === "string" ? paramsObject.name : "";
        const toolArguments = isPlainObject(paramsObject.arguments) ? paramsObject.arguments : {};

        if (toolName === "wait_for_decision_request") {
          const lastSeenVersion = normalizeStateVersion(toolArguments.last_seen_state_version, 0);
          const timeoutMs = normalizeTimeoutMs(toolArguments.timeout_ms, DEFAULT_TIMEOUT_MS);
          const result = await waitForDecisionRequest(bridgeUrl, lastSeenVersion, timeoutMs);

          if (hasRequestId) {
            writeToolResult(requestId, result);
          }
          return;
        }

        if (toolName === "get_current_state") {
          const result = await getCurrentState(bridgeUrl);

          if (hasRequestId) {
            writeToolResult(requestId, result);
          }
          return;
        }

        if (toolName === "submit_action") {
          const result = await submitAction(bridgeUrl, toolArguments);

          if (hasRequestId) {
            writeToolResult(requestId, result);
          }
          return;
        }

        if (hasRequestId) {
          writeError(requestId, -32601, `알 수 없는 도구입니다: ${toolName}`);
        }
        return;
      }

      if (methodName === "shutdown") {
        if (hasRequestId) {
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

      if (hasRequestId) {
        writeError(requestId, -32601, `알 수 없는 메시지입니다: ${methodName}`);
      }
    } catch (error) {
      if (hasRequestId) {
        writeError(
          requestId,
          -32000,
          error instanceof Error ? error.message : "요청 처리 중 오류가 발생했습니다."
        );
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
    "  node bridge/spiremind_mcp_proxy.js [--bridge-url http://127.0.0.1:17832]",
    "",
    "설명:",
    "  - 자체 HTTP 서버를 열지 않는다.",
    "  - stdin/stdout에서 MCP(JSON-RPC)를 받고, 모든 도구 호출을 기존 브리지 HTTP 서버로 보낸다.",
    "  - 기본 브리지 주소는 http://127.0.0.1:17832이다."
  ].join("\n"));
}

const options = parseArgs(process.argv.slice(2));
if (options.help) {
  printUsage();
  process.exit(0);
}

createJsonRpcTransport(options.bridgeUrl);
