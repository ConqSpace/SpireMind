# 벤치마크 구현 설계

이 문서는 [benchmark_design.md](./benchmark_design.md)를 실제 코드와 실행 절차로 옮기기 위한 구현 설계다.

핵심 방향은 기존 브리지와 상주 에이전트 데몬을 최대한 재사용하는 것이다. 새 시스템을 크게 만들기보다, 벤치마크 실행 조건과 종료 조건을 관리하는 얇은 계층을 추가한다.

## 1. 구현 목표

1차 구현 목표는 `B0_NEOW_FIRST_COMBAT`를 안정적으로 실행하고 비교 가능한 로그를 남기는 것이다.

```text
새 런 시작
-> 니오우 선택
-> 첫 전투 노드 이동
-> 첫 전투 진입
-> 첫 전투 종료
-> 실행 요약 생성
```

이 단계에서는 시드 자동 주입, 여러 판단기 대량 실행, 전체 런 리포트까지 한 번에 만들지 않는다. 먼저 단일 시드와 단일 판단기 실행을 정확히 멈추고 기록하는 것이 우선이다.

## 2. 기존 구성 재사용

현재 재사용할 수 있는 구성은 다음과 같다.

```text
STS2 모드:
- 상태 export
- legal_actions 생성
- 행동 claim
- 행동 실행
- result 보고

bridge/spiremind_bridge.js:
- 최신 상태 저장
- 행동 제출 검증
- claim/result 관리
- 브리지 이벤트 로그

scripts/spiremind_agent_daemon.js:
- 상태 폴링
- LLM 또는 command 판단기 호출
- 행동 제출
- 결과 대기
- agent_events.jsonl 기록
- metrics.json 기록
```

새로 필요한 것은 “벤치마크 실행 관리자”다. 이 관리자는 게임을 직접 조작하지 않는다. 브리지 상태를 관찰하고, 정해진 종료 조건에 도달하면 데몬 실행을 멈추고 요약을 만든다.

## 3. 새 구성요소

### 3.1 벤치마크 설정 파일

권장 위치:

```text
benchmarks/
  B0_NEOW_FIRST_COMBAT/
    scenario_config.json
    seeds.json
    deciders/
      llm_current.json
```

`scenario_config.json` 예시:

```json
{
  "benchmark_id": "B0_NEOW_FIRST_COMBAT",
  "description": "단일 시드 니오우 선택부터 첫 전투 종료까지 검증한다.",
  "character": "Ironclad",
  "ascension": 0,
  "seed_id": "seed_0001",
  "stop_rule": "first_combat_finished",
  "max_decisions": 30,
  "timeout_ms": 120000,
  "result_timeout_ms": 60000,
  "auto_trivial": false,
  "dismiss_terminal": false,
  "human_intervention": "setup_only"
}
```

`seeds.json` 예시:

```json
{
  "seeds": [
    {
      "seed_id": "seed_0001",
      "seed": "TO_BE_FILLED_AFTER_GAME_SUPPORT",
      "notes": "첫 B0 고정 시드"
    }
  ]
}
```

`deciders/llm_current.json` 예시:

```json
{
  "decider_id": "llm_current",
  "decision_backend": "app-server",
  "model": "gpt-5.4-mini",
  "effort": "low",
  "prompt_version": "current",
  "command": null,
  "command_args": []
}
```

시드 자동 주입은 모드의 `start_new_run` 자동 테스트 명령을 사용한다. `force_abandon=true`를 함께 보내면 진행 중인 런을 먼저 포기하고, 메인 메뉴로 돌아온 뒤 같은 명령 안에서 고정 시드 새 런을 시작한다.

```json
{
  "id": "cmd-start-seeded-...",
  "action": "start_new_run",
  "params": {
    "seed": "7MJCUHEB5Q",
    "character_id": "Ironclad",
    "force_abandon": true,
    "timeout_ms": 180000,
    "ready_timeout_ms": 180000
  }
}
```

`force_abandon`은 기본값이 `false`다. 이 값이 없으면 기존처럼 이미 진행 중인 런의 상태 파일 준비만 확인한다. 진행 중인 전투를 버리는 작업은 되돌리기 어렵기 때문에, 벤치마크 준비 단계에서만 명시적으로 켠다.

### 3.2 벤치마크 실행 스크립트

권장 파일:

```text
scripts/run_benchmark.js
```

책임:

- `scenario_config.json`과 `decider_config.json`을 읽는다.
- 실행 폴더를 만든다.
- `scripts/spiremind_agent_daemon.js`를 자식 프로세스로 실행한다.
- 브리지의 `/state/current`를 폴링해 종료 조건을 감시한다.
- 종료 조건을 만족하면 데몬을 종료한다.
- 실행 요약 파일을 생성한다.

