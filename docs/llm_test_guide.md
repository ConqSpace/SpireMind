# LLM 테스트 가이드

마지막 갱신: 2026-05-03

이 문서는 SpireMind에서 LLM 판단기를 붙여 테스트하는 절차를 정리한다. 목표는 LLM이 현재 게임 상태와 `legal_actions`를 보고 다음 행동을 JSON으로 고르게 만드는 것이다.

## 1. 기본 구조

LLM 테스트 흐름은 다음과 같다.

```text
STS2 모드
-> combat_state.json 생성
-> 브리지 서버에 상태 게시
-> decision_loop가 상태 조회
-> command 모드에서 외부 판단기 실행
-> scripts/codex_decider.js가 Codex CLI 호출
-> JSON 행동 반환
-> 브리지가 행동 검증
-> STS2 모드가 행동 claim 후 실행
-> 결과와 상태 변화 기록
```

중요한 원칙:

- LLM은 게임을 직접 클릭하지 않는다.
- LLM은 항상 `legal_actions`에 있는 행동만 고른다.
- 실제 게임 조작은 모드의 실행기가 담당한다.
- 실행 결과는 `decisions.jsonl`, `combat_log.jsonl`, `metrics.json`, `memory_summary.json`에 남긴다.

## 2. 관련 파일

주요 파일은 다음과 같다.

- 브리지 서버: `bridge/spiremind_bridge.js`
- 의사결정 루프: `bridge/spiremind_decision_loop.js`
- Codex 판단기 어댑터: `scripts/codex_decider.js`
- 상주 에이전트 데몬: `scripts/spiremind_agent_daemon.js`
- 실행 기록 설명: `docs/run_memory_logging.md`
- Codex 연결 설명: `docs/codex_decider.md`
- 상주 데몬 설명: `docs/persistent_agent_daemon.md`
- 게임 실행 설명: `docs/game_launch_guide.md`

기본 모델 설정은 `scripts/codex_decider.js`에 있다.

```text
DEFAULT_MODEL = "gpt-5.4-mini"
```

환경 변수로 바꿀 수 있다.

```powershell
$env:SPIREMIND_CODEX_MODEL = "gpt-5.4-mini"
```

## 3. 사전 확인

먼저 Codex CLI가 실행 가능한지 확인한다.

```powershell
Get-Command codex.cmd
codex --version
```

Node.js와 모드 빌드도 확인한다.

```powershell
node --version
dotnet build .\src\SpireMindMod\SpireMindMod.csproj
```

브리지가 이미 켜져 있는지 확인한다.

```powershell
Invoke-RestMethod -Method Get -Uri "http://127.0.0.1:17832/health" -TimeoutSec 2
```

꺼져 있으면 다음 명령으로 켠다.

```powershell
Start-Process `
  -FilePath "node" `
  -ArgumentList @(".\bridge\spiremind_bridge.js", "--http-host", "127.0.0.1", "--http-port", "17832") `
  -WorkingDirectory "F:\Antigravity\STSAutoplay" `
  -WindowStyle Hidden
```

## 4. 게임 상태 준비

실제 게임 상태로 테스트하려면 게임이 켜져 있어야 한다.

```powershell
Start-Process "steam://rungameid/2868840"
```

게임에서 전투, 이벤트, 보상, 지도 등 테스트할 화면에 들어간 뒤 상태 파일이 갱신되는지 확인한다.

```powershell
$statePath = Join-Path $env:APPDATA "SlayTheSpire2\SpireMind\combat_state.json"
Get-Item $statePath | Select-Object FullName, LastWriteTime, Length
Get-Content $statePath -Raw -Encoding UTF8
```

브리지에 게시된 상태도 확인한다.

```powershell
Invoke-RestMethod -Method Get -Uri "http://127.0.0.1:17832/state/current" -TimeoutSec 2
```

확인할 값:

- `status`가 `ready`인지
- `state_version`이 증가하는지
- `state.state_id`가 최신 상태인지
- `state.phase`가 현재 화면과 맞는지
- `state.legal_actions`가 비어 있지 않은지

## 5. 휴리스틱 기준선 테스트

LLM을 붙이기 전에 휴리스틱이 같은 상태에서 정상 동작하는지 먼저 확인한다.

판단만 확인:

```powershell
node .\bridge\spiremind_decision_loop.js `
  --bridge-url "http://127.0.0.1:17832" `
  --mode heuristic `
  --once `
  --dry-run
```

실제 제출:

```powershell
node .\bridge\spiremind_decision_loop.js `
  --bridge-url "http://127.0.0.1:17832" `
  --mode heuristic `
  --once `
  --wait-result
```

휴리스틱도 실패하면 LLM 문제가 아니다. 이 경우 먼저 상태 추출, 브리지, 행동 실행기를 확인한다.

## 6. Codex 판단기 자체 점검

`scripts/codex_decider.js`가 입력을 요약할 수 있는지 확인한다.

```powershell
node .\scripts\codex_decider.js --self-test
```

Codex를 실제로 부르지 않고 연결 경로만 확인하려면 고정 응답을 사용한다.

```powershell
$env:SPIREMIND_CODEX_FAKE_DECISION = '{"selected_action_id":"end_turn","reason":"smoke"}'

node .\bridge\spiremind_decision_loop.js `
  --bridge-url "http://127.0.0.1:17832" `
  --mode command `
  --command node `
  --command-arg .\scripts\codex_decider.js `
  --once `
  --dry-run

Remove-Item Env:\SPIREMIND_CODEX_FAKE_DECISION
```

이 테스트가 통과하면 다음 연결은 정상이다.

- `decision_loop`
- command 모드
- `codex_decider.js`
- 판단기 출력 JSON 파싱

## 7. LLM dry-run 테스트

실제 Codex CLI를 호출하되 행동은 제출하지 않으려면 `--dry-run`을 사용한다.

```powershell
$env:SPIREMIND_CODEX_MODEL = "gpt-5.4-mini"
$env:SPIREMIND_CODEX_TIMEOUT_MS = "120000"

node .\bridge\spiremind_decision_loop.js `
  --bridge-url "http://127.0.0.1:17832" `
  --mode command `
  --command node `
  --command-arg .\scripts\codex_decider.js `
  --once `
  --dry-run
```

이 단계에서는 게임 상태가 변하지 않는다. 확인할 것은 LLM이 유효한 JSON을 반환하는지, 반환된 `selected_action_id`나 `actions`가 현재 `legal_actions`와 맞는지다.

app-server 상주 백엔드를 확인하려면 다음 명령을 사용한다.

```powershell
node .\scripts\spiremind_agent_daemon.js `
  --decision-backend app-server `
  --dry-run `
  --max-decisions 1 `
  --model gpt-5.4-mini `
  --effort low
```

이 방식은 `codex app-server --listen stdio://`를 한 번 띄운 뒤 같은 프로세스에 턴을 보낸다. 로그의 `decision_source`가 `llm_app_server`이면 app-server 백엔드를 사용한 것이다.

## 8. LLM 단일 행동 실행 테스트

LLM 판단을 실제 게임에 한 번 적용하려면 다음 명령을 사용한다.

```powershell
$runLogDir = Join-Path $env:APPDATA "SlayTheSpire2\SpireMind\runs\llm_once_001"

node .\bridge\spiremind_decision_loop.js `
  --bridge-url "http://127.0.0.1:17832" `
  --mode command `
  --command node `
  --command-arg .\scripts\codex_decider.js `
  --once `
  --wait-result `
  --run-log-dir $runLogDir `
  --scenario-id "llm_once_check" `
  --play-session-id "session_llm_once_001"
