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

function getLegalActions(stateObject) {
  const legalActions = isPlainObject(stateObject) ? stateObject.legal_actions : null;
  return Array.isArray(legalActions) ? legalActions.filter(isPlainObject) : [];
}

function getHandCards(stateObject) {
  const piles = isPlainObject(stateObject) ? stateObject.piles : null;
  const hand = isPlainObject(piles) ? piles.hand : null;
  return Array.isArray(hand) ? hand.filter(isPlainObject) : [];
}

function getLiveEnemies(stateObject) {
  const enemies = isPlainObject(stateObject) ? stateObject.enemies : null;
  if (!Array.isArray(enemies)) {
    return [];
  }

  return enemies.filter((enemy) => {
    if (!isPlainObject(enemy)) {
      return false;
    }

    const hp = Number(enemy.hp);
    return !Number.isFinite(hp) || hp > 0;
  });
}

function readTrimmedString(value) {
  return typeof value === "string" ? value.trim() : "";
}

function readOptionalInteger(value) {
  if (value === null || value === undefined || value === "") {
    return null;
  }

  const numberValue = Number(value);
  return Number.isInteger(numberValue) ? numberValue : null;
}

function normalizeOptionalTargetId(value) {
  const targetId = readTrimmedString(value);
  return targetId === "" ? null : targetId;
}

function findHandCardForAction(stateObject, actionObject) {
  const hand = getHandCards(stateObject);
  const combatCardId = readOptionalInteger(actionObject.combat_card_id);
  if (combatCardId !== null) {
    const card = hand.find((candidate) => readOptionalInteger(candidate.combat_card_id) === combatCardId);
    if (!card) {
      return {
        ok: false,
        error: `현재 손패에서 combat_card_id ${combatCardId} 카드를 찾지 못했습니다.`
      };
    }

    return {
      ok: true,
      card,
      cardInstanceId: readTrimmedString(card.instance_id || card.card_instance_id)
    };
  }

  const directCardId = readTrimmedString(actionObject.card_id || actionObject.card_instance_id || actionObject.instance_id);
  if (directCardId !== "") {
    const card = hand.find((candidate) => {
      return readTrimmedString(candidate.instance_id) === directCardId
        || readTrimmedString(candidate.card_instance_id) === directCardId
        || readTrimmedString(candidate.fallback_instance_id) === directCardId;
    });
    if (!card) {
      return {
        ok: false,
        error: `현재 손패에서 card_id '${directCardId}'와 일치하는 instance_id를 찾지 못했습니다.`
      };
    }

    return {
      ok: true,
      card,
      cardInstanceId: readTrimmedString(card.instance_id || card.card_instance_id)
    };
  }

  const indexCandidate = Object.prototype.hasOwnProperty.call(actionObject, "hand_index")
    ? actionObject.hand_index
    : actionObject.card_index;
  const cardIndex = Number(indexCandidate);
  if (!Number.isInteger(cardIndex) || cardIndex < 0 || cardIndex >= hand.length) {
    return {
      ok: false,
      error: "play_card에는 card_id 또는 유효한 hand_index/card_index가 필요합니다."
    };
  }

  const card = hand[cardIndex];
  const cardInstanceId = readTrimmedString(card.instance_id || card.card_instance_id);
  if (cardInstanceId === "") {
    return {
      ok: false,
      error: `손패 ${cardIndex}번 카드에 instance_id가 없습니다.`
    };
  }

  return {
    ok: true,
    card,
    cardInstanceId
  };
}

