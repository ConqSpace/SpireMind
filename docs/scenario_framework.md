# 시나리오 평가 프레임워크

이 문서는 SpireMind를 STS2 플레이 AI 시나리오 평가 도구로 정의하고, 비교 가능한 실험을 만들기 위한 기준을 정리한다.

SpireMind의 목표는 플레이어를 돕는 모드가 아니다. 목표는 여러 판단기가 같은 조건에서 STS2를 얼마나 잘 플레이하는지 측정하는 것이다.

```text
상태 추출 -> 판단 요청 -> 행동 제출 -> 게임 실행 -> 로그 저장 -> 지표 계산 -> 판단기 비교
```

## 제품 정체성

SpireMind는 STS2 전투와 런 선택을 표준화된 상태, 행동, 로그, 평가 지표로 변환한다. 그 위에서 휴리스틱, LLM, Codex CLI, 강화학습 정책을 같은 조건으로 비교한다.

따라서 중요한 목표는 “가장 똑똑한 AI를 바로 만들기”가 아니다.

중요한 목표는 다음이다.

- 같은 시작 조건을 반복 실행한다.
- 판단기가 받은 입력과 낸 출력을 모두 저장한다.
- 게임이 실제로 어떻게 변했는지 기록한다.
- 승패뿐 아니라 중간 손실과 오류를 지표로 만든다.
- 여러 판단기를 같은 표로 비교한다.

## 시나리오 단위

시나리오는 하나의 실험 조건이다.

예시:

```json
{
  "scenario_id": "ironclad_act1_seed_001",
  "description": "Ironclad 기본 덱으로 1막 전투와 보상 판단을 측정합니다.",
  "character": "Ironclad",
  "ascension": 0,
  "seed": "ABC123",
  "start_point": "run_start",
  "allowed_systems": ["combat", "rewards", "map"],
  "decision_timeout_ms": 15000,
  "max_retries": 2,
  "max_floors": 17,
  "human_intervention": "none"
}
```

시나리오는 다음을 고정한다.

- 캐릭터
- 시작 덱
- 승천 단계
- 시드
- 시작 지점
- 허용할 선택 영역
- 판단 시간 제한
- 오류 재시도 횟수
- 사람 개입 규칙

## 시나리오 범위

초기에는 전투 시나리오 평가부터 시작한다.

```text
전투 1개
-> 여러 전투 묶음
-> 1막
-> 전체 런
```

각 범위의 목적은 다르다.

| 범위 | 측정 질문 | 주요 지표 |
|---|---|---|
| 단일 턴 | 지금 손패에서 올바른 순서를 고르는가? | 불법 행동률, 피해량, 방어량 |
| 단일 전투 | 전투를 체력 손실 적게 끝내는가? | 체력 손실, 턴 수, 승리 여부 |
| 전투 묶음 | 여러 전투에서 안정적으로 반복되는가? | 평균 체력 손실, 실패율 |
| 1막 | 보상과 경로를 장기적으로 판단하는가? | 보스 도달, 엘리트 수, 덱 품질 |
| 전체 런 | 누적 위험을 관리하는가? | 승패, 도달 층, 총 손실 |

## 판단기 인터페이스

모든 판단기는 같은 입력을 받고 같은 형식으로 응답한다.

```text
Decider
  input: decision_request.json
  output: decision_response.json
```

판단기 후보:

- `heuristic_baseline`
- `random_legal_action`
- `llm_api`
- `codex_cli`
- `resident_agent`
- `rl_policy`
- `human_replay`

## 판단기 비노출 경계

판단기, 특히 LLM에는 비교 실험이라는 사실을 직접 알려주지 않는다.

외부 판단기에 보내지 않는 정보:

- 다른 판단기와 비교 중이라는 설명
- 점수, 순위, 승패 외 평가 기준
- 반복 실행 횟수
- 내부 시나리오 설정 파일명
- 지표 계산 방식
- 사람 개입 여부

외부 판단기에 보내는 정보:

