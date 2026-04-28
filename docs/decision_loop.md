# 의사결정 루프

이 문서는 `bridge/spiremind_decision_loop.js`의 역할과 실행 방법을 정리한다.

의사결정 루프의 목적은 브리지에 쌓인 최신 전투 상태를 읽고, 그 상태에 맞는 행동 묶음을 다시 브리지에 제출하는 것이다. 게임 조작은 직접 하지 않는다. 실제 실행은 STS2 모드가 `/action/claim`으로 행동을 확보한 뒤 처리한다.

## 현재 목표

- 브리지의 `/state/current`를 반복 조회한다.
- 아직 처리 중인 행동이 없고 전투 상태가 준비됐을 때만 판단한다.
- `combat_card_id`, `target_combat_id` 기반 행동을 제출한다.
- 여러 장의 카드를 순서대로 쓰는 계획을 `actions` 배열로 제출한다.
- 마지막에 `end_turn`을 붙여 한 턴 단위 실행을 닫는다.
- 판단 입력과 결과는 나중에 `decisions.jsonl`에 남긴다. 자세한 로그 설계는 [run_memory_logging.md](./run_memory_logging.md)를 따른다.

## 실행 모드

### `heuristic`

현재 검증용 기본 모드다.

사용 가능한 공격 카드를 에너지 범위 안에서 앞에서부터 고른다. 그 뒤 `end_turn`을 붙인다. 이 모드는 강한 플레이를 목표로 하지 않는다. 브리지, 모드, 행동 실행 흐름이 안정적으로 연결됐는지 확인하는 기준선이다.

```powershell
node .\bridge\spiremind_decision_loop.js --mode heuristic --once --max-actions-per-turn 2
```

실행 전 판단만 확인할 때:

```powershell
node .\bridge\spiremind_decision_loop.js --mode heuristic --once --dry-run
```

실행 결과까지 기다리고 기록을 남길 때:

```powershell
node .\bridge\spiremind_decision_loop.js `
  --mode heuristic `
  --once `
  --wait-result `
  --scenario-id "manual_combat" `
  --play-session-id "session_001" `
  --run-log-dir "$env:APPDATA\SlayTheSpire2\SpireMind\runs\manual_001"
```

전투가 끝날 때까지 반복 판단할 때:

```powershell
node .\bridge\spiremind_decision_loop.js `
  --mode command `
  --command node `
  --command-arg .\scripts\codex_decider.js `
  --until-combat-end `
  --wait-result `
  --max-decisions 20 `
  --run-log-dir "$env:APPDATA\SlayTheSpire2\SpireMind\runs\combat_001"
```

이 모드는 `phase`가 `combat_turn`이고 살아 있는 적과 가능한 행동이 있을 때만 판단한다. 적 턴, 애니메이션, 전환처럼 아직 판단할 수 없는 전투 상태는 기다린다. 플레이어가 쓰러졌거나, 살아 있는 적이 없거나, 전투 밖 화면으로 넘어가면 `combat_loop_stopped` 이벤트를 남기고 멈춘다.

### `command`

LLM 또는 Codex CLI 같은 외부 판단기를 붙이기 위한 모드다.

의사결정 루프는 현재 상태와 규칙을 JSON 프롬프트로 외부 명령의 stdin에 전달한다. 외부 명령은 stdout으로 JSON만 반환해야 한다.

```powershell
node .\bridge\spiremind_decision_loop.js `
  --mode command `
  --command node `
  --command-arg .\scripts\my_decider.js
```

Codex CLI를 붙일 때는 `scripts/codex_decider.js`를 사용한다. 자세한 내용은 [codex_decider.md](./codex_decider.md)를 따른다.

반환 형식:

```json
{
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
  "reason": "공격 카드를 먼저 사용한 뒤 턴을 종료합니다."
}
```

단일 행동만 제출할 수도 있다.

```json
{
  "selected_action_id": "end_turn",
  "reason": "남은 에너지로 의미 있는 행동이 없습니다."
}
```

## 상태 변경 대응

카드를 여러 장 순서대로 쓰면 한 장을 쓸 때마다 손패, 에너지, 적 체력, `state_version`이 바뀐다.

