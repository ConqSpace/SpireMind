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

