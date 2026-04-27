# 브리지 아키텍처

이 문서는 SpireMind의 상주 브리지와 Codex 연결 구조를 설명한다.

핵심 목표는 Codex CLI를 매 행동마다 새로 띄우지 않는 것이다. 실제 게임 상태는 하나의 HTTP 브리지 프로세스가 계속 보관하고, Codex는 별도의 MCP 프록시를 통해 그 상태를 읽고 행동을 제출한다.

## 구조

```text
STS2 모드
-> 브리지 HTTP 서버
-> Codex MCP 프록시
-> Codex 세션
-> 행동 선택
-> 브리지 검증
-> R5에서 모드가 claim 후 실행
```

## 책임

### STS2 모드

- 전투 상태를 추출한다.
- `legal_actions`를 만든다.
- `POST /state`로 상태를 브리지에 보낸다.
- 게임 행동을 직접 실행하는 책임은 R5 이후로 미룬다.

### 브리지 HTTP 서버

- 최신 전투 상태를 보관한다.
- 상태가 들어오면 `state_version`을 올린다.
- 최신 상태 조회용 `GET /state/current`를 제공한다.
- 행동 제출용 `POST /action/submit`를 제공한다.
- 제출된 `selected_action_id`를 최신 `legal_actions`와 비교한다.
- R5에서는 실행 claim과 실행 결과 보고를 받는다.
- 상태 수신과 행동 제출을 로그에 남긴다.

### Codex MCP 프록시

- stdin/stdout 기반 MCP 서버로 동작한다.
- 자체 HTTP 서버를 열지 않는다.
- `GET /state/current`를 폴링해 새 상태를 기다린다.
- `POST /action/submit`로 행동을 전달한다.
- 기본 브리지 주소는 `http://127.0.0.1:17832`이다.

## 구현 파일

- `bridge/spiremind_bridge.js`
- `bridge/spiremind_mcp_proxy.js`

둘 다 Node.js 기본 모듈만 사용한다.

## HTTP endpoint

### `GET /health`

브리지 실행 상태를 확인한다.

### `POST /state`

최신 `combat_state` JSON을 보낸다.

### `GET /state/current`

브리지가 보관 중인 최신 상태를 즉시 돌려준다.

반환 예시:

```json
{
  "ok": true,
  "status": "ready",
  "state_version": 1,
  "received_at": "2026-04-27T10:00:00.000Z",
  "state_id": "demo_turn_1",
  "legal_action_ids": ["play_hand_0_enemy_0", "end_turn"],
  "state": {},
  "latest_action": null
}
```

### `GET /action/latest`

최근 제출된 행동을 확인한다. 기존 확인용 경로라서 유지한다.

### `POST /action/submit`

Codex가 고른 행동을 브리지에 저장한다.

입력 필드:

- `selected_action_id`
- `source`
- `note`
- `expected_state_version`

브리지는 다음을 같은 규칙으로 검증한다.

- `selected_action_id`가 현재 `legal_actions` 안에 있는지
- `expected_state_version`이 현재 `state_version`과 맞는지

검증 결과는 `latest_action`으로 저장한다. 실패해도 기록은 남긴다.

### `POST /action/claim`

STS2 모드가 실행할 행동을 확보한다. 단순 조회가 아니라 claim을 쓰는 이유는 같은 `submission_id`를 여러 번 실행하지 않기 위해서다.

claim은 아래 조건을 만족할 때만 성공한다.

- 최신 행동이 `valid: true`다.
- 아직 실행 결과가 보고되지 않았다.
- 모드가 보낸 `observed_state_id`와 `observed_state_version`이 행동의 상태와 맞다.
- 모드가 해당 행동 타입을 지원한다.

### `POST /action/result`

STS2 모드가 실행 결과를 보고한다.

결과 값은 다음 중 하나다.

- `applied`
- `stale`
- `unsupported`
- `failed`
- `ignored_duplicate`

브리지는 결과를 `latest_action.json`, `events.jsonl`, `bridge.log`에 남긴다.

## MCP 도구

### `wait_for_decision_request`

입력된 `last_seen_state_version`보다 새 상태가 들어올 때까지 기다린다.

이 도구는 브리지 HTTP 서버의 `GET /state/current`를 폴링한다. 새 버전이 나오면 바로 반환한다.

### `get_current_state`

브리지의 최신 상태를 즉시 가져온다.

### `submit_action`

선택한 행동을 브리지의 `POST /action/submit`로 보낸다.

## 실행 방법

브리지 HTTP 서버를 직접 실행할 때:

```powershell
node .\bridge\spiremind_bridge.js
```

포트를 바꿀 때:

```powershell
node .\bridge\spiremind_bridge.js --http-port 17833
```

MCP 프록시를 Codex에 등록할 때:

```powershell
codex mcp add spiremind-bridge -- node F:\Antigravity\STSAutoplay\bridge\spiremind_mcp_proxy.js --bridge-url http://127.0.0.1:17832
```

환경 변수로 바꿀 수도 있다.

```powershell
$env:SPIREMIND_BRIDGE_URL = "http://127.0.0.1:17832"
codex mcp add spiremind-bridge -- node F:\Antigravity\STSAutoplay\bridge\spiremind_mcp_proxy.js
```

## 현재 한계

- R4는 행동 실행을 하지 않는다.
- MCP 프록시는 상태 조회와 행동 제출만 담당한다.
- 게임 행동을 실제로 실행하는 경로는 R5에서 `claim -> 실행 -> result` 흐름으로 만든다.

## 나중에 열어둘 에이전트 구조

현재 구조는 Codex와 브리지를 MCP 프록시로 연결한다. 이 방식은 Codex CLI의 표준 도구 호출 흐름을 그대로 쓰기 때문에 초기 구현 위험이 낮다.

다만 최종 구조로 고정하지 않는다. 아래 조건이 실제 실험에서 확인되면, MCP와 비슷하게 동작하는 전용 상주 에이전트로 바꿀 수 있다.

- 행동 1회당 응답 시간이 실험 진행을 크게 늦춘다.
- Codex가 대기 도구 호출을 오래 유지하지 못한다.
- 상태 이벤트가 자주 들어와 MCP 호출 단위가 무거워진다.
- 전투별 기억과 이벤트 로그 처리를 더 직접 제어해야 한다.

이 선택지를 열어두더라도 핵심 계약은 유지한다. 에이전트는 게임을 직접 조작하지 않고, 브리지가 제공한 `legal_actions` 안에서 하나의 `action_id`만 제출한다.

## 로그

기본 로그 위치는 `%APPDATA%\SlayTheSpire2\SpireMind\bridge_runs\<timestamp>_<pid>_<suffix>\`이다.

주요 파일은 다음과 같다.

- `run_info.json`
- `current_state.json`
- `latest_action.json`
- `events.jsonl`
- `bridge.log`