브리지는 계획의 다음 행동을 실행 직전 최신 상태에 다시 맞춘다. `combat_card_id`와 `target_combat_id`를 우선 사용하므로, 같은 이름 카드가 여러 장 있어도 어떤 전투 카드였는지 구분할 수 있다.

실행자가 행동을 확보하려는 순간 상태가 바뀌면 `/action/claim`이 `stale`이 될 수 있다. 이 경우 브리지는 같은 계획 단계를 최신 `legal_actions` 기준으로 최대 2회 다시 제출한다. 다시 맞출 수 없으면 계획을 실패로 닫는다.

## 판단 전후 기록

`--wait-result`와 `--run-log-dir`를 함께 쓰면 `decisions.jsonl`의 각 판단 기록에 다음 필드가 추가된다.

- `state_summary`: 판단을 만들 때 본 상태 요약
- `after_state_summary`: 행동 결과를 확인한 뒤 다시 읽은 상태 요약
- `state_delta`: 판단 전후 변화량

`state_delta`에는 플레이어 체력 손실, 방어도 변화, 에너지 소비, 손패 수 변화, 살아 있는 적 수 변화, 적 전체 체력 감소량, 적별 체력 변화가 들어간다. 이 값은 판단기가 좋은 선택을 했는지 나중에 읽기 위한 최소 기록이다.

같은 정보는 `combat_log.jsonl`의 `action_result_observed` 이벤트에도 남는다. 따라서 한 판단이 어떤 행동을 냈고, 그 행동 뒤에 전투 상태가 어떻게 바뀌었는지 한 줄 단위로 추적할 수 있다.

## 최근 기록 전달

`command` 모드에서 `--run-log-dir`를 함께 쓰면 의사결정 루프는 최근 `combat_log.jsonl`과 `decisions.jsonl` 일부를 읽어 외부 판단기에 `recent_history`로 보낸다.

기본 전달 개수는 최근 8개다. 바꾸려면 다음 옵션을 쓴다.

```powershell
node .\bridge\spiremind_decision_loop.js `
  --mode command `
  --command node `
  --command-arg .\scripts\codex_decider.js `
  --recent-history-limit 12 `
  --run-log-dir "$env:APPDATA\SlayTheSpire2\SpireMind\runs\combat_001"
```

`recent_history`에는 판단 결과, 행동 결과, `state_delta`가 들어간다. 판단기는 이전 턴에 체력을 얼마나 잃었는지, 적 체력을 얼마나 줄였는지, 어떤 계획이 실패했는지 참고할 수 있다. 단, 현재 턴에서 실제로 실행 가능한 행동은 항상 최신 `legal_actions`가 결정한다.

## 검증

브리지와 의사결정 루프만 빠르게 확인할 때:

```powershell
.\scripts\decision_loop_smoke_check.ps1
```

이 검증은 다음을 확인한다.

- 휴리스틱 판단이 JSON으로 생성된다.
- 행동 묶음이 `/action/submit`으로 제출된다.
- `--run-log-dir` 지정 시 `scenario_config.json`, `decider_config.json`, `decisions.jsonl`, `combat_log.jsonl`, `metrics.json`이 생성된다.
- `--wait-result` 지정 시 제출한 행동의 실행 결과까지 기다린다.
- 일부러 만든 `stale` claim 상황에서 같은 계획 단계가 다시 큐에 들어간다.

실제 게임까지 포함한 검증은 다음 흐름으로 본다.

1. 브리지를 실행한다.
2. STS2를 실행한다.
3. `continue_run` 자동 명령으로 전투에 진입한다.
4. 의사결정 루프를 `--once`로 실행한다.
5. 브리지의 `/action/latest`와 게임 로그에서 카드 사용, 턴 종료, 계획 상태를 확인한다.

## 현재 한계

- `heuristic`은 공격 카드 중심 검증용 판단기다.
- X 비용 카드, 에너지 생성 카드, 드로우 후 추가 행동 가치는 아직 정교하게 계산하지 않는다.
- `command` 모드는 외부 판단기의 실행 형식만 제공한다. Codex CLI용 장기 상주 세션 연결은 다음 단계에서 별도 설계가 필요하다.
- 현재 전투 로그는 `combat_observed`, `decision_submitted`, `action_result_observed`만 남긴다. 전투 시작/종료, 턴 시작/종료, 피해량 변화 같은 세부 이벤트 수집은 아직 별도 구현이 필요하다.
