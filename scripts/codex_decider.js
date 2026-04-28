#!/usr/bin/env node
"use strict";

const { spawn } = require("child_process");
const fs = require("fs");
const os = require("os");
const path = require("path");

const DEFAULT_MODEL = "gpt-5.4-mini";
const DEFAULT_TIMEOUT_MS = 120000;

function parseArgs(argv) {
  const options = {
    model: process.env.SPIREMIND_CODEX_MODEL || DEFAULT_MODEL,
    codexCommand: process.env.SPIREMIND_CODEX_COMMAND || (process.platform === "win32" ? "codex.cmd" : "codex"),
    cwd: process.env.SPIREMIND_CODEX_CWD || process.cwd(),
    timeoutMs: readPositiveInteger(process.env.SPIREMIND_CODEX_TIMEOUT_MS, DEFAULT_TIMEOUT_MS),
    keepTemp: false,
    selfTest: false,
    help: false
  };

  for (let index = 0; index < argv.length; index += 1) {
    const token = argv[index];
    if (token === "--help" || token === "-h") {
      options.help = true;
      continue;
    }

    if (token === "--model" && index + 1 < argv.length) {
      options.model = argv[index + 1];
      index += 1;
      continue;
    }

    if (token.startsWith("--model=")) {
      options.model = token.slice("--model=".length);
      continue;
    }

    if (token === "--codex-command" && index + 1 < argv.length) {
      options.codexCommand = argv[index + 1];
      index += 1;
      continue;
    }

    if (token.startsWith("--codex-command=")) {
      options.codexCommand = token.slice("--codex-command=".length);
      continue;
    }

    if (token === "--cwd" && index + 1 < argv.length) {
      options.cwd = argv[index + 1];
      index += 1;
      continue;
    }

    if (token.startsWith("--cwd=")) {
      options.cwd = token.slice("--cwd=".length);
      continue;
    }

    if (token === "--timeout-ms" && index + 1 < argv.length) {
      options.timeoutMs = readPositiveInteger(argv[index + 1], options.timeoutMs);
      index += 1;
      continue;
    }

    if (token === "--keep-temp") {
      options.keepTemp = true;
      continue;
    }

    if (token === "--self-test") {
      options.selfTest = true;
    }
  }

  return options;
}

function showHelp() {
  process.stderr.write([
    "SpireMind Codex decider",
    "",
    "Usage:",
    "  node scripts/codex_decider.js < decision_request.json",
    "  node scripts/codex_decider.js --model gpt-5.4-mini",
    "",
    "Environment:",
    "  SPIREMIND_CODEX_MODEL       기본 모델. 기본값은 gpt-5.4-mini",
    "  SPIREMIND_CODEX_COMMAND     Codex 실행 파일. Windows 기본값은 codex.cmd",
    "  SPIREMIND_CODEX_TIMEOUT_MS  Codex 응답 제한 시간",
    "  SPIREMIND_CODEX_FAKE_DECISION 테스트용 고정 결정 JSON. 설정되면 Codex를 호출하지 않는다.",
    ""
  ].join("\n"));
}

function readPositiveInteger(value, fallbackValue) {
  const parsed = Number.parseInt(String(value), 10);
  return Number.isFinite(parsed) && parsed > 0 ? parsed : fallbackValue;
}

function readStdin() {
  return new Promise((resolve, reject) => {
    let text = "";
    process.stdin.setEncoding("utf8");
    process.stdin.on("data", (chunk) => {
      text += chunk;
    });
    process.stdin.on("end", () => resolve(text));
    process.stdin.on("error", reject);
  });
}

function safeJsonParse(text) {
  try {
    return JSON.parse(String(text).replace(/^\uFEFF/, ""));
  } catch {
    return null;
  }
}

function isPlainObject(value) {
  return typeof value === "object" && value !== null && !Array.isArray(value);
}