```

성공하면 표준 출력에 `status`가 나온다.

대표 상태:

- `applied`: 게임 쪽 실행기가 행동을 적용했다.
- `submitted`: 행동은 제출됐지만 결과까지 기다리지 않았다.
- `failed`: 실행기가 행동 적용에 실패했다.
- `result_timeout`: 결과 보고를 제한 시간 안에 받지 못했다.
- `decision_stale_before_submit`: 판단하는 동안 상태가 바뀌어 제출을 건너뛰었다.

## 9. 전투 종료까지 반복 테스트

한 전투를 끝까지 LLM으로 진행하려면 다음 명령을 사용한다.

```powershell
$runLogDir = Join-Path $env:APPDATA "SlayTheSpire2\SpireMind\runs\llm_combat_001"

node .\bridge\spiremind_decision_loop.js `
  --bridge-url "http://127.0.0.1:17832" `
  --mode command `
  --command node `
  --command-arg .\scripts\codex_decider.js `
  --until-combat-end `
  --wait-result `
  --max-decisions 20 `
  --run-log-dir $runLogDir `
  --scenario-id "llm_combat_check" `
  --play-session-id "session_llm_combat_001"
```

주의:

- `--until-combat-end`는 전투 중심 반복 모드다.
- `phase: "reward"`, `phase: "map"`, `phase: "event"` 같은 비전투 상태가 나오면 멈출 수 있다.
- 런 전체 자동 진행은 별도 루프로 다루어야 한다.

## 10. 실행 기록 확인

`--run-log-dir`를 지정하면 다음 파일이 생성된다.

- `decider_config.json`
- `scenario_config.json`
- `decisions.jsonl`
- `combat_log.jsonl`
- `metrics.json`
- `memory_summary.json`

확인 명령:

```powershell
Get-ChildItem $runLogDir
Get-Content (Join-Path $runLogDir "decisions.jsonl") -Raw -Encoding UTF8
Get-Content (Join-Path $runLogDir "combat_log.jsonl") -Raw -Encoding UTF8
Get-Content (Join-Path $runLogDir "metrics.json") -Raw -Encoding UTF8
Get-Content (Join-Path $runLogDir "memory_summary.json") -Raw -Encoding UTF8
```

중요하게 볼 항목:

- LLM 판단 시간: `decision_ms`
- 제출 상태: `status`
- 선택한 행동: `decision`
- 실행 결과: `latest_action`, `final_latest_action`
- 실행 전후 변화: `state_delta`
- 체력 손실: `state_delta.player.hp_lost`
- 적 체력 감소: `state_delta.enemy_hp_lost`
- 최근 기억 요약: `memory_summary.json`

## 11. LLM 입력에 들어가는 정보

`decision_loop`는 외부 판단기에 다음 정보를 전달한다.

- `play_session_id`
- 최근 실행 기록 요약
- 응답 형식
- 현재 `state_version`
- 현재 게임 상태
- `legal_actions`

`scripts/codex_decider.js`는 이 입력을 다시 줄여 Codex CLI에 넘긴다.

전투 상태에는 보통 다음 정보가 들어간다.

- 플레이어 체력, 최대 체력, 방어도, 에너지
- 손패 카드 이름, 비용, 타입, 설명, 피해량, 방어량
- 적 체력, 방어도, 의도
- 유물 목록
- 실행 가능한 행동 목록
- 최근 판단과 결과 기록

LLM은 내부적으로 `scenario_id`를 직접 받지 않는다. 실험 관리용 식별자는 실행 기록에만 남긴다.

## 12. 응답 형식

단일 행동 선택:

```json
{
  "selected_action_id": "end_turn",
  "reason": "사용 가능한 카드가 없어 턴을 종료합니다."
}
```

여러 행동 묶음:

```json
{
  "actions": [
    {
      "type": "play_card",
      "combat_card_id": 3,
      "target_combat_id": 1
    },
    {
      "type": "end_turn"
    }
  ],
  "reason": "공격 카드를 사용한 뒤 턴을 종료합니다."
}
```

브리지는 이 응답을 현재 `legal_actions`와 대조한다. 맞지 않으면 제출을 거부하거나 stale 처리한다.

## 13. 자주 생기는 문제

### Codex 명령을 찾지 못한다

```powershell
Get-Command codex.cmd
```

찾지 못하면 `SPIREMIND_CODEX_COMMAND`로 실행 파일을 지정한다.

```powershell
$env:SPIREMIND_CODEX_COMMAND = "C:\path\to\codex.cmd"
```

### Codex 응답이 너무 오래 걸린다

제한 시간을 늘린다.

```powershell
$env:SPIREMIND_CODEX_TIMEOUT_MS = "180000"
```

`decision_loop` 자체 제한 시간도 늘릴 수 있다.

```powershell
node .\bridge\spiremind_decision_loop.js `
  --mode command `
  --command node `
  --command-arg .\scripts\codex_decider.js `
  --once `
  --timeout-ms 180000
```

