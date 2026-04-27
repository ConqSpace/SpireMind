# 브리지 아키텍처

이 문서는 R4 상주 브리지 구조를 정의한다.

핵심 목표는 Codex CLI를 매 행동마다 새로 실행하지 않는 것이다. Codex 세션은 계속 살아 있고, 브리지 서버가 새 상태를 받으면 Codex가 도구 호출로 그 상태를 읽고 행동을 제출한다.

## 책임 분리

```text
STS2 모드
-> 브리지 HTTP endpoint
-> 브리지 내부 상태와 판단 요청 큐
-> Codex MCP 도구
-> submit_action 검증
-> R5에서 게임 행동 실행
```

### STS2 모드

- 전투 상태를 추출한다.
- `legal_actions`를 생성한다.
- R4에서는 브리지로 상태를 보내는 역할만 맡는다.
- R5 전까지 게임 행동을 자동 실행하지 않는다.

### 브리지 서버

- 최신 전투 상태를 보관한다.
- 상태가 들어오면 `state_version`을 증가시킨다.
- Codex가 기다릴 수 있는 판단 요청을 제공한다.
- Codex가 제출한 `selected_action_id`를 최신 `legal_actions`와 대조한다.
- 모든 상태 수신과 행동 제출을 로그에 남긴다.

### Codex 세션

- 브리지 MCP 도구만 본다.
- 게임 파일, STS2 프로세스, 입력 장치를 직접 조작하지 않는다.
- `legal_actions` 안에서 하나의 `action_id`만 고른다.

## 구현 파일

- `bridge/spiremind_bridge.js`

이 파일은 외부 npm 의존성 없이 Node.js 기본 모듈만 사용한다.

하나의 프로세스가 두 역할을 동시에 수행한다.

- stdin/stdout: Codex가 연결하는 MCP JSON-RPC 서버
- HTTP: 게임 모드나 테스트가 상태를 보내는 로컬 endpoint

## HTTP endpoint

기본 주소는 `http://127.0.0.1:17832`이다.

### GET /health

브리지 실행 상태를 확인한다.

### POST /state

최신 `combat_state` JSON을 보낸다.

예시:

```powershell
$state = @{
  state_id = "demo_turn_1"
  legal_actions = @(
    @{ action_id = "play_hand_0_enemy_0"; type = "play_card" },
    @{ action_id = "end_turn"; type = "end_turn" }
  )
} | ConvertTo-Json -Depth 16

Invoke-RestMethod -Method Post -Uri "http://127.0.0.1:17832/state" -Body $state -ContentType "application/json"
```

### GET /action/latest

최근 Codex가 제출한 행동을 확인한다.

R4에서는 확인용이다. R5에서 모드가 이 값을 가져가거나, 별도 전용 endpoint로 확장한다.

## MCP 도구

### wait_for_decision_request

새 상태가 들어올 때까지 기다린다.

입력:

```json
{
  "last_seen_state_version": 0,
  "timeout_ms": 30000
}
```

반환:

```json
{
  "status": "ready",
  "state_version": 1,
  "state_id": "demo_turn_1",
  "legal_action_ids": ["play_hand_0_enemy_0", "end_turn"],
  "state": {}
}
```

### get_current_state

브리지에 저장된 최신 상태를 즉시 반환한다.

### submit_action

Codex가 고른 행동을 제출한다.

입력:

```json
{
  "selected_action_id": "end_turn",
  "source": "codex-gpt-5.4-mini",
  "note": "방어 수단이 부족해 턴을 종료한다.",
  "expected_state_version": 1
}
```

브리지는 최신 `legal_actions` 안에 있는 행동인지 확인하고 `valid` 값을 기록한다.

## 실행 방법

브리지만 직접 실행할 때:

```powershell
node .\bridge\spiremind_bridge.js
```

포트를 바꿀 때:

```powershell
node .\bridge\spiremind_bridge.js --http-port 17833
```

## Codex MCP 등록

Codex가 브리지 도구를 보려면 MCP 서버로 등록한다.