function resolveTargetIdForAction(stateObject, actionObject) {
  const liveEnemies = getLiveEnemies(stateObject);
  const targetCombatId = readOptionalInteger(actionObject.target_combat_id);
  if (targetCombatId !== null) {
    const enemy = liveEnemies.find((candidate) => readOptionalInteger(candidate.combat_id) === targetCombatId);
    if (!enemy) {
      return {
        ok: false,
        error: `현재 적 목록에서 target_combat_id ${targetCombatId} 대상을 찾지 못했습니다.`
      };
    }

    const targetId = readTrimmedString(enemy.id);
    if (targetId === "") {
      return {
        ok: false,
        error: `target_combat_id ${targetCombatId} 대상에 id가 없습니다.`
      };
    }

    return {
      ok: true,
      targetId,
      targetCombatId
    };
  }

  const explicitTargetId = normalizeOptionalTargetId(actionObject.target_id);
  if (explicitTargetId !== null) {
    const enemy = liveEnemies.find((candidate) => readTrimmedString(candidate.id) === explicitTargetId);
    return {
      ok: true,
      targetId: explicitTargetId,
      targetCombatId: enemy ? readOptionalInteger(enemy.combat_id) : null
    };
  }

  if (!Object.prototype.hasOwnProperty.call(actionObject, "target_index")
    || actionObject.target_index === null
    || actionObject.target_index === undefined) {
    return {
      ok: true,
      targetId: null,
      targetCombatId: null
    };
  }

  const targetIndex = Number(actionObject.target_index);
  if (!Number.isInteger(targetIndex) || targetIndex < 0 || targetIndex >= liveEnemies.length) {
    return {
      ok: false,
      error: `target_index ${actionObject.target_index}가 현재 적 목록 범위를 벗어났습니다.`
    };
  }

  const targetId = readTrimmedString(liveEnemies[targetIndex].id);
  if (targetId === "") {
    return {
      ok: false,
      error: `target_index ${targetIndex}의 적에 id가 없습니다.`
    };
  }

  return {
    ok: true,
    targetId,
    targetCombatId: readOptionalInteger(liveEnemies[targetIndex].combat_id)
  };
}

function validateCardEnergy(stateObject, card) {
  const player = isPlainObject(stateObject) ? stateObject.player : null;
  const energy = isPlainObject(player) ? Number(player.energy) : Number.NaN;
  const cost = Number(card.cost);
  if (Number.isFinite(energy) && Number.isFinite(cost) && cost > energy) {
    return {
      ok: false,
      error: `에너지가 부족합니다. 필요 ${cost}, 현재 ${energy}.`
    };
  }

  return {
    ok: true
  };
}