function readNumber(value) {
  const parsed = Number(value);
  return Number.isFinite(parsed) ? parsed : null;
}

function compactRequest(request) {
  const state = isPlainObject(request.state) ? request.state : {};
  const player = isPlainObject(state.player) ? state.player : {};
  const piles = isPlainObject(state.piles) ? state.piles : {};
  const hand = Array.isArray(piles.hand) ? piles.hand.filter(isPlainObject) : [];
  const enemies = Array.isArray(state.enemies) ? state.enemies.filter(isPlainObject) : [];
  const legalActions = Array.isArray(state.legal_actions) ? state.legal_actions.filter(isPlainObject) : [];

  return {
    play_session_id: typeof request.play_session_id === "string" ? request.play_session_id : null,
    state_version: readNumber(request.state_version),
    player: {
      hp: readNumber(player.hp),
      max_hp: readNumber(player.max_hp),
      block: readNumber(player.block),
      energy: readNumber(player.energy),
      max_energy: readNumber(player.max_energy)
    },
    hand: hand.map((card) => ({
      combat_card_id: readNumber(card.combat_card_id),
      name: typeof card.name === "string" ? card.name : null,
      type: typeof card.type === "string" ? card.type : null,
      cost: readNumber(card.cost),
      playable: card.playable === true,
      target_type: typeof card.target_type === "string" ? card.target_type : null,
      damage: readNumber(card.damage),
      block: readNumber(card.block),
      description: typeof card.description === "string" ? card.description : null
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
            total_damage: readNumber(enemy.intent.total_damage),
            description: typeof enemy.intent.description === "string" ? enemy.intent.description : null
          }
        : null
    })),
    relics: Array.isArray(state.relics)
      ? state.relics.filter(isPlainObject).map((relic) => ({
          id: typeof relic.id === "string" ? relic.id : null,
          name: typeof relic.name === "string" ? relic.name : null
        }))
      : [],
    legal_actions: legalActions.map((action) => ({
      action_id: typeof action.action_id === "string" ? action.action_id : null,
      type: typeof action.type === "string" ? action.type : null,
      combat_card_id: readNumber(action.combat_card_id),
      target_combat_id: readNumber(action.target_combat_id),
      energy_cost: readNumber(action.energy_cost),
      summary: typeof action.summary === "string" ? action.summary : null
    }))
  };
}

function createResponseSchema(tempDirectory) {
  const schemaPath = path.join(tempDirectory, "decision_response_schema.json");
  const schema = {
    type: "object",
    additionalProperties: false,
    properties: {
      actions: {
        type: "array",
        items: {
          type: "object",
          additionalProperties: false,
          properties: {
            type: { type: "string" },
            combat_card_id: { type: ["number", "null"] },
            target_combat_id: { type: ["number", "null"] },
            selected_action_id: { type: ["string", "null"] }
          },
          required: ["type", "combat_card_id", "target_combat_id", "selected_action_id"]
        }
      },
      selected_action_id: { type: ["string", "null"] },
      reason: { type: "string" },
      confidence: { type: ["number", "null"] }
    },
    required: ["actions", "selected_action_id", "reason", "confidence"]
  };
  fs.writeFileSync(schemaPath, `${JSON.stringify(schema, null, 2)}\n`, "utf8");
  return schemaPath;
}

