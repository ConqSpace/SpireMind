# 행동 목록과 Codex 응답 형식

이 문서는 Codex가 선택할 수 있는 행동 목록과 응답 형식을 정의한다.

Codex는 게임 명령을 직접 만들지 않는다. 모드가 만든 `legal_actions` 안에서 하나의 `action_id`만 고른다. 이렇게 해야 판단 오류와 실행 오류를 분리할 수 있다.

## 요청 구조

상태 JSON 안의 `legal_actions`는 다음 형식을 따른다.

```json
[
  {
    "action_id": "play_hand_0_enemy_0",
    "type": "play_card",
    "card_instance_id": "hand_0",
    "target_id": "enemy_0",
    "energy_cost": 1,
    "summary": "Strike를 Cultist에게 사용한다."
  },
  {
    "action_id": "end_turn",
    "type": "end_turn",
    "summary": "현재 턴을 끝낸다."
  }
]
```

## action_id 규칙

- `action_id`는 현재 `state_id`와 `state_version` 안에서만 유효하다.
- 다음 상태로 넘어가면 같은 문자열을 재사용해도 되지만, 이전 응답을 새 상태에 적용하면 안 된다.
- 사람이 읽을 수 있는 형태를 우선한다.
- 실행에는 `action_id`만 사용한다. `summary`는 분석용이다.

## 1차 행동 종류

### play_card

```json
{
  "action_id": "play_hand_2_enemy_1",
  "type": "play_card",
  "card_instance_id": "hand_2",
  "target_id": "enemy_1",
  "energy_cost": 1,
  "summary": "Bash를 Jaw Worm에게 사용한다."
}
```

대상이 없는 카드는 `target_id`를 `null`로 둔다.

```json
{
  "action_id": "play_hand_1_no_target",
  "type": "play_card",
  "card_instance_id": "hand_1",
  "target_id": null,
  "energy_cost": 0,
  "summary": "Battle Trance를 사용한다."
}
```

### end_turn

```json
{
  "action_id": "end_turn",
  "type": "end_turn",
  "summary": "현재 턴을 끝낸다."
}
```

### 보상 화면 후보 행동

보상 화면에서는 `phase: "reward"` 상태와 함께 아래 후보를 만든다. 현재 단계에서는 관측용이다. 모드 실행기는 아직 보상을 클릭하지 않는다.

```json
{
  "action_id": "choose_reward_0_card_1",
  "type": "choose_card_reward",
  "reward_id": "reward_0",
  "card_reward_index": 1,
  "card_id": "DefendCard",
  "card_name": "Defend",
  "summary": "Defend 카드를 보상으로 선택한다."
}
```

지원 예정 후보:

- `choose_card_reward`
- `skip_card_reward`
- `claim_relic_reward`
- `claim_potion_reward`

## Codex MCP 도구 응답

R4에서는 Codex가 브리지 MCP 도구를 사용한다.

Codex의 선택은 `submit_action` 호출로 표현한다.

```json
{
  "selected_action_id": "play_hand_0_enemy_0",
  "source": "codex-gpt-5.4-mini",
  "note": "적 체력을 먼저 줄이는 선택이다.",
  "expected_state_version": 3
}
```

## 브리지 검증 규칙

브리지는 `submit_action`을 받으면 아래를 확인한다.

- 최신 상태가 존재하는가.
- `selected_action_id`가 비어 있지 않은가.
- `selected_action_id`가 최신 `legal_actions` 안에 있는가.
- `expected_state_version`이 들어오면 현재 `state_version`과 비교할 수 있는가.

검증 결과는 `latest_action.json`과 `events.jsonl`에 남긴다.

## R5 실행 소비 규칙

R5부터는 Codex가 고른 행동을 STS2 모드가 실제로 실행한다. 이때 `latest_action`은 실행 명령 전체가 아니라 실행 후보의 참조값이다.

실행 가능 조건:

- `latest_action.valid`가 `true`다.
- `submission_id`가 이전에 실행된 적이 없다.
- `latest_action.state_id`가 모드가 보고 있는 최신 상태와 맞다.
- `latest_action.state_version`이 모드가 보고 있는 최신 상태 버전과 맞다.
- `selected_action_id`가 현재 `state.legal_actions` 안에 다시 존재한다.
- 현재 `legal_actions`에서 찾은 항목의 `type`을 모드가 지원한다.

중요한 규칙:

- 모드는 `latest_action.selected_action_id`만 실행 입력으로 삼지 않는다.
- 모드는 현재 `legal_actions`에서 같은 `action_id`를 다시 찾아 `type`, `card_instance_id`, `target_id`, `energy_cost`를 읽는다.
- 같은 `submission_id`는 성공, 실패, 무시 여부와 관계없이 다시 실행하지 않는다.
- 상태가 맞지 않으면 실행하지 않고 `stale`로 보고한다.
- 지원하지 않는 행동이면 실행하지 않고 `unsupported`로 보고한다.
- 실행 예외가 발생하면 `failed`로 보고하고 다음 상태를 기다린다.

R5.1에서는 `end_turn`만 실행한다. `play_card`는 대상 없는 카드, 대상 있는 카드 순서로 나중에 연다.

## 나중에 추가할 행동 종류

아래 행동은 1차 자동 전투가 안정화된 뒤 추가한다.

- `use_potion`
- `choose_map_node`
- `buy_shop_item`
- `remove_card`
- `upgrade_card`
- `rest`
- `choose_event_option`

## 턴 단위와 행동 단위

1차 구현은 행동 단위로 진행한다.

```text
상태 추출
-> 브리지에 상태 전달
-> Codex가 다음 행동 1개 선택
-> 브리지가 선택 검증
-> R5에서 모드가 실행
-> 새 상태 추출
```

이 방식을 먼저 쓰는 이유는 명확하다.

- 카드 한 장이 더미, 에너지, 적 체력, 버프를 즉시 바꾼다.
- 한 턴 전체 계획을 한 번에 받으면 중간 상태 변화로 계획이 무효가 될 수 있다.
- 실패한 행동을 어느 순간에 골랐는지 쉽게 추적할 수 있다.
## 현재 실행 지원 상태

현재 모드 실행기는 `end_turn`과 `play_card`를 지원한다.

`play_card` 행동에는 실행 안정성을 위해 다음 필드를 함께 둔다.

```json
{
  "action_id": "play_combat_card_0_enemy_0",
  "type": "play_card",
  "card_instance_id": "combat_card_0",
  "combat_card_id": 0,
  "target_id": "enemy_0",
  "target_combat_id": 1,
  "energy_cost": 1
}
```

- `card_instance_id`는 사람이 읽고 브리지에서 고르는 행동 식별자에 가깝다.
- `combat_card_id`는 실행기가 실제 `CardModel`을 되찾는 기준이다.
- `target_id`는 기존 호환용 대상 이름이다.
- `target_combat_id`는 실행기가 실제 `Creature`를 되찾는 기준이다.