### JSON 파싱 오류가 난다

Codex 출력에 설명문이나 코드 블록이 섞였을 가능성이 있다.

확인할 것:

- `codex_decider.js`가 `--output-schema`를 사용하고 있는지
- Codex가 JSON 객체 하나만 출력하는지
- stdout에 로그가 섞이지 않는지

### 행동이 stale 처리된다

판단 도중 게임 상태가 바뀐 것이다.

대응:

- 전투 애니메이션이 끝난 뒤 실행한다.
- `--wait-result`로 한 번씩 천천히 확인한다.
- 상태가 자주 바뀌는 화면에서는 `--once`부터 테스트한다.

### LLM이 잘못된 카드를 고른다

확인할 것:

- `combat_card_id`가 손패 카드와 일치하는지
- 같은 이름의 카드가 여러 장인지
- 카드 비용과 현재 에너지가 맞는지
- X 비용 카드가 포함되어 있는지
- `legal_actions`에 실제로 해당 카드 행동이 있는지

### 실행 결과가 안 돌아온다

다음을 확인한다.

```powershell
Invoke-RestMethod -Method Get -Uri "http://127.0.0.1:17832/action/latest" -TimeoutSec 2
```

확인할 값:

- `latest_action.execution_status`
- `latest_action.result`
- `action_plan.status`
- `action_plan.failure`

게임 쪽 로그도 확인한다.

```powershell
$logPath = Join-Path $env:APPDATA "SlayTheSpire2\logs\godot.log"
Get-Content $logPath -Tail 160 -Encoding UTF8 | Select-String "SpireMind"
```

## 14. 최소 테스트 순서

처음 새 환경에서 확인할 때는 아래 순서를 따른다.

```powershell
# 1. 브리지 확인
Invoke-RestMethod -Method Get -Uri "http://127.0.0.1:17832/health" -TimeoutSec 2

# 2. 게임 실행
Start-Process "steam://rungameid/2868840"

# 3. 게임에서 전투 또는 테스트 화면 진입

# 4. 상태 확인
Invoke-RestMethod -Method Get -Uri "http://127.0.0.1:17832/state/current" -TimeoutSec 2

# 5. 휴리스틱 dry-run
node .\bridge\spiremind_decision_loop.js --bridge-url "http://127.0.0.1:17832" --mode heuristic --once --dry-run

# 6. Codex 연결 dry-run
$env:SPIREMIND_CODEX_MODEL = "gpt-5.4-mini"
node .\bridge\spiremind_decision_loop.js --bridge-url "http://127.0.0.1:17832" --mode command --command node --command-arg .\scripts\codex_decider.js --once --dry-run

# 7. Codex 실제 1회 실행
$runLogDir = Join-Path $env:APPDATA "SlayTheSpire2\SpireMind\runs\llm_once_001"
node .\bridge\spiremind_decision_loop.js --bridge-url "http://127.0.0.1:17832" --mode command --command node --command-arg .\scripts\codex_decider.js --once --wait-result --run-log-dir $runLogDir
```
