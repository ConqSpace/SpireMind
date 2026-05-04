# 상주 에이전트 데몬 설계

마지막 갱신: 2026-05-04

## 목표

`scripts/spiremind_agent_daemon.js`는 브리지와 계속 연결된 상태로 게임 상태를 관찰하고, 판단 가능한 순간마다 LLM 판단기 또는 명시적 자동 처리 규칙으로 행동을 제출한다.

핵심 목표는 두 가지다.

- 매번 수동으로 `decision_loop --once`를 다시 실행하지 않는다.
- 벤치마크에서는 쉬운 행동도 LLM에게 맡겨 실제 어댑터 입력 품질을 측정한다.

## 기본 원칙

기본값은 벤치마크 모드다.

이 모드에서는 `continue_run`, `proceed_rewards`, 단일 지도 선택처럼 쉬운 행동도 자동 처리하지 않는다. 모두 `scripts/codex_decider.js`로 전달한다. 이렇게 해야 LLM이 실제로 받는 상태, 합법 행동, 응답 시간을 같은 기준으로 측정할 수 있다.

`game_over`와 `run_finished` 같은 종료 화면은 기본값에서 멈춘다. 종료 확인 행동까지 테스트하려면 `--dismiss-terminal`을 명시한다.

자동 처리가 필요한 장기 실행에서는 `--auto-trivial`을 명시적으로 켠다. 이때도 로그에는 `decision_source`가 남는다.

```text
decision_source=llm
decision_source=llm_app_server
decision_source=auto_trivial
```

따라서 나중에 결과를 볼 때 LLM 판단 성능과 자동 진행 성능을 분리할 수 있다.

## 실행 예시

기존 command 판단기 벤치마크:

```powershell
$runLogDir = Join-Path $env:APPDATA "SlayTheSpire2\SpireMind\runs\agent_benchmark_001"

node .\scripts\spiremind_agent_daemon.js `
  --bridge-url "http://127.0.0.1:17832" `
  --command node `
  --command-arg .\scripts\codex_decider.js `
  --max-decisions 30 `
  --run-log-dir $runLogDir
```

Codex app-server 상주 백엔드:

```powershell
$runLogDir = Join-Path $env:APPDATA "SlayTheSpire2\SpireMind\runs\agent_app_server_001"

node .\scripts\spiremind_agent_daemon.js `
  --bridge-url "http://127.0.0.1:17832" `
  --decision-backend app-server `
  --model gpt-5.4-mini `
  --effort low `
  --max-decisions 30 `
  --run-log-dir $runLogDir
```

단순 행동 자동 처리 모드:

```powershell
$runLogDir = Join-Path $env:APPDATA "SlayTheSpire2\SpireMind\runs\agent_auto_trivial_001"

node .\scripts\spiremind_agent_daemon.js `
  --bridge-url "http://127.0.0.1:17832" `
  --command node `
  --command-arg .\scripts\codex_decider.js `
  --auto-trivial `
  --max-decisions 30 `
  --run-log-dir $runLogDir
```

판단만 확인:

```powershell
node .\scripts\spiremind_agent_daemon.js --dry-run --max-decisions 1
```

## 로그

`--run-log-dir`를 지정하면 다음 파일이 생성된다.

- `agent_config.json`: 실행 설정
- `agent_events.jsonl`: 각 판단과 제출 결과
- `metrics.json`: 판단 횟수, LLM 판단 수, 자동 판단 수, 판단 시간 집계

중요하게 볼 값은 다음과 같다.

- `decision_source`: `llm` 또는 `auto_trivial`
- `decision_ms`: 판단기 호출 또는 자동 판단에 걸린 시간
- `selected_action_id`: 실제 선택한 합법 행동
- `status`: `completed`, `failed`, `dry_run`, `submit_failed`, `result_timeout`

기본 판단 제한 시간은 120초다. 이 값은 `scripts/codex_decider.js`의 기본 제한 시간과 맞춘 것이다. 더 빠른 실패를 보고 싶으면 `--timeout-ms`를 낮춘다.

## 현재 한계

기본 `command` 백엔드는 `scripts/codex_decider.js`를 매 판단마다 command로 호출한다. 이 방식은 기존 비교군으로 남겨둔다.

`app-server` 백엔드는 `codex app-server --listen stdio://`를 한 번만 띄운다. 데몬은 같은 app-server 프로세스에 `turn/start`를 반복으로 보내 판단을 받는다. 이 방식은 Codex 실행 준비 비용을 줄이는 목적이다.

주의할 점:

- app-server 시작 시 로컬 플러그인 동기화 경고가 stderr에 찍힐 수 있다. 판단 자체가 실패하지 않으면 무시해도 된다.
- app-server 백엔드는 입력 크기에 민감하다. 그래서 데몬은 전체 `state.map.full_graph`를 직접 보내지 않고, `path_options_summary`, 현재 노드, 선택 가능 노드, 손패, 적, 보상, 상점, 이벤트, 합법 행동만 압축해서 보낸다.
- `exec-server`는 모델 판단 서버가 아니라 원격 프로세스 실행 서버다. LLM 판단을 상주시킬 기본 경로는 `app-server`다.

벤치마크 기본값은 유지해야 한다. 쉬운 행동을 자동 처리하면 LLM 입력 품질과 판단 시간을 정확히 비교하기 어렵기 때문이다.
