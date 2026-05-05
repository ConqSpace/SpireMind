"use strict";

function isPlainObject(value) {
  return typeof value === "object" && value !== null && !Array.isArray(value);
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

module.exports = {
  buildDecisionRequest,
  buildAppServerDecisionRequest
};
