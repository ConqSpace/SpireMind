"use strict";

const { spawn } = require("child_process");
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

class CommandDecider {
  constructor(options) {
    this.options = options;
    this.source = "llm";
  }

  async start() {}

  async decide(snapshot) {
    return runCommandDecision(this.options, snapshot);
  }

  stop() {}
}

module.exports = {
  CommandDecider,
  runCommandDecision
};
