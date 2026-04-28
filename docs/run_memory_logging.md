# 런 기억과 로그 설계

이 문서는 SpireMind가 전투와 런 전체의 선택을 어떻게 기록하고, 그 기록을 LLM 판단 기억으로 어떻게 압축할지 정리한다. 상위 시나리오 평가 구조는 [scenario_framework.md](./scenario_framework.md)를 따른다.

핵심 원칙은 단순하다.

```text
로그 = 실험 원본 기록
메모리 = 판단을 위해 압축한 요약
컨텍스트 = 이번 LLM 요청에 넣는 일부 정보
```

LLM 컨텍스트만 기억으로 쓰면 쉽게 사라진다. 따라서 전투 결과, 보상 선택, 이벤트 선택, 상점 행동, 체력 손실은 모두 파일에 남겨야 한다. LLM에게는 전체 파일을 매번 넣지 않고, 에이전트가 필요한 요약만 골라서 보낸다.

## 파일 구조

런 단위 저장소를 만든다.

```text
%APPDATA%\SlayTheSpire2\SpireMind\runs\
  20260428_160253_<run_id>\
    combat_state_latest.json
    combat_log.jsonl
    run_log.jsonl
    decisions.jsonl
    memory_summary.json
    snapshots\
      state_000001.json
      state_000002.json
```

각 파일의 책임은 다음과 같다.

- `combat_state_latest.json`: 현재 판단용 최신 상태
- `combat_log.jsonl`: 전투 안에서 발생한 턴, 행동, 피해, 종료 이벤트
- `run_log.jsonl`: 막, 지도, 이벤트, 상점, 보상, 휴식 장소 같은 런 전체 이벤트
- `decisions.jsonl`: 판단기에 보낸 입력, 판단기 응답, 브리지 제출 결과, 실행 결과
- `memory_summary.json`: LLM 컨텍스트에 넣기 위한 압축 기억
- `snapshots/`: 중요한 시점의 전체 상태 백업

## Combat Log

`combat_log.jsonl`은 한 전투 안에서 실제로 무슨 일이 일어났는지 기록한다.

주요 이벤트:

- `combat_started`
- `turn_started`
- `decision_submitted`
- `action_applied`
- `turn_ended`
- `combat_ended`

전투 시작 예시:

```json
{
  "event_type": "combat_started",
  "run_id": "run_20260428_160253",
  "combat_id": "combat_ce51558887707aa9",
  "act": 1,
  "floor": 1,
  "room_type": "monster",
  "player_hp": 80,
  "player_max_hp": 80,
  "enemies": [
    {
      "combat_id": 1,
      "name": "오물팽이",
      "hp": 37,
      "max_hp": 37
    }
  ]
}
```

턴 시작 예시:

```json
{
  "event_type": "turn_started",
  "combat_id": "combat_ce51558887707aa9",
  "turn": 1,
  "player": {
    "hp": 80,
    "block": 0,
    "energy": 3
  },
  "hand": [
    {
      "combat_card_id": 0,
      "name": "타격",
      "cost": 1
    },
    {
      "combat_card_id": 3,
      "name": "수비",
      "cost": 1
    }
  ],
  "enemies": [
    {
      "combat_id": 1,
      "name": "오물팽이",
      "hp": 37,
      "block": 0,
      "intent": {
        "type": "attack_debuff",
        "total_damage": 8
      }
    }
  ]
}
```

행동 적용 예시:

```json
{
  "event_type": "action_applied",
  "combat_id": "combat_ce51558887707aa9",
  "turn": 1,
  "action_index": 0,
  "action": {
    "type": "play_card",
    "combat_card_id": 0,
    "card_name": "타격",
    "target_combat_id": 1,
    "target_name": "오물팽이"
  },
  "before": {
    "player_energy": 3,
    "target_hp": 37
  },
  "after": {
    "player_energy": 2,
    "target_hp": 31
  }
}
```

턴 종료 예시:

```json
{
  "event_type": "turn_ended",
  "combat_id": "combat_ce51558887707aa9",
  "turn": 1,
  "cards_played": ["타격", "타격"],
  "hp_lost": 8,
  "player_hp_after": 72,
  "decision_reason": "공격 카드 두 장을 사용한 뒤 턴을 종료합니다."
}
```

## Run Log

`run_log.jsonl`은 전투 밖 선택까지 포함한다. 이것은 장기 전략을 평가하기 위한 원본 기록이다.

주요 이벤트:

- `run_started`
- `act_started`
- `act_bonus_selected`
- `map_node_selected`
- `event_option_selected`
- `shop_action_taken`
- `combat_started`
- `combat_ended`
- `reward_selected`
- `card_reward_skipped`
- `relic_obtained`
- `potion_obtained`
- `rest_site_action_taken`
- `elite_killed`
- `boss_killed`
- `act_ended`
- `run_ended`

막 시작 보너스 예시:

```json
{
  "event_type": "act_bonus_selected",
  "run_id": "run_20260428_160253",
  "act": 1,
  "options": [
    "최대 체력 +7",
    "카드 제거",
    "무작위 희귀 유물"
  ],
  "selected": "무작위 희귀 유물",
  "reason": "초반 전투 안정성을 높이기 위해 유물 보너스를 선택했습니다."
}
```

`?` 방 선택 예시:

```json
{
  "event_type": "event_option_selected",
  "act": 1,
  "floor": 6,
  "room_type": "?",
  "event_name": "알 수 없는 성소",
  "options": [
    {
      "label": "기도한다",
      "preview": "체력 7 손실, 유물 획득"
    },
    {
      "label": "떠난다",
      "preview": "아무 일도 없음"
    }
  ],
  "selected": "기도한다",
  "hp_before": 62,
  "hp_after": 55,
  "rewards": ["새 유물"]
}
```

전투 종료와 보상 예시:

```json
{
  "event_type": "combat_ended",
  "act": 1,
  "floor": 8,
  "room_type": "elite",
  "enemy_group": ["엘리트 이름"],
  "turns": 5,
  "hp_before": 68,
  "hp_after": 41,
  "hp_lost": 27,
  "rewards": {
    "gold": 31,
    "cards_offered": ["카드 A", "카드 B", "카드 C"],
    "card_selected": "카드 B",
    "relic": "새 유물"
  }
}
```

상점 행동 예시:

```json
{
  "event_type": "shop_action_taken",
  "act": 1,
  "floor": 10,
  "gold_before": 177,
  "gold_after": 62,
  "actions": [
    {
      "type": "buy_card",
      "name": "카드 A",
      "cost": 75
    },
    {
      "type": "remove_card",
      "name": "타격",
      "cost": 40
    }
  ]
}
```

## Decisions Log

`decisions.jsonl`은 판단기와 브리지 사이의 계약을 검증하기 위한 기록이다.

각 판단마다 다음을 남긴다.

- 판단 요청 시각
- 사용한 판단기 종류
- 입력 상태 요약
- 입력에 포함한 기억 요약
- 판단기 원본 응답
- 정규화된 행동 묶음
- 브리지 제출 결과
- 실행 결과
- 실패 사유

예시:

```json
{
  "event_type": "decision_recorded",
  "decision_id": "decision_000042",
  "backend": "llm",
  "state_version": 9,
  "combat_id": "combat_ce51558887707aa9",
  "input_summary": {
    "player_hp": 80,
    "player_energy": 3,
    "hand": ["타격", "타격", "타격", "수비", "연쇄"],
    "enemy_intents": ["오물팽이: 8 피해와 디버프"]
  },
  "memory_summary": {
    "recent_hp_loss": 0,
    "deck_plan": "취약을 부여한 뒤 공격 카드로 빠르게 전투를 끝냅니다."
  },
  "decision": {
    "actions": [
      {
        "type": "play_card",
        "combat_card_id": 0,
        "target_combat_id": 1
      },
      {
        "type": "end_turn"
      }
    ],
    "reason": "현재 손패에서 즉시 피해를 줄 수 있는 카드를 사용합니다."
  },
  "bridge_result": {
    "status": "submitted",
    "plan_id": "plan_000042"
  },
  "final_result": {
    "status": "completed",
    "hp_lost": 8
  }
}
```