- 현재 게임 상태
- 이번 선택에서 허용되는 행동
- 이전 선택의 짧은 게임 내 요약
- 시간 제한과 응답 형식

즉, 내부 저장소에는 비교와 분석에 필요한 정보를 남기지만, 판단기 입력은 “현재 게임을 플레이하는 데 필요한 정보”로 제한한다.

판단기 입력은 최신 상태와 압축 기억을 포함한다.

```json
{
  "request_id": "decision_000042",
  "play_session_id": "session_0003",
  "decision_type": "combat_turn",
  "state_version": 9,
  "state": {},
  "legal_actions": [],
  "memory_summary": {},
  "constraints": {
    "timeout_ms": 15000,
    "must_choose_legal_action": true,
    "allow_action_batch": true
  }
}
```

판단기 응답은 행동 묶음과 이유를 담는다.

```json
{
  "request_id": "decision_000042",
  "actions": [
    {
      "type": "play_card",
      "combat_card_id": 8,
      "target_combat_id": 1
    },
    {
      "type": "end_turn"
    }
  ],
  "reason": "강타로 취약을 먼저 부여한 뒤 턴을 종료합니다.",
  "confidence": 0.72
}
```

## 실행 규칙

판단기는 게임 객체를 직접 조작하지 않는다.

실행 흐름은 항상 아래 순서를 따른다.

```text
판단기 응답
-> 브리지 검증
-> 모드 claim
-> 게임 실행
-> result 보고
-> 로그 기록
```

브리지는 다음을 거부하거나 보정한다.

- 현재 상태에 없는 카드
- 현재 상태에 없는 대상
- 에너지가 부족한 행동
- `legal_actions`에 없는 행동
- 오래된 `state_version` 기준 행동
- 시간 제한을 넘긴 응답
- JSON 형식이 깨진 응답

행동 묶음 중간에 상태가 바뀌면 브리지는 `combat_card_id`와 `target_combat_id`로 다음 행동을 다시 맞춘다. 다시 맞출 수 없으면 계획을 실패로 닫고 로그에 남긴다.

## 로그 산출물

시나리오 실행 결과는 런 단위 폴더에 저장한다.

```text
runs\
  ironclad_act1_seed_001\
    run_0001\
      scenario_config.json
      decider_config.json
      combat_log.jsonl
      run_log.jsonl
      decisions.jsonl
      metrics.json
      replay_manifest.json
      memory_summary.json
      snapshots\
```

파일 책임:

- `scenario_config.json`: 실험 조건
- `decider_config.json`: 판단기 종류, 모델, 프롬프트, 온도, 시간 제한
- `combat_log.jsonl`: 전투 안에서 실제로 일어난 사건
- `run_log.jsonl`: 런 전체 선택과 결과
- `decisions.jsonl`: 판단 요청, 응답, 검증, 실행 결과
- `metrics.json`: 계산된 지표
- `replay_manifest.json`: 재현에 필요한 파일 목록과 해시
- `memory_summary.json`: 다음 판단에 넣을 압축 기억

자세한 로그 형식은 [run_memory_logging.md](./run_memory_logging.md)를 따른다.

## 지표

### 전투 지표

- 전투 승리 여부
- 전투 턴 수
- 잃은 체력
- 준 피해량
- 막은 피해량
- 사용하지 못하고 버린 에너지
- 오버킬 피해량
- 불법 행동 제안률
- `stale` 재시도 횟수
- 평균 판단 시간

### 런 지표

- 도달 층
- 보스 처치 여부
- 총 체력 손실
- 엘리트 처치 수
- 획득 유물 수
- 덱 크기
- 카드 보상 선택 수
- 보상 건너뛰기 수
- 상점 구매 효율
- 이벤트 선택 손익

### 시스템 지표

- JSON 오류율
- 불법 행동 제안률
- 타임아웃률
- 평균 응답 시간
- 평균 토큰 사용량
- 실행 실패율
- 재시도 후 회복률

## 결과 리포트

