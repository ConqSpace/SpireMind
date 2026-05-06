"use strict";

const fs = require("fs");

function isPlainObject(value) {
  return typeof value === "object" && value !== null && !Array.isArray(value);
}

function readOptionalJson(filePath) {
  if (typeof filePath !== "string" || filePath.trim() === "" || !fs.existsSync(filePath)) {
    return null;
  }

  try {
    return JSON.parse(fs.readFileSync(filePath, "utf8").replace(/^\uFEFF/, ""));
  } catch {
    return null;
  }
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
    handoff: readOptionalJson(options.handoffFile),
    state_version: snapshot.state_version,
    state_id: snapshot.state_id || null,
    state: snapshot.state
  };
}

function buildAppServerDecisionRequest(options, snapshot) {
  const baseRequest = buildDecisionRequest(options, snapshot);
  return {
    ...baseRequest,
    state: compactStateForDecision(snapshot.state, options)
  };
}

function buildLocalHttpDecisionRequest(options, snapshot) {
  const baseRequest = buildDecisionRequest(options, snapshot);
  return {
    ...baseRequest,
    state: compactStateForDecision(snapshot.state, options)
  };
}

function compactStateForDecision(state, options = {}) {
  const source = isPlainObject(state) ? state : {};
  const piles = isPlainObject(source.piles) ? source.piles : {};
  const map = isPlainObject(source.map) ? source.map : {};
  const compactMap = compactMapForDecision(map, source, options);
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
    map: compactMap,
    legal_actions: compactLegalActionsForDecision(source, compactMap, options)
  };
}

function compactMapForDecision(map, source, options) {
  const phase = typeof source.phase === "string" ? source.phase : "";
  const availableNextNodes = Array.isArray(map.available_next_nodes) ? map.available_next_nodes : [];
  const pathOptionsSummary = isPlainObject(map.path_options_summary) ? map.path_options_summary : null;

  if (phase === "map" && shouldUseSingleMapNode(options)) {
    const firstNode = availableNextNodes.find(isPlainObject) || null;
    return {
      current: isPlainObject(map.current) ? map.current : null,
      available_next_nodes: firstNode ? [firstNode] : [],
      path_options_summary: summarizeSingleMapNode(pathOptionsSummary, firstNode),
      note: "B0 테스트는 첫 전투 진입만 검증하므로 지도 후보를 하나로 제한합니다."
    };
  }

  return {
    current: isPlainObject(map.current) ? map.current : null,
    available_next_nodes: availableNextNodes,
    path_options_summary: pathOptionsSummary
  };
}

function compactLegalActionsForDecision(source, compactMap, options) {
  const legalActions = Array.isArray(source.legal_actions) ? source.legal_actions.filter(isPlainObject) : [];
  const phase = typeof source.phase === "string" ? source.phase : "";
  if (phase !== "map" || !shouldUseSingleMapNode(options)) {
    return legalActions;
  }

  const firstNode = Array.isArray(compactMap.available_next_nodes) && isPlainObject(compactMap.available_next_nodes[0])
    ? compactMap.available_next_nodes[0]
    : null;
  const nodeId = firstNode && typeof firstNode.node_id === "string" ? firstNode.node_id : "";
  const matchingAction = legalActions.find((action) => action.node_id === nodeId)
    || legalActions.find((action) => typeof action.action_id === "string" && action.action_id.startsWith("choose_map_"))
    || null;

  return matchingAction ? [matchingAction] : legalActions;
}

function summarizeSingleMapNode(pathOptionsSummary, firstNode) {
  if (!isPlainObject(pathOptionsSummary)) {
    return null;
  }

  const options = Array.isArray(pathOptionsSummary.options) ? pathOptionsSummary.options : [];
  const nodeId = firstNode && typeof firstNode.node_id === "string" ? firstNode.node_id : "";
  const firstOption = options.find((option) => isPlainObject(option) && option.start_node_id === nodeId)
    || options.find(isPlainObject)
    || null;

  return {
    options: firstOption ? [firstOption] : [],
    note: "B0 최적화에서는 첫 전투 진입에 필요한 시작 노드 요약만 사용하므로 지도 전체 그래프를 제외합니다."
  };
}

function shouldUseSingleMapNode(options) {
  return options && options.scenarioId === "B0_NEOW_FIRST_COMBAT";
}

module.exports = {
  buildDecisionRequest,
  buildAppServerDecisionRequest,
  buildLocalHttpDecisionRequest,
  readOptionalJson
};
