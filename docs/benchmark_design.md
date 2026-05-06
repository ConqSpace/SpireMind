# 벤치마크 설계

SpireMind 벤치마크의 목적은 LLM이 STS2를 “잘한다”는 한 줄 결론을 내는 것이 아니다. 목표는 LLM이 어떤 상태를 보고 어떤 행동을 선택했으며, 실패를 다음 시도에서 전략으로 바꾸는지 측정하는 것이다.

## 1. 설계 원칙

모든 벤치마크는 두 실패를 분리해서 기록한다.

```text
실행 실패:
상태 추출, 합법 행동 생성, 행동 제출, 결과 대기, 브리지 연결 문제

전략 실패:
체력 관리, 카드 보상, 지도 경로, 엘리트 진입, 상점 구매, 휴식/강화 판단 문제
```

이 둘을 섞으면 결과 해석이 흐려진다. `invalid_action`, `executor_failed`, `timeout`, `stale_state`는 먼저 실행 안정성 지표로 본다. 그 값이 안정적일 때 장기 전략을 평가한다.

## 2. 벤치마크 계층

| ID | 이름 | 목적 | 종료 조건 |
| --- | --- | --- | --- |
| B0 | 첫 전투 안정성 | 니오우부터 첫 전투 보상까지 실행 루프가 안정적인지 확인 | 첫 전투 보상, 사망, 실행 실패 |
| B1 | 단일 런 장기 진행 | 한 번의 런에서 어디까지 가는지 확인 | game_over, run_finished, 최대 판단 횟수 |
| B2 | handoff N회 반복 | 정해진 반복 횟수 안에서 이전 실패 요약이 다음 런 선택을 바꾸는지 확인 | B1과 동일 |
| B3 | handoff 클리어 반복 | handoff를 이어 붙여 클리어하거나 중단 한도에 도달할 때까지 반복 | run_finished, game_over, 최대 반복 횟수 |
| B4 | 엘리트 가치 판단 | 엘리트를 위험한 투자로 이해하는지 확인 | 지정 범위 또는 game_over |
| B5 | 모듈형 판단 비교 | 단일 프롬프트와 상황별 프롬프트, provider 차이를 비교 | 벤치마크별 종료 조건 |

## 3. B0: 첫 전투 안정성

현재 구현:

```text
benchmarks/B0_NEOW_FIRST_COMBAT
```

범위:

```text
니오우 선택
-> 지도에서 첫 전투 노드 선택
-> 첫 전투 진입
-> 첫 전투 보상 또는 사망
```

통과 기준:

- 5회 중 5회 첫 전투 종료
- `invalid_action = 0`
- `executor_failed = 0`
- `timeout = 0`
- 최종 단계가 `reward` 또는 `game_over`

주의할 점:

- 보상 화면으로 넘어가지 않았는데 종료로 판정하면 안 된다.
- 첫 전투 중 `map` 또는 오래된 이벤트 기록이 섞여도 종료로 오판하지 않아야 한다.

## 4. B1: 단일 런 장기 진행

현재 구현:

```text
benchmarks/B1_FULL_RUN
```

목적은 한 번의 런에서 가능한 한 멀리 진행하는 것이다. 1막으로 제한하지 않는다. 승리보다 먼저 어디서 판단이 무너지는지 본다.

주요 지표:

- `furthest_map_row`
- `final_phase`
- `decision_count`
- `invalid_action_count`
- `stale_state_count`
- `timeout_count`
- `card_reward_pick_count`
- `potion_use_count`
- `rest_choice_count`
- `smith_choice_count`
- `shop_purchase_count`

해석 기준:

- 실행 지표가 깨끗한데 사망했다면 전략 실패로 본다.
- `stale_state`가 많으면 모델 판단 문제가 아니라 상태 변화 타이밍 문제일 수 있다.
- 후반 대형 단일 적에서 사망하면 체력 관리, 방어 우선순위, 포션 사용 시점을 함께 본다.

## 5. B2: Handoff N회 반복

현재 구현:

```text
benchmarks/B2_HANDOFF_N_RUNS
```

B2는 같은 시드를 정해진 횟수만큼 돌리되, 이전 런의 `handoff.json`을 다음 런 입력에 붙인다.

흐름:

```text
run_001 종료
-> run_001/handoff.json 생성
-> run_002 판단 요청에 run_001 handoff 포함
-> run_002 종료
-> run_002/handoff.json 생성
-> run_003 판단 요청에 run_002 handoff 포함
```

handoff는 자유 감상문이 아니라 다음 런에서 적용할 수 있는 전략 메모다.