시나리오 결과는 판단기별 비교 표로 만든다.

예시:

```text
scenario: ironclad_act1_seed_001

decider              win   floor   hp_lost   avg_time   invalid
heuristic_baseline   no    12      63        18ms       0%
random_legal_action  no    4       80        3ms        0%
llm_api              yes   17      44        4100ms     2%
codex_cli            yes   18      39        7800ms     1%
```

리포트는 승패만 보여주지 않는다. 어떤 판단기가 느리지만 안정적인지, 어떤 판단기가 빠르지만 체력 손실이 큰지 함께 보여준다.

## 재현성 기준

완전한 재현은 STS2 내부 업데이트, 비동기 실행, 게임 엔진 타이밍 때문에 항상 보장되지 않을 수 있다.

대신 다음 수준을 목표로 한다.

1. 같은 시나리오 설정과 같은 판단기 설정을 저장한다.
2. 판단기가 받은 입력과 응답을 모두 저장한다.
3. 실행 결과와 상태 변화를 모두 저장한다.
4. 재실행 시 차이가 생기면 어느 지점에서 갈라졌는지 찾을 수 있다.

재현성을 위해 `replay_manifest.json`에는 다음을 넣는다.

- SpireMind 버전
- STS2 버전
- 모드 DLL 해시
- 시나리오 설정 해시
- 판단기 설정 해시
- 시작 시드
- 실행 시각
- 주요 로그 파일 해시

## 사람 개입 규칙

시나리오는 사람 개입 여부를 명시해야 한다.

허용 값:

- `none`: 사람 개입 없음
- `setup_only`: 실행 전 세팅만 사람이 수행
- `recover_on_crash`: 충돌 복구만 사람이 수행
- `annotated`: 사람이 관찰 메모를 남기지만 선택에는 개입하지 않음

사람이 선택에 개입하면 해당 실행은 자동 비교 표에서 제외한다. 대신 별도 분석용 실행으로 남긴다.

## 실패 처리

실패는 버리지 않는다. 실패도 시나리오 결과다.

실패 유형:

- `invalid_json`
- `invalid_action`
- `timeout`
- `stale_unresolved`
- `executor_failed`
- `game_crash`
- `manual_abort`

각 실패는 `decisions.jsonl`, `run_log.jsonl`, `metrics.json`에 남긴다.

## 초기 구현 순서

1. `scenario_config.json` 형식 고정
2. `decider_config.json` 형식 고정
3. `decisions.jsonl` 기록 구현
4. 전투 단위 `metrics.json` 계산
5. 단일 전투 시나리오 실행 스크립트
6. 여러 판단기 비교 리포트 생성
7. 1막 시나리오 평가로 확장

지금 단계에서는 전체 런 자동화를 바로 목표로 하지 않는다. 먼저 단일 전투와 전투 묶음에서 판단기 비교가 안정적으로 되는지 확인한다.

## 현재 최소 구현

현재 의사결정 루프는 `--run-log-dir` 옵션으로 실행 기록 폴더를 받을 수 있다.

생성하는 파일:

- `scenario_config.json`
- `decider_config.json`
- `decisions.jsonl`
- `combat_log.jsonl`
- `metrics.json`

현재 `metrics.json`에 기록하는 값:

- `decisions`
- `submitted_decisions`
- `dry_run_decisions`
- `plans_completed`
- `plans_failed`
- `actions_applied`
- `stale_retries`
- `invalid_actions`
- `observed_combats`
- `combat_turn_decisions`
- `end_turns_applied`
- `average_decision_ms`

아직 생성하지 않는 파일:

- `run_log.jsonl`
- `replay_manifest.json`
- `memory_summary.json`

현재 `combat_log.jsonl`에 기록하는 최소 이벤트:

- `combat_observed`
- `decision_submitted`
- `action_result_observed`

다음 구현에서는 `combat_log.jsonl`의 전투 시작/종료와 턴 단위 이벤트를 더 촘촘히 남기고, `run_log.jsonl`을 추가한다.