```powershell
codex mcp add spiremind-bridge -- node F:\Antigravity\STSAutoplay\bridge\spiremind_bridge.js
```

등록 확인:

```powershell
codex mcp list
```

그 다음 Codex 세션에서 브리지 도구만 사용하도록 지시한다.

```text
SpireMind 브리지의 wait_for_decision_request로 새 상태를 기다려라.
상태를 받으면 legal_action_ids 중 하나만 골라 submit_action을 호출해라.
게임 파일이나 프로세스는 직접 조작하지 마라.
```

## 나중에 열어둘 에이전트 구조

현재 R4는 Codex와 브리지를 MCP로 연결한다. 이 선택은 표준 도구 호출 흐름을 그대로 쓸 수 있고, 구현 위험이 낮다.

다만 MCP가 최종 구조로 고정된 것은 아니다. 전투 자동 실행과 반복 실험에서 아래 문제가 실제로 확인되면, MCP와 비슷하게 동작하는 전용 상주 에이전트로 바꿀 수 있다.

- 도구 호출 왕복 시간이 전투 진행을 눈에 띄게 늦춘다.
- Codex가 `wait_for_decision_request` 대기 루프를 안정적으로 유지하지 못한다.
- 상태 이벤트가 자주 들어와 MCP 호출 단위가 너무 무거워진다.
- 전투별 기억과 로그 처리를 더 직접 제어해야 한다.

전용 상주 에이전트 후보 구조는 다음과 같다.

```text
브리지 서버
-> 전용 Agent Host
-> WebSocket, SSE, 또는 stdio 이벤트 스트림
-> decision JSON 반환
-> 브리지 검증
```

이 구조로 바꾸더라도 유지해야 하는 불변 규칙은 같다.

- 에이전트는 게임을 직접 조작하지 않는다.
- 에이전트는 브리지가 제공한 최신 상태와 `legal_actions`만 본다.
- 행동 제출은 `selected_action_id` 하나로 제한한다.
- 브리지가 항상 `state_version`과 `legal_actions`를 다시 검증한다.
- 게임 행동 실행은 R5 실행기가 맡는다.

따라서 MCP는 현재 검증용 표준 연결 방식이고, 전용 상주 에이전트는 성능이나 운용 문제가 지표로 확인된 뒤 교체할 수 있는 선택지로 둔다.

## 로그

기본 로그 위치:

`%APPDATA%\SlayTheSpire2\SpireMind\bridge_runs\<timestamp>_<pid>_<suffix>\`

주요 파일:

- `run_info.json`: 브리지 실행 정보
- `current_state.json`: 최신 상태
- `latest_action.json`: 최근 제출 행동
- `events.jsonl`: 상태 수신, MCP 호출, 행동 제출 이벤트
- `bridge.log`: 사람이 빠르게 읽는 로그

## 현재 한계

- R4는 행동 실행을 하지 않는다.
- MCP 도구는 최소 계약만 제공한다.
- STS2 모드에서 `/state`로 직접 전송하는 코드는 아직 없다. 현재는 테스트나 외부 스크립트가 상태를 보낼 수 있다.
- R5에서 모드가 행동을 가져가 실행하는 경로를 추가해야 한다.

## STS2 exporter 직접 전송

이제 STS2 모드의 `CombatStateExporter`도 같은 `combat_state.json`을 로컬 브리지 `http://127.0.0.1:17832/state`로 보냅니다.

- 파일 출력은 그대로 유지합니다.
- 브리지 전송은 짧은 timeout으로 처리합니다.
- 같은 상태는 너무 자주 보내지 않도록 막습니다.
- 실패해도 게임은 멈추지 않습니다.
- 전송 설정은 `%APPDATA%\SlayTheSpire2\SpireMind\bridge_config.json`에서 바꿀 수 있습니다.
- 환경 변수 `SPIREMIND_BRIDGE_ENABLED`, `SPIREMIND_BRIDGE_STATE_URL`이 있으면 설정 파일보다 우선합니다.