function resolveLegalActionForPlannedStep(stateObject, actionObject) {
  if (!isPlainObject(stateObject)) {
    return {
      ok: false,
      error: "현재 전투 상태가 없어 행동을 검증할 수 없습니다."
    };
  }

  if (!isPlainObject(actionObject)) {
    return {
      ok: false,
      error: "actions 배열의 각 항목은 JSON 객체여야 합니다."
    };
  }

  const selectedActionId = readTrimmedString(actionObject.selected_action_id || actionObject.action_id);
  if (selectedActionId !== "") {
    const legalAction = getLegalActionById(stateObject, selectedActionId);
    if (!legalAction) {
      return {
        ok: false,
        error: `현재 legal_actions에서 action_id '${selectedActionId}'를 찾지 못했습니다.`
      };
    }

    return {
      ok: true,
      legalAction
    };
  }

  const actionType = readTrimmedString(actionObject.type);
  if (actionType === "end_turn") {
    const legalAction = getLegalActions(stateObject).find((candidate) => readTrimmedString(candidate.type) === "end_turn");
    if (!legalAction) {
      return {
        ok: false,
        error: "현재 legal_actions에서 end_turn을 찾지 못했습니다."
      };
    }

    return {
      ok: true,
      legalAction
    };
  }

  if (actionType === "confirm_card_selection") {
    const legalAction = getLegalActions(stateObject).find((candidate) =>
      readTrimmedString(candidate.type) === "confirm_card_selection"
    );
    if (!legalAction) {
      return {
        ok: false,
        error: "현재 legal_actions에서 confirm_card_selection을 찾지 못했습니다."
      };
    }

    return {
      ok: true,
      legalAction
    };
  }

  if (actionType === "choose_card_selection") {
    const requestedSelectionId = readTrimmedString(actionObject.card_selection_id);
    const requestedCardId = readTrimmedString(actionObject.card_id);
    const requestedName = readTrimmedString(actionObject.name);
    const requestedIndex = readOptionalInteger(actionObject.card_selection_index);
    const legalAction = getLegalActions(stateObject).find((candidate) => {
      if (readTrimmedString(candidate.type) !== "choose_card_selection") {
        return false;
      }

      if (requestedSelectionId !== "") {
        return readTrimmedString(candidate.card_selection_id) === requestedSelectionId;
      }

      if (requestedIndex !== null) {
        return readOptionalInteger(candidate.card_selection_index) === requestedIndex;
      }

      if (requestedCardId !== "") {
        return readTrimmedString(candidate.card_id) === requestedCardId;
      }

      if (requestedName !== "") {
        return readTrimmedString(candidate.name) === requestedName;
      }

      return false;
    });
    if (!legalAction) {
      return {
        ok: false,
        error: "현재 legal_actions에서 요청한 choose_card_selection을 찾지 못했습니다."
      };
    }

    return {
      ok: true,
      legalAction
    };
  }

  if (actionType !== "play_card") {
    return {
      ok: false,
      error: `지원하지 않는 행동 타입입니다: ${actionType || "<empty>"}`
    };
  }

  const cardResult = findHandCardForAction(stateObject, actionObject);
  if (!cardResult.ok) {
    return cardResult;
  }

  const energyResult = validateCardEnergy(stateObject, cardResult.card);
  if (!energyResult.ok) {
    return energyResult;
  }

  const targetResult = resolveTargetIdForAction(stateObject, actionObject);
  if (!targetResult.ok) {
    return targetResult;
  }

  const legalAction = getLegalActions(stateObject).find((candidate) => {
    const candidateTargetCombatId = readOptionalInteger(candidate.target_combat_id);
    const targetCombatIdMatches = targetResult.targetCombatId === null
      || candidateTargetCombatId === null
      || candidateTargetCombatId === targetResult.targetCombatId;

    return readTrimmedString(candidate.type) === "play_card"
      && readTrimmedString(candidate.card_instance_id) === cardResult.cardInstanceId
      && normalizeOptionalTargetId(candidate.target_id) === targetResult.targetId
      && targetCombatIdMatches;
  });
  if (!legalAction) {
    return {
      ok: false,
      error: `현재 legal_actions에서 카드 ${cardResult.cardInstanceId}, 대상 ${targetResult.targetId || "없음"} 조합을 찾지 못했습니다.`
    };
  }

  return {
    ok: true,
    legalAction
  };
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
    actionPlan: null,
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
  advanceActionPlanAfterStateUpdate(state);
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

function createActionSubmissionFromLegalAction(state, legalAction, actionArguments) {
  const selectedActionId = readTrimmedString(legalAction.action_id || legalAction.actionId);
  return createActionSubmission(state, {
    selected_action_id: selectedActionId,
    expected_state_version: state.stateVersion,
    source: actionArguments.source,
    note: actionArguments.note
  });
}

function createActionPlanSubmission(state, actionArguments) {
  if (!Array.isArray(actionArguments.actions) || actionArguments.actions.length === 0) {
    return {
      ok: false,
      status: "invalid",
      error: "actions는 비어 있지 않은 배열이어야 합니다."
    };
  }

  if (state.latestAction
    && state.latestAction.result === null
    && state.latestAction.execution_status !== "invalid"
    && state.latestAction.execution_status !== "failed"
    && state.latestAction.execution_status !== "stale"
    && state.latestAction.execution_status !== "unsupported") {
    return {
      ok: false,
      status: "busy",
      error: "아직 처리 중인 행동이 있어 새 행동 묶음을 받을 수 없습니다."
    };
  }

  const firstStep = actionArguments.actions[0];
  const resolved = resolveLegalActionForPlannedStep(state.currentState, firstStep);
  const plan = {
    plan_id: crypto.randomUUID(),
    submitted_at: toIsoString(new Date()),
    status: resolved.ok ? "running" : "failed",
    state_version_started: state.stateVersion,
    state_id_started: state.currentStateId,
    reason: typeof actionArguments.reason === "string" ? actionArguments.reason.trim() : null,
    source: typeof actionArguments.source === "string" ? actionArguments.source.trim() : null,
    actions: actionArguments.actions,
    current_index: 0,
    completed: [],
    failure: resolved.ok ? null : {
      action_index: 0,
      reason: resolved.error
    }
  };
  state.actionPlan = plan;

  if (!resolved.ok) {
    appendEvent(state, "action_plan_failed", {
      plan_id: plan.plan_id,
      action_index: 0,
      reason: resolved.error
    });

    return {
      ok: false,
      status: "invalid",
      action_plan: plan,
      error: resolved.error
    };
  }

  const submission = createActionSubmissionFromLegalAction(state, resolved.legalAction, {
    source: actionArguments.source,
    note: actionArguments.reason || actionArguments.note
  });
  submission.plan_id = plan.plan_id;
  submission.plan_action_index = 0;
  submission.planned_action = firstStep;
  setLatestAction(state, submission);
  appendEvent(state, "action_plan_started", {
    plan_id: plan.plan_id,
    action_count: plan.actions.length,
    first_submission_id: submission.submission_id,
    selected_action_id: submission.selected_action_id
  });

  return {
    ok: true,
    status: "queued",
    action_plan: plan,
    latest_action: submission
  };
}

function advanceActionPlanAfterStateUpdate(state) {
  const plan = state.actionPlan;
  if (!plan || plan.status !== "running") {
    return;
  }

  const latestAction = state.latestAction;
  if (!latestAction || latestAction.plan_id !== plan.plan_id) {
    return;
  }

  if (latestAction.result === null) {
    return;
  }

  if (latestAction.result !== "applied") {
    plan.status = "failed";
    plan.failure = {
      action_index: latestAction.plan_action_index,
      submission_id: latestAction.submission_id,
      result: latestAction.result,
      reason: latestAction.result_note || "이전 행동이 적용되지 않아 남은 행동을 중단했습니다."
    };
    appendEvent(state, "action_plan_failed", {
      plan_id: plan.plan_id,
      action_index: latestAction.plan_action_index,
      submission_id: latestAction.submission_id,
      result: latestAction.result
    });
    return;
  }

  const completedIndex = Number(latestAction.plan_action_index);
  if (!plan.completed.some((item) => item.submission_id === latestAction.submission_id)) {
    plan.completed.push({
      action_index: completedIndex,
      submission_id: latestAction.submission_id,
      selected_action_id: latestAction.selected_action_id,
      applied_at_state_version: state.stateVersion
    });
  }

  const nextIndex = completedIndex + 1;
  plan.current_index = nextIndex;
  if (nextIndex >= plan.actions.length) {
    plan.status = "completed";
    appendEvent(state, "action_plan_completed", {
      plan_id: plan.plan_id,
      completed_count: plan.completed.length
    });
    return;
  }

  const resolved = resolveLegalActionForPlannedStep(state.currentState, plan.actions[nextIndex]);
  if (!resolved.ok) {
    plan.status = "failed";
    plan.failure = {
      action_index: nextIndex,
      reason: resolved.error
    };
    appendEvent(state, "action_plan_failed", {
      plan_id: plan.plan_id,
      action_index: nextIndex,
      reason: resolved.error
    });
    return;
  }

  const submission = createActionSubmissionFromLegalAction(state, resolved.legalAction, {
    source: plan.source,
    note: plan.reason
  });
  submission.plan_id = plan.plan_id;
  submission.plan_action_index = nextIndex;
  submission.planned_action = plan.actions[nextIndex];
  setLatestAction(state, submission);
  appendEvent(state, "action_plan_step_submitted", {
    plan_id: plan.plan_id,
    action_index: nextIndex,
    submission_id: submission.submission_id,
    selected_action_id: submission.selected_action_id
  });
}

function retryCurrentPlanStepAfterStale(state, staleAction, note) {
  const plan = state.actionPlan;
  if (!plan || plan.status !== "running" || staleAction.plan_id !== plan.plan_id) {
    return null;
  }

  const actionIndex = Number(staleAction.plan_action_index);
  if (!Number.isInteger(actionIndex) || actionIndex < 0 || actionIndex >= plan.actions.length) {
    return null;
  }

  if (!isPlainObject(plan.stale_retries)) {
    plan.stale_retries = {};
  }

  const retryCount = Number(plan.stale_retries[String(actionIndex)] || 0);
  if (retryCount >= 2) {
    return null;
  }

  const plannedAction = plan.actions[actionIndex];
  const resolved = resolveLegalActionForPlannedStep(state.currentState, plannedAction);
  if (!resolved.ok) {
    return null;
  }

  plan.stale_retries[String(actionIndex)] = retryCount + 1;
  plan.current_index = actionIndex;
  const submission = createActionSubmissionFromLegalAction(state, resolved.legalAction, {
    source: plan.source,
    note: plan.reason || note
  });
  submission.plan_id = plan.plan_id;
  submission.plan_action_index = actionIndex;
  submission.planned_action = plannedAction;
  setLatestAction(state, submission);
  appendEvent(state, "action_plan_step_retried_after_stale", {
    plan_id: plan.plan_id,
    action_index: actionIndex,
    retry_count: retryCount + 1,
    stale_submission_id: staleAction.submission_id,
    submission_id: submission.submission_id,
    selected_action_id: submission.selected_action_id
  });
  return submission;
}

function failCurrentPlanStepAfterStale(state, staleAction, note) {
  if (!state.actionPlan
    || state.actionPlan.status !== "running"
    || staleAction.plan_id !== state.actionPlan.plan_id) {
    return;
  }

  state.actionPlan.status = "failed";
  state.actionPlan.failure = {
    action_index: staleAction.plan_action_index,
    submission_id: staleAction.submission_id,
    result: "stale",
    reason: note || "stale 행동을 최신 상태에서 다시 제출하지 못했습니다."
  };
  updateLatestAction(state, "action_plan_failed", {
    plan_id: state.actionPlan.plan_id,
    action_index: staleAction.plan_action_index,
    submission_id: staleAction.submission_id,
    result: "stale"
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

    const retrySubmission = retryCurrentPlanStepAfterStale(state, latestAction, latestAction.result_note);
    if (retrySubmission) {
      return {
        ok: true,
        status: "stale_retry_queued",
        reason: "상태가 바뀐 행동을 최신 상태 기준으로 다시 제출했습니다.",
        action: retrySubmission
      };
    }

    failCurrentPlanStepAfterStale(state, latestAction, latestAction.result_note);
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

    const retrySubmission = retryCurrentPlanStepAfterStale(state, latestAction, latestAction.result_note);
    if (retrySubmission) {
      return {
        ok: true,
        status: "stale_retry_queued",
        reason: "사라진 행동을 최신 legal_actions 기준으로 다시 제출했습니다.",
        action: retrySubmission
      };
    }

    failCurrentPlanStepAfterStale(state, latestAction, latestAction.result_note);
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

  if (state.actionPlan
    && state.actionPlan.status === "running"
    && latestAction.plan_id === state.actionPlan.plan_id
    && result !== "applied"
    && result !== "stale") {
    state.actionPlan.status = "failed";
    state.actionPlan.failure = {
      action_index: latestAction.plan_action_index,
      submission_id: latestAction.submission_id,
      result,
      reason: note || "행동 실행 결과가 applied가 아니어서 남은 행동을 중단했습니다."
    };
  }

  updateLatestAction(state, "action_result_reported", {
    submission_id: latestAction.submission_id,
    claim_token: latestAction.claim_token,
    executor_id: executorId || null,
    result,
    observed_state_id: observedStateId || null,
    observed_state_version: observedStateVersion >= 0 ? observedStateVersion : null
  });

  if (result === "stale") {
    const retrySubmission = retryCurrentPlanStepAfterStale(state, latestAction, note);
    if (retrySubmission) {
      return {
        ok: true,
        status: "stale_retry_queued",
        latest_action: retrySubmission,
        action_plan: state.actionPlan
      };
    }

    failCurrentPlanStepAfterStale(state, latestAction, note);
  }

  return {
    ok: true,
    status: result,
    latest_action: latestAction,
    action_plan: state.actionPlan
  };
}

function getCurrentStatePayload(state) {
  return {
    state_version: state.stateVersion,
    received_at: state.currentStateReceivedAt,
    state_id: state.currentStateId,
    legal_action_ids: state.legalActionIds,
    state: state.currentState,
    latest_action: state.latestAction,
    action_plan: state.actionPlan
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
          latest_action: state.latestAction,
          action_plan: state.actionPlan
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

        if (Array.isArray(body.actions)) {
          const planSubmission = createActionPlanSubmission(state, body);
          sendJson(res, planSubmission.ok ? 200 : 400, planSubmission);
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
