# 행동 묶음 실행 형식

LLM은 여러 행동을 순서대로 제안할 수 있다. 브리지는 행동 묶음 전체를 한 번에 믿고 실행하지 않는다. 각 행동 직전에 최신 전투 상태를 기준으로 다시 검증한다. 이전 행동 실행 결과와 새 상태 갱신을 확인한 뒤 다음 행동을 제출한다.

```json
{
  "actions": [
    { "type": "play_card", "card_id": "hand_0_bash", "target_index": 0 },
    { "type": "play_card", "card_id": "hand_1_strike", "target_index": 0 },
    { "type": "end_turn" }
  ],
  "reason": "취약을 먼저 적용한 뒤 공격하고 턴을 종료한다."
}
```

## instance_id 규칙

- `combat_state.json`의 `piles.hand[]` 항목에는 `instance_id`가 있다.
- `card_id`는 이 `instance_id`와 비교한다.
- 카드 런타임 식별자, UUID, GUID 같은 값이 있으면 그것을 우선 사용한다.
- 그런 값이 없으면 `hand_0_Strike`처럼 현재 상태 안에서 카드를 구분할 수 있는 값을 사용한다.
- 같은 이름의 카드가 여러 장 있을 수 있으므로 LLM은 카드 이름보다 `instance_id`를 우선 사용해야 한다.

## 실패 뒤 재요청 흐름

- 브리지는 현재 손패, 에너지, 대상 범위, `legal_actions` 존재 여부를 확인한다.
- 중간 행동이 실패, 만료, 미지원으로 끝나면 남은 행동은 제출하지 않는다.
- LLM은 최신 상태를 다시 읽고, 남은 의도를 새 `actions` 묶음으로 다시 제출한다.

현재 모드 실행기는 `play_card`와 `end_turn`을 모두 처리할 수 있다. 브리지는 `play_card` 묶음을 한 번에 실행하지 않고, 각 행동 결과와 다음 상태 갱신을 기다린 뒤 다음 행동을 다시 검증해 제출한다.
# 행동 묶음 실행 형식

LLM은 여러 행동을 `actions` 배열로 제출할 수 있다. 브리지는 첫 행동만 즉시
검증해 제출하고, 모드가 `applied`를 보고한 뒤 새 전투 상태가 들어오면 다음 행동을
다시 검증해 제출한다.

계획 단계에서는 손패 순서가 바뀔 수 있으므로 `hand_index`보다 `combat_card_id`를
우선 사용한다. 대상도 가능하면 `target_combat_id`를 사용한다.

```json
{
  "actions": [
    { "type": "play_card", "combat_card_id": 0, "target_combat_id": 1 },
    { "type": "play_card", "combat_card_id": 1, "target_combat_id": 1 },
    { "type": "end_turn" }
  ],
  "reason": "공격 카드 두 장을 순서대로 사용한 뒤 턴을 종료한다."
}
```

각 단계는 실행 직전 최신 `legal_actions`에 다시 존재해야 한다. 카드가 이미
손패에서 사라졌거나 에너지가 부족하거나 대상이 사라졌으면 계획은 실패 상태로 멈춘다.
