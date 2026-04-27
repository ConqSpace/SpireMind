# 행동 목록과 LLM 응답 형식

이 문서는 LLM이 선택할 수 있는 행동 목록과 응답 형식을 정의한다.

LLM은 게임 명령을 직접 만들지 않는다. 모드가 만든 `legal_actions` 안에서 하나의 `action_id`만 고른다. 이렇게 해야 판단 오류와 실행 오류를 분리할 수 있다.

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
    "summary": "Play Strike on Cultist."
  },
  {
    "action_id": "end_turn",
    "type": "end_turn",
    "summary": "End the current turn."
  }
]
```

## action_id 규칙

- `action_id`는 현재 `state_id` 안에서만 유효하다.
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
  "summary": "Play Bash on Jaw Worm."
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
  "summary": "Play Battle Trance."
}
```

### end_turn

```json
{
  "action_id": "end_turn",
  "type": "end_turn",
  "summary": "End the current turn."
}
```

## 나중에 추가할 행동 종류

아래 행동은 1차 자동 전투가 안정화된 뒤 추가한다.

- `use_potion`
- `choose_card_reward`
- `skip_reward`
- `choose_map_node`
- `buy_shop_item`
- `remove_card`
- `upgrade_card`
- `rest`
- `choose_event_option`

## LLM 응답 형식

LLM은 반드시 JSON 하나만 반환한다.

```json
{
  "state_id": "run_ABC123_floor_06_combat_02_turn_03_step_01",
  "selected_action_id": "play_hand_2_enemy_1",
  "reason": "적이 공격하지 않는 턴이라 취약을 먼저 적용해 다음 공격 가치를 높인다.",
  "risk_note": "이번 턴 방어를 만들지 않으므로 다음 턴 큰 공격이 오면 위험하다.",
  "confidence": 0.72
}
```

## 응답 필드 규칙

- `state_id`: 요청의 `state_id`를 그대로 반환한다.
- `selected_action_id`: `legal_actions` 중 하나여야 한다.
- `reason`: 선택 이유다. 실행에는 사용하지 않는다.
- `risk_note`: 선택의 위험을 짧게 적는다. 없으면 빈 문자열을 허용한다.
- `confidence`: 0.0 이상 1.0 이하의 자기 평가다. 실행 판단에는 사용하지 않고 분석에만 쓴다.

## 턴 단위와 행동 단위

1차 구현은 행동 단위로 진행한다.

```text
상태 추출
-> LLM이 다음 행동 1개 선택
-> 모드가 실행
-> 새 상태 추출
-> 다시 LLM 호출
```

이 방식을 먼저 쓰는 이유는 명확하다.

- 카드 한 장이 더미, 에너지, 적 체력, 버프를 즉시 바꾼다.
- 한 턴 전체 계획을 한 번에 받으면 중간 상태 변화로 계획이 무효가 될 수 있다.
- 실패한 행동을 어느 순간에 골랐는지 쉽게 추적할 수 있다.

## 합법 행동 생성 규칙

모드는 다음 조건을 모두 만족하는 행동만 `legal_actions`에 넣는다.

- 카드가 현재 손패에 있다.
- 카드가 현재 비용 기준으로 사용 가능하다.
- 대상이 필요한 카드라면 살아 있는 유효 대상이 있다.
- 대상이 필요 없는 카드라면 `target_id`는 `null`이다.
- 게임 상태가 플레이어 행동을 받을 수 있는 시점이다.

## 실행 전 검증

LLM 응답을 받으면 모드는 실행 전에 다시 확인한다.

- 응답 JSON 파싱 가능 여부
- `state_id` 일치 여부
- `selected_action_id` 존재 여부
- 현재 상태에서도 그 행동이 여전히 가능한지

검증 실패 시 처리 방식은 [failure_policy.md](./failure_policy.md)를 따른다.