첫 구현에서 이 스크립트는 게임 실행과 브리지 실행을 자동화하지 않는다. 사용자가 브리지와 게임을 준비한 뒤 실행한다. 이렇게 하면 자동 실행 실패와 벤치마크 실패를 구분하기 쉽다. 고정 시드 런 준비는 `runtime_smoke_check.ps1 -StartSeededRun` 또는 브리지의 `start_new_run` 명령으로 별도 수행한다.

실행 예시:

```powershell
node .\scripts\run_benchmark.js `
  --benchmark-dir .\benchmarks\B0_NEOW_FIRST_COMBAT `
  --decider llm_current `
  --seed seed_0001 `
  --repeat-index 1 `
  --bridge-url http://127.0.0.1:17832
```

### 3.3 종료 조건 판정기

B0의 종료 조건은 “첫 전투가 끝났다”이다.

상태 흐름은 다음처럼 판정한다.

```text
neow_seen:
phase가 니오우 또는 이벤트/보상 계열이고, 니오우 선택 legal_actions가 관찰됨

map_seen_after_neow:
니오우 선택 이후 phase가 map으로 전환됨

first_combat_seen:
map 이후 phase가 combat으로 전환됨

first_combat_finished:
first_combat_seen 이후 phase가 reward, map, event, treasure, shop, game_over 중 하나로 전환됨
```

정확한 phase 이름은 현재 export 결과를 기준으로 보정한다. 초기 구현에서는 phase 문자열과 `room_context`를 함께 본다.

판정 상태 예시:

```json
{
  "neow_seen": true,
  "map_seen_after_neow": true,
  "first_combat_seen": true,
  "first_combat_finished": false,
  "last_phase": "combat",
  "last_state_id": "combat_..."
}
```

### 3.4 실행 요약 파일

각 실행 폴더에는 아래 파일을 추가한다.

```text
benchmark_run_summary.json
```

예시:

```json
{
  "benchmark_id": "B0_NEOW_FIRST_COMBAT",
  "seed_id": "seed_0001",
  "decider_id": "llm_current",
  "repeat_index": 1,
  "status": "completed",
  "stop_reason": "first_combat_finished",
  "started_at": "2026-05-05T00:00:00.000Z",
  "finished_at": "2026-05-05T00:05:00.000Z",
  "phase_trace": ["neow", "map", "combat", "reward"],
  "adapter": {
    "invalid_action_count": 0,
    "executor_failed_count": 0,
    "stale_state_count": 0,
    "timeout_count": 0
  },
  "combat": {
    "first_combat_finished": true,
    "first_combat_won": true,
    "hp_lost": null,
    "turn_count": null
  }
}
```

초기에는 `hp_lost`, `turn_count`가 `null`이어도 된다. 먼저 종료 판정과 실행 파일 구조를 잠근 뒤, `agent_events.jsonl`과 상태 스냅샷에서 계산하도록 확장한다.

## 4. 데이터 흐름

B0 실행 흐름은 다음과 같다.

```text
사용자:
브리지 실행
게임 실행
고정 시드 새 런 준비

run_benchmark.js:
설정 읽기
run 폴더 생성
agent daemon 실행
브리지 상태 폴링
종료 조건 감시
요약 생성

agent daemon:
LLM 판단 요청
행동 제출
결과 대기
agent_events.jsonl 기록
metrics.json 기록

STS2 모드:
상태 export
행동 claim
게임 내부 실행
result 보고
```

## 5. 폴더 구조

권장 실행 산출물:

```text
benchmarks/
  B0_NEOW_FIRST_COMBAT/
    scenario_config.json
    seeds.json
    deciders/
      llm_current.json
    runs/
      seed_0001/
        llm_current_run_001/
          scenario_config.json
          decider_config.json
          agent_config.json
          agent_events.jsonl
          metrics.json
          benchmark_run_summary.json
