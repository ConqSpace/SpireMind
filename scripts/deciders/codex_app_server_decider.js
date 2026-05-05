"use strict";

const { spawn } = require("child_process");
const { buildAppServerDecisionRequest } = require("./request_builder");
const { decisionOutputSchema, postRunReportOutputSchema } = require("./schemas");

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
    this.source = "llm_app_server";
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
      throw new Error("Codex app-server thread가 아직 준비되지 않았습니다.");
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

module.exports = {
  CodexAppServerDecider,
  JsonRpcLineClient
};
