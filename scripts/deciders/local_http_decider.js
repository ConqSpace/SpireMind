"use strict";

const http = require("http");
const https = require("https");
const { buildDecisionRequest } = require("./request_builder");

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

function postJson(endpoint, body, headers, timeoutMs) {
  return new Promise((resolve, reject) => {
    const url = new URL(endpoint);
    const transport = url.protocol === "https:" ? https : http;
    const payload = JSON.stringify(body);
    const request = transport.request(
      url,
      {
        method: "POST",
        headers: {
          "Content-Type": "application/json",
          "Content-Length": Buffer.byteLength(payload),
          ...headers
        }
      },
      (response) => {
        const chunks = [];
        response.on("data", (chunk) => chunks.push(chunk));
        response.on("end", () => {
          const raw = Buffer.concat(chunks).toString("utf8");
          const parsed = safeJsonParse(raw);
          if (response.statusCode < 200 || response.statusCode >= 300) {
            reject(new Error(`local-http 응답 실패: HTTP ${response.statusCode}, body=${raw}`));
            return;
          }

          if (!isPlainObject(parsed)) {
            reject(new Error(`local-http 응답이 JSON 객체가 아닙니다: ${raw}`));
            return;
          }

          resolve(parsed);
        });
      }
    );

    request.setTimeout(timeoutMs, () => {
      request.destroy(new Error(`local-http 요청이 ${timeoutMs}ms 안에 끝나지 않았습니다.`));
    });
    request.on("error", reject);
    request.end(payload, "utf8");
  });
}

function readChatText(response) {
  const choices = Array.isArray(response.choices) ? response.choices : [];
  const first = isPlainObject(choices[0]) ? choices[0] : {};
  const message = isPlainObject(first.message) ? first.message : {};
  if (typeof message.content === "string") {
    return message.content;
  }

  if (typeof response.output_text === "string") {
    return response.output_text;
  }

  if (isPlainObject(response.decision)) {
    return JSON.stringify(response.decision);
  }

  return JSON.stringify(response);
}

class LocalHttpDecider {
  constructor(options) {
    this.options = options;
    this.source = "llm_local_http";
  }

  async start() {}

  async decide(snapshot) {
    const endpoint = this.options.endpoint || this.options.providerEndpoint;
    if (!endpoint) {
      throw new Error("local-http decider에는 endpoint가 필요합니다.");
    }

    const prompt = JSON.stringify(buildDecisionRequest(this.options, snapshot), null, 2);
    const headers = {};
    const apiKeyEnv = this.options.apiKeyEnv || this.options.providerApiKeyEnv;
    if (apiKeyEnv && process.env[apiKeyEnv]) {
      headers.Authorization = `Bearer ${process.env[apiKeyEnv]}`;
    }

    const response = await postJson(
      endpoint,
      {
        model: this.options.model,
        temperature: this.options.temperature ?? 0,
        messages: [
          {
            role: "system",
            content: "너는 Slay the Spire 2 자동 플레이 판단기다. JSON 객체 하나만 응답한다."
          },
          {
            role: "user",
            content: prompt
          }
        ]
      },
      headers,
      this.options.timeoutMs
    );

    const parsed = extractJsonObject(readChatText(response));
    if (!isPlainObject(parsed)) {
      throw new Error(`local-http 최종 응답이 JSON 객체가 아닙니다: ${JSON.stringify(response)}`);
    }

    return parsed;
  }

  stop() {}
}

module.exports = {
  LocalHttpDecider
};