```

`scenario_config.json`과 `decider_config.json`은 실행 폴더에도 복사한다. 원본 설정이 나중에 바뀌어도 과거 실행 조건을 재현하기 위해서다.

## 6. 구현 단계

### 6.1 1단계: 실행 관리자 골격

추가할 것:

- `scripts/run_benchmark.js`
- `benchmarks/B0_NEOW_FIRST_COMBAT/scenario_config.json`
- `benchmarks/B0_NEOW_FIRST_COMBAT/seeds.json`
- `benchmarks/B0_NEOW_FIRST_COMBAT/deciders/llm_current.json`

동작:

- 설정 파일을 읽는다.
- 실행 폴더를 만든다.
- `spiremind_agent_daemon.js --self-test` 수준의 dry 확인을 할 수 있다.
- `--dry-run` 옵션으로 데몬 호출 없이 폴더와 요약 파일만 생성할 수 있다.

### 6.2 2단계: 브리지 상태 감시

추가할 것:

- `/state/current` 폴링
- phase trace 기록
- B0 종료 조건 판정

동작:

- 브리지가 꺼져 있으면 명확한 오류로 종료한다.
- 상태가 없으면 대기한다.
- 첫 전투 종료를 감지하면 `stop_reason=first_combat_finished`로 요약한다.

### 6.3 3단계: 데몬 실행 통합

추가할 것:

- `spiremind_agent_daemon.js` 자식 프로세스 실행
- 데몬 표준 출력과 오류 로그 저장
- 종료 조건 도달 시 데몬 종료

주의:

- 데몬을 강제 종료하더라도 실행 결과는 이미 `agent_events.jsonl`에 남아 있어야 한다.
- 종료 직전 최신 `/action/latest`를 한 번 더 저장한다.

### 6.4 4단계: 요약 지표 계산

추가할 것:

- `agent_events.jsonl` 파싱
- `metrics.json` 병합
- adapter 안정성 지표 계산
- phase trace 기반 절차 지표 계산

초기 자동 계산 항목:

- decision_count
- invalid_action_count
- executor_failed_count
- stale_state_count
- timeout_count
- first_combat_seen
- first_combat_finished
- final_phase

전투 손실 체력과 턴 수는 상태 스냅샷 구조가 충분히 안정화된 뒤 계산한다.

## 7. 장기 테스트 확장 설계

B1 이후는 같은 실행 관리자를 재사용하고 `stop_rule`만 바꾼다.

```text
B1 stop_rule: act1_midpoint_or_max_decisions
B2 stop_rule: act1_boss_finished_or_death
B3 stop_rule: act1_boss_finished_or_death, seeds 여러 개
B4 stop_rule: run_finished_or_death
```

장기 테스트에서 추가해야 하는 필드:

- 현재 층
- 현재 막
- 보스 도달 여부
- 사망 여부
- 엘리트 처치 수
- 카드 보상 선택 이력
- 상점 선택 이력
- 이벤트 선택 이력

이 값들은 가능하면 `CombatStateExporter`의 상태 JSON에서 직접 읽는다. 상태 JSON에 없으면 먼저 export 필드를 보강한 뒤 지표를 계산한다.

## 8. 리스크와 대안

### 리스크: 진행 중인 런 포기가 기존 저장을 삭제함

대응:

- `force_abandon` 기본값은 `false`로 둔다.
- 벤치마크 준비 명령에서만 `force_abandon=true`를 명시한다.
- 결과 진단 정보에 `force_abandon`, `abandon_is_abandoned`, `return_to_main_menu_task_status`를 남긴다.

### 리스크: phase 이름이 실제 export와 다를 수 있음

대응:

- 종료 판정은 phase 하나에만 의존하지 않는다.
- `room_context`, `state_id`, `legal_actions` 타입을 함께 본다.
- 초기 실행의 phase trace를 보고 판정 규칙을 보정한다.

### 리스크: 데몬 종료 시점에 마지막 로그가 덜 써질 수 있음

대응:

- 종료 조건 감지 후 짧은 유예 시간을 둔다.
- `/action/latest`와 `/state/current`를 마지막으로 저장한다.
- 데몬 종료는 정상 SIGTERM을 먼저 보내고, 제한 시간 이후에만 강제 종료한다.

### 리스크: 전투 결과 지표를 바로 계산하기 어려움

대응:

- 1차 요약은 절차 지표 중심으로 만든다.
- 체력 손실, 턴 수, 에너지 낭비는 후속 단계에서 추가한다.
- 지표 계산이 안 되는 항목은 `null`로 남기고, 누락 이유를 적는다.

## 9. 완료 기준

1차 구현 완료 기준:

- B0 설정 파일이 존재한다.
- 벤치마크 실행 폴더가 정해진 구조로 생성된다.
- 브리지 상태를 읽어 phase trace를 기록한다.
- 첫 전투 종료 조건을 감지한다.
- `benchmark_run_summary.json`이 생성된다.
- 데몬 self-test와 기존 모드 빌드가 통과한다.

이 기준을 만족하면 다음 단계에서 실제 게임을 켜고 B0 1회 실행을 검증한다.
