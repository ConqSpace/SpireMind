# 카드 선택 브리지 설계

## 목적

이 문서는 카드 효과를 직접 재구현하지 않고, 원본 카드 실행 중 발생하는 선택 지점만 LLM legal action으로 바꾸기 위한 설계 기준이다.

핵심 문장은 다음과 같다.

> 어댑터는 카드 효과를 실행하지 않는다. 원본 효과가 멈춰서 플레이어 선택을 요구하는 지점만 LLM action으로 번역한다.

## 반면교사

이전에 특수 카드를 어댑터 쪽에서 직접 처리하려고 했다.

예를 들어 `Armaments`의 방어 획득과 강화, `Headbutt`의 버림 더미 이동, 전투장비 계열 효과를 카드별로 재현하려는 방식이었다. 이 방식은 화면상으로는 맞아 보여도 원본 게임 흐름을 건너뛴다.

문제는 다음과 같다.

- 원본 `OnPlayWrapper`와 `OnPlay`의 실행 순서를 보장하지 못한다.
- `DamageCmd`, `CreatureCmd`, `PowerCmd`, `CardPileCmd`, `CardCmd`가 남기는 전투 기록을 놓친다.
- 권능 훅, 전투 훅, 카드 내부 상태 변화가 빠질 수 있다.
- 같은 카드라도 강화 상태, 대상, 손패 구성, 더미 상태에 따라 원본 분기가 달라진다.
- 카드별 예외 처리를 늘릴수록 원본과 어댑터가 서로 다른 게임을 실행하게 된다.

따라서 카드 효과를 맞추는 방식이 아니라, 원본이 선택을 기다리는 순간만 연결하는 방식으로 가야 한다.

## 설계 원칙

1. 카드는 항상 원본 경로로 사용한다.
   - `TryManualPlay`
   - `PlayCardAction`
   - `OnPlayWrapper`
   - 실제 카드의 `OnPlay`

2. 어댑터는 `CardSelectCmd` 계열 호출만 가로챈다.
   - 카드 효과를 대신 실행하지 않는다.
   - 선택 후보만 상태로 노출한다.
   - LLM의 선택 결과를 원본이 기다리는 반환값으로 돌려준다.

3. 브리지는 카드명이 아니라 선택 API 기준으로 만든다.
   - `CardSelectCmd.FromHandForUpgrade`
   - `CardSelectCmd.FromSimpleGrid`
   - `CardSelectCmd.FromHand`

4. 선택 후보는 안정 ID를 가진다.
   - 같은 이름 카드가 여러 장 있어도 구분 가능해야 한다.
   - 기본 구성은 `pile + index + model_id + upgraded`다.

5. 선택 후 효과는 원본이 계속 실행한다.
   - 어댑터는 선택된 카드 객체만 원본 흐름에 반환한다.
   - 소멸, 강화, 더미 이동, 드로우, 권능 적용은 원본 명령이 수행한다.

## 대상 선택 API

| 선택 API | 현재 대상 카드 | 후보 출처 | 원본 후속 처리 | 구현 상태 |
|---|---|---|---|---|
| `FromHandForUpgrade` | `Armaments` | 손패 중 강화 가능 카드 | `CardCmd.Upgrade` | 구현 및 실전 검증 완료 |
| `FromSimpleGrid` | `Headbutt` | 버림 더미 | `CardPileCmd.Add(...Draw, Top)` | 다음 구현 대상 |
| `FromHand` | `BurningPact`, `TrueGrit+`, `Brand` | 손패 | 대부분 `CardCmd.Exhaust` | 구현 필요 |

## 상태 구조

선택 대기 중에는 일반 전투 턴이 아니라 `adapter_card_selection` 상태를 노출한다.

예시:

```json
{
  "phase": "adapter_card_selection",
  "selection_kind": "from_discard_simple_grid",
  "source_card": {
    "model_id": "CARD.HEADBUTT",
    "name": "Headbutt",
    "upgraded": false
  },
  "candidates": [
    {
      "index": 0,
      "pile": "discard",
      "model_id": "CARD.BASH",
      "name": "Bash",
      "upgraded": false,
      "stable_id": "discard:0:CARD.BASH:base"
    }
  ],
  "legal_actions": [
    {
      "id": "choose_card_selection_adapter_0_bash_base",
      "action_type": "choose_card_selection",
      "selection_index": 0,
      "stable_id": "discard:0:CARD.BASH:base"
    }
  ]
}
```