function buildCodexPrompt(request) {
  const compact = compactRequest(request);
  return [
    "너는 Slay the Spire 2를 플레이하는 의사결정기다.",
    "현재 전투 상태를 보고 이번 턴에 실행할 행동 JSON만 반환한다.",
    "",
    "규칙:",
    "- legal_actions에 맞는 행동만 선택한다.",
    "- 카드를 사용할 때는 combat_card_id를 사용한다.",
    "- 적 대상은 target_combat_id를 사용한다.",
    "- 대상이 없는 카드는 target_combat_id를 null로 둔다.",
    "- cost가 -1인 X비용 카드는 현재 에너지를 모두 쓰는 행동으로 본다.",
    "- X비용 카드를 쓴 뒤에는 에너지를 쓰는 카드를 이어서 계획하지 않는다.",
    "- 여러 카드를 순서대로 쓸 수 있으면 actions 배열에 순서대로 넣는다.",
    "- actions 배열 안에서는 selected_action_id를 사용하지 않는다.",
    "- 더 할 행동이 없으면 end_turn을 포함한다.",
    "- 출력은 JSON 객체 하나뿐이어야 한다.",
    "- 마크다운, 설명문, 코드블록은 출력하지 않는다.",
    "",
    "응답 예시:",
    "{\"actions\":[{\"type\":\"play_card\",\"combat_card_id\":0,\"target_combat_id\":1},{\"type\":\"end_turn\"}],\"reason\":\"공격 후 턴 종료\"}",
    "",
    "현재 상태:",
    JSON.stringify(compact, null, 2)
  ].join("\n");
}

function runCodex(options, prompt, schemaPath, outputPath) {
  return new Promise((resolve, reject) => {
    const args = [
      "exec",
      "--ephemeral",
      "--sandbox",
      "read-only",
      "--skip-git-repo-check",
      "--cd",
      options.cwd,
      "--output-schema",
      schemaPath,
      "--output-last-message",
      outputPath
    ];

    if (options.model.trim() !== "") {
      args.push("--model", options.model);
    }

    args.push("-");

    const child = spawn(options.codexCommand, args, {
      cwd: options.cwd,
      stdio: ["pipe", "pipe", "pipe"],
      windowsHide: true,
      shell: process.platform === "win32" && /\.cmd$/i.test(options.codexCommand)
    });

    let stderr = "";
    const timer = setTimeout(() => {
      child.kill();
      reject(new Error(`Codex 응답이 ${options.timeoutMs}ms 안에 끝나지 않았습니다.`));
    }, options.timeoutMs);

    child.stderr.setEncoding("utf8");
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
        reject(new Error(`Codex 실행 실패: exit=${code}, stderr=${stderr.trim()}`));
        return;
      }

      resolve();
    });

    child.stdin.end(prompt);
  });
}

function extractJsonObject(text) {
  const parsed = safeJsonParse(text.trim());
  if (isPlainObject(parsed)) {
    return parsed;
  }

  const firstBrace = text.indexOf("{");
  const lastBrace = text.lastIndexOf("}");
  if (firstBrace < 0 || lastBrace <= firstBrace) {
    return null;
  }

  return safeJsonParse(text.slice(firstBrace, lastBrace + 1));
}

function createHandCostMap(request) {
  const compact = compactRequest(request);
  const costMap = new Map();
  for (const card of compact.hand) {
    if (card.combat_card_id !== null && card.cost !== null) {
      costMap.set(card.combat_card_id, card.cost);
    }
  }

  return {
    energy: compact.player.energy,
    costMap
  };
}

function trimActionsByKnownEnergy(actions, request) {
  if (!Array.isArray(actions)) {
    return actions;
  }

  const { energy: initialEnergy, costMap } = createHandCostMap(request);
  let energy = initialEnergy;
  const trimmed = [];
  for (const action of actions) {
    if (!isPlainObject(action) || action.type !== "play_card") {
      trimmed.push(action);
      continue;
    }

    const combatCardId = readNumber(action.combat_card_id);
    const cost = combatCardId === null ? null : costMap.get(combatCardId);
    if (energy !== null && cost !== null) {
      if (cost < 0) {
        trimmed.push(action);
        energy = 0;
        continue;
      }

      if (cost > energy) {
        continue;
      }

      energy -= cost;
    }

    trimmed.push(action);
  }

  if (trimmed.length > 0 && trimmed[trimmed.length - 1].type !== "end_turn") {
    trimmed.push({ type: "end_turn" });
  }

  return trimmed;
}