```json
{
  "schema_version": "handoff.v1",
  "run_summary": {},
  "diagnosis": [],
  "next_run_rules": [],
  "experiment": {},
  "free_note": ""
}
```

`next_run_rules`는 반드시 조건, 행동, 이유를 포함한다.

```json
{
  "condition": "체력 35 이하이고 휴식 지점에 도착했다.",
  "action": "강화보다 회복을 우선한다.",
  "reason": "낮은 체력으로 후반 전투에 들어가면 장기전에서 사망 위험이 커진다.",
  "priority": 1
}
```

핵심 지표:

- `handoff_fact_error_count`: handoff가 실제 로그와 모순되는 수
- `actionable_rule_count`: 다음 런에 적용 가능한 규칙 수
- `rule_follow_rate`: 조건이 발생했을 때 규칙을 따른 비율
- `repeated_failure_count`: 같은 원인으로 다시 죽은 횟수
- `furthest_progress_delta`: 이전 런 대비 진행도 변화

현재 구현은 `handoff.json` 생성과 다음 런 입력 연결까지 제공한다. 규칙 이행률 자동 채점은 다음 단계에서 구현한다.

## 6. B3: Handoff 클리어 반복

현재 구현:

```text
benchmarks/B3_HANDOFF_UNTIL_CLEAR
```

B3는 B2와 같은 handoff 연결 구조를 사용한다. 차이는 반복 횟수를 먼저 정하지 않고, `run_finished`에 도달하거나 사람이 정한 중단 한도에 도달할 때까지 반복한다는 점이다.

핵심 질문:

- 반복 기회가 충분하면 같은 시드에서 전략이 실제로 개선되는가?
- handoff가 단순 회고가 아니라 다음 런의 경로, 보상, 휴식, 엘리트 판단을 바꾸는가?
- 같은 실패 원인이 반복될 때 handoff가 더 구체적인 규칙으로 압축되는가?

주의:

- B3는 시간이 많이 드는 장기 실험이다.
- 자동 실행 전에는 B0와 B1이 안정적으로 통과해야 한다.
- 클리어까지 반복하는 동안 모델, 프롬프트, 상태 요약 정책을 바꾸면 같은 실험으로 비교하지 않는다.

## 7. B4: 엘리트 가치 판단

STS2에서 초반 엘리트는 단기적으로 위험하지만 장기 승리에 중요한 유물 성장 기회다. 따라서 엘리트 판단은 단순히 “위험 회피”가 아니라 조건부 투자로 평가해야 한다.

좋은 판단:

- 체력 55 이상, 포션 있음, 공격 카드 보강됨 -> 엘리트 적극 고려
- 체력 25 이하, 회복 전, 덱 약함 -> 엘리트 회피
- 이전 런에서 유물 부족으로 후반에 밀림 -> 다음 런에서 엘리트 가치 상승

나쁜 판단:

- 엘리트는 위험하니 항상 회피
- 유물이 좋다며 낮은 체력에서도 무조건 진입
- 이전 실패와 무관하게 같은 경로 반복

현재 자동 지표:

- `elite_action_count`: action_id에 `elite`가 포함된 지도 선택 수
- `furthest_map_row`: 선택한 지도 행의 최고값
- `relic_count`: 최종 상태에서 관측된 유물 수

주의:

현재 지도 action_id만으로는 모든 엘리트 노드를 확실히 식별하지 못할 수 있다. 지도 노드 타입 export가 안정화되면 `elite_node_entered_count`를 별도로 추가한다.

## 7. B5: 모듈형 판단과 provider 비교

decider 구조는 provider를 바꿔 끼울 수 있게 분리한다.

현재 지원:

- `command`: 외부 프로세스에 JSON 요청을 전달
- `app-server`: Codex app-server 사용
- `local-http`: OpenAI 호환 `chat/completions` 엔드포인트 사용

비교할 때 지켜야 할 원칙:

- 같은 `DecisionRequest`를 사용한다.
- provider별 차이는 호출 방식과 출력 형식 처리에만 둔다.
- 프롬프트를 바꾸면 provider 비교가 아니라 prompt profile 비교로 분리한다.

## 8. 결과 해석 우선순위

1. 실행 안정성: invalid, executor_failed, timeout
2. 진행도: furthest_map_row, final_phase
3. 생존 판단: final_hp, rest/smith 선택
4. 성장 판단: elite, relic, reward, shop
5. handoff 반영: 다음 런에서 규칙이 실제 선택으로 바뀌었는가

벤치마크의 가장 중요한 질문은 이것이다.

```text
LLM은 같은 실패를 반복하는가,
아니면 실패를 다음 런의 구체적인 선택 규칙으로 바꾸는가?
```