## Memory Summary

`memory_summary.json`은 LLM 입력에 넣는 압축 기억이다. 원본 로그를 대체하지 않는다.

예시:

```json
{
  "run_id": "run_20260428_160253",
  "act": 1,
  "floor": 8,
  "deck_plan": "취약 후 공격 카드로 초반 전투를 빠르게 끝내는 방향",
  "important_pickups": [
    "무작위 희귀 유물",
    "공격 카드 B"
  ],
  "recent_losses": [
    "6층 ? 방에서 체력 7 손실",
    "8층 엘리트에서 체력 27 손실"
  ],
  "risk_notes": [
    "현재 체력 41/80이라 다음 엘리트 진입은 위험합니다."
  ],
  "combat_notes": [
    "오물팽이는 지난 턴 공격과 디버프 의도를 보였습니다."
  ]
}
```

요약은 아래 상황에서 갱신한다.

- 전투 종료
- 보상 선택 완료
- 이벤트 선택 완료
- 상점 이탈
- 휴식 장소 행동 완료
- 막 종료

## LLM 입력에 넣는 범위

LLM 요청에는 다음만 넣는다.

- 최신 `combat_state`
- 현재 선택 가능한 `legal_actions`
- 최근 전투 1개 또는 최근 턴 3개 요약
- 현재 막과 층의 런 요약
- 큰 체력 손실, 중요한 보상, 위험 메모

넣지 않는 것:

- 전체 `combat_log.jsonl`
- 전체 `run_log.jsonl`
- 모든 과거 스냅샷

이렇게 해야 응답 시간이 안정된다. 또한 LLM이 오래된 사건에 과하게 매달리는 일을 줄일 수 있다.

## 최소 구현 범위

처음부터 모든 방과 보상을 추적하지 않는다. 다음 순서로 잠근다.

1. `decisions.jsonl`
   - 판단 입력, 판단 응답, 제출 결과, 실행 결과를 남긴다.
   - 현재 `--run-log-dir` 옵션으로 최소 구현이 들어가 있다.

2. `combat_log.jsonl`
   - 전투 시작, 턴 시작, 행동 적용, 턴 종료, 전투 종료를 남긴다.
   - 현재는 `combat_observed`, `decision_submitted`, `action_result_observed` 최소 이벤트만 구현되어 있다.

3. `run_log.jsonl` 골격
   - 이벤트 이름과 파일 형식을 먼저 고정한다.
   - 실제 필드 수집은 보상, 지도, 상점, 이벤트 순서로 넓힌다.

4. `memory_summary.json`
   - 전투와 런 로그를 바탕으로 LLM 입력용 요약을 만든다.
   - 현재는 `decisions.jsonl`과 `combat_log.jsonl`에서 판단 수, 실행 행동 수, 턴 종료 수, 누적 체력 손실, 누적 적 체력 감소, 최근 판단 메모, 위험 메모를 압축해 저장한다.
   - `command` 모드에서 `--run-log-dir`를 쓰면 이 요약이 `recent_history.memory_summary`로 판단기에 전달된다.

## 열어둘 범위

다음 항목은 구조만 열어두고, 수집 가능성이 확인될 때 구현한다.

- 막 시작 보너스 선택지 전체와 선택 결과
- 지도 후보 경로와 실제 선택 노드
- `?` 방 선택지와 결과
- 상점의 구매, 제거, 건너뛰기
- 전투 보상 선택과 건너뛰기
- 유물, 포션, 골드 획득
- 휴식 장소에서 휴식 또는 강화 선택
- 엘리트와 보스 처치 결과
- 체력 손실 원인 분해

이 항목들은 LLM 성능 평가에 중요하다. 다만 지금 단계에서는 전투 자동 실행 안정화가 먼저다.