## 후보 필드

| 필드 | 의미 |
|---|---|
| `index` | 현재 후보 목록 안에서의 위치 |
| `pile` | 후보 카드가 온 더미. 예: `hand`, `discard`, `draw`, `exhaust`, `grid` |
| `model_id` | 카드 모델 ID. 예: `CARD.HEADBUTT` |
| `name` | 표시 이름 |
| `upgraded` | 강화 여부 |
| `cost` | 현재 비용. 가능하면 동적 비용 기준 |
| `type` | 공격, 스킬, 파워 |
| `target_type` | 카드 대상 타입 |
| `stable_id` | LLM이 같은 이름 카드 여러 장을 구분하기 위한 ID |

## 실행 흐름

1. LLM이 카드 사용 legal action을 제출한다.
2. 어댑터는 카드를 원본 경로로 사용한다.
3. 원본 `OnPlay`가 실행된다.
4. 원본이 `CardSelectCmd`를 호출한다.
5. Harmony 패치가 호출을 감지한다.
6. 패치는 후보 카드를 수집하고 `AdapterCardSelectionBridge`에 선택 요청을 등록한다.
7. 상태 출력은 `phase = adapter_card_selection`을 반환한다.
8. LLM이 `choose_card_selection_*` legal action을 제출한다.
9. 브리지는 선택된 원본 카드 객체를 `CardSelectCmd`의 반환값처럼 돌려준다.
10. 원본 `OnPlay`가 이어서 실행된다.
11. 최종 전투 상태를 다시 출력한다.

## 구현 순서

1. `FromSimpleGrid` 브리지
   - 첫 대상은 `Headbutt`.
   - 후보 출처는 버림 더미다.
   - 선택 후 원본이 뽑을 더미 맨 위로 카드를 옮겨야 한다.

2. `FromHand` 브리지
   - 대상은 `BurningPact`, `TrueGrit+`, `Brand`.
   - 후보 출처는 손패다.
   - 선택 목적은 카드별로 다를 수 있으므로 브리지는 목적을 실행하지 않는다.

3. 상태 출력 보강
   - 선택 대기 중인 원본 카드
   - 선택 종류
   - 후보 카드의 더미, 위치, 모델 ID, 강화 여부
   - 선택 후 최신 action 결과

4. 검증 자동화 보강
   - 선택 전 상태
   - 선택 후보 목록
   - 선택 제출 결과
   - 선택 후 손패, 버림 더미, 뽑을 더미, 소멸 더미 변화

## 카드별 검증 기준

| 카드 | 성공 기준 |
|---|---|
| `Headbutt` | 피해가 먼저 들어가고, 선택한 버림 더미 카드가 뽑을 더미 맨 위로 이동한다. |
| `BurningPact` | 선택한 손패 카드가 소멸하고, 원본 수치만큼 드로우한다. |
| `TrueGrit+` | 방어를 먼저 얻고, 선택한 손패 카드가 소멸한다. |
| `Brand` | 자해가 먼저 적용되고, 선택한 손패 카드가 소멸하며, 이후 힘이 증가한다. |
| `Armaments` | 비강화 상태에서 선택한 손패 카드만 강화된다. 강화 상태에서는 손패의 강화 가능 카드가 모두 강화된다. |

## 리스크와 대응

| 리스크 | 대응 |
|---|---|
| 같은 카드가 여러 장 있어 LLM이 잘못 고름 | `pile + index + model_id + upgraded` 기반 안정 ID를 제공한다. |
| 선택 대기 중 전투 상태를 일반 턴으로 오해함 | `phase = adapter_card_selection`을 명확히 출력한다. |
| 브리지가 카드 효과를 다시 실행함 | 브리지는 원본 카드 객체만 반환한다. 효과 실행 코드는 두지 않는다. |
| `FromSimpleGrid` 후보 출처가 손패가 아닐 수 있음 | `selection_kind`와 `pile`을 분리해서 표현한다. |
| 원본 선택 API의 반환 타입이 다름 | API별 패치를 분리하되 내부 요청/응답 구조는 `AdapterCardSelectionBridge`로 통일한다. |

## 관련 문서

- `docs/ironclad_card_effect_reference.md`
- `docs/ironclad_card_execution_classification.md`
- `docs/ironclad_followup_reference_implementation.md`