function normalizeDecision(decision, request) {
  if (!isPlainObject(decision)) {
    throw new Error("Codex 응답이 JSON 객체가 아닙니다.");
  }

  const normalized = {};
  if (Array.isArray(decision.actions)) {
    normalized.actions = decision.actions
      .filter(isPlainObject)
      .map((action) => {
        const actionType = typeof action.type === "string" ? action.type.trim() : "";
        if (actionType === "end_turn") {
          return { type: "end_turn" };
        }

        const item = {
          type: actionType
        };
        const combatCardId = readNumber(action.combat_card_id);
        const targetCombatId = readNumber(action.target_combat_id);
        if (combatCardId !== null) {
          item.combat_card_id = combatCardId;
        }
        if (targetCombatId !== null && targetCombatId > 0) {
          item.target_combat_id = targetCombatId;
        }
        return item;
      })
      .filter((action) => action.type.trim() !== "");
    normalized.actions = trimActionsByKnownEnergy(normalized.actions, request);
  }

  if (typeof decision.selected_action_id === "string" && decision.selected_action_id.trim() !== "") {
    normalized.selected_action_id = decision.selected_action_id.trim();
  }

  normalized.reason = typeof decision.reason === "string" && decision.reason.trim() !== ""
    ? decision.reason.trim()
    : "Codex 판단";

  const confidence = readNumber(decision.confidence);
  if (confidence !== null) {
    normalized.confidence = confidence;
  }

  if ((!Array.isArray(normalized.actions) || normalized.actions.length === 0) && !normalized.selected_action_id) {
    throw new Error("Codex 응답에 actions 또는 selected_action_id가 없습니다.");
  }

  return normalized;
}

async function main() {
  const options = parseArgs(process.argv.slice(2));
  if (options.help) {
    showHelp();
    return;
  }

  if (options.selfTest) {
    const sample = {
      state_version: 1,
      play_session_id: "self_test",
      state: {
        player: { hp: 80, energy: 3 },
        piles: { hand: [] },
        enemies: [],
        legal_actions: [{ action_id: "end_turn", type: "end_turn" }]
      }
    };
    process.stdout.write(`${JSON.stringify(compactRequest(sample), null, 2)}\n`);
    return;
  }

  const rawInput = await readStdin();
  const request = safeJsonParse(rawInput);
  if (!isPlainObject(request)) {
    throw new Error("stdin 입력이 JSON 객체가 아닙니다.");
  }

  const tempDirectory = fs.mkdtempSync(path.join(os.tmpdir(), "spiremind_codex_decider_"));
  const outputPath = path.join(tempDirectory, "last_message.json");
  const schemaPath = createResponseSchema(tempDirectory);

  try {
    if (typeof process.env.SPIREMIND_CODEX_FAKE_DECISION === "string"
      && process.env.SPIREMIND_CODEX_FAKE_DECISION.trim() !== "") {
      const fakeDecision = extractJsonObject(process.env.SPIREMIND_CODEX_FAKE_DECISION);
      const normalizedFakeDecision = normalizeDecision(fakeDecision, request);
      process.stdout.write(`${JSON.stringify(normalizedFakeDecision)}\n`);
      return;
    }

    const prompt = buildCodexPrompt(request);
    await runCodex(options, prompt, schemaPath, outputPath);
    const finalMessage = fs.existsSync(outputPath) ? fs.readFileSync(outputPath, "utf8") : "";
    const parsed = extractJsonObject(finalMessage);
    const normalized = normalizeDecision(parsed, request);
    process.stdout.write(`${JSON.stringify(normalized)}\n`);
  } finally {
    if (!options.keepTemp) {
      fs.rmSync(tempDirectory, { recursive: true, force: true });
    }
  }
}

main().catch((error) => {
  process.stderr.write(`${error.stack || error.message || String(error)}\n`);
  process.exit(1);
});
