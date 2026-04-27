# R5 전투 행동 실행 설계

이 문서는 R5에서 Codex가 고른 `action_id`를 STS2 안에서 실제 행동으로 실행하는 구조를 정의한다.

R5의 목표는 빠른 자동 플레이가 아니다. 목표는 행동 1개가 선택되고, 한 번만 실행되고, 결과가 로그로 남는 닫힌 흐름을 만드는 것이다.

## 구현 티켓 요약

### 목적

R5.1에서는 Codex가 제출한 `end_turn`을 STS2 모드가 한 번만 claim하고, 현재 상태와 맞을 때만 실행한 뒤, 실행 결과를 브리지에 보고한다.

### 수정 허용 파일

- `bridge/spiremind_bridge.js`
- `docs/action_execution_design.md`
- `docs/action_schema.md`
- `docs/bridge_architecture.md`
- `docs/development_roadmap.md`
- `src/SpireMindMod/CombatStateHarmonyPatch.cs`
- `src/SpireMindMod/CombatStateExporter.cs`
- `src/SpireMindMod/SpireMindBridgePoster.cs`
- 새 파일: `src/SpireMindMod/CombatActionBridgeClient.cs`
- 새 파일: `src/SpireMindMod/CombatActionExecutor.cs`
- 새 파일: `src/SpireMindMod/CombatActionRuntimeContext.cs`

### 파일별 책임

- `bridge/spiremind_bridge.js`: claim/result endpoint, action 상태 전이, 실행 로그를 담당한다.
- `CombatActionBridgeClient.cs`: claim 요청과 result 보고를 담당한다.
- `CombatActionExecutor.cs`: claim된 행동을 게임 스레드에서 검증하고 실행한다.
- `CombatActionRuntimeContext.cs`: 최신 전투 루트, 최신 상태 id, 최신 상태 버전, 현재 `legal_actions`를 보관한다.
- `CombatStateHarmonyPatch.cs`: 실행 루프가 붙을 전투 tick 관찰 지점을 제공한다.
- `CombatStateExporter.cs`: 상태 추출과 행동 후보 생성을 유지하고, 실행 context가 현재 `legal_actions`를 참조할 수 있게 한다.
- `SpireMindBridgePoster.cs`: 기존 상태 전송 책임을 유지한다. claim/result 클라이언트와 섞지 않는다.

### 수정 금지 파일 또는 금지 책임

- Codex MCP 프록시가 게임 행동을 직접 실행하면 안 된다.
- 브리지가 STS2 프로세스, 입력 장치, 게임 파일을 직접 조작하면 안 된다.
- R5.1에서 `play_card` 실행을 열면 안 된다.
- 마우스 좌표 클릭 기반 입력 시뮬레이션을 기본 실행 경로로 넣으면 안 된다.
- 보상, 지도, 상점, 이벤트 선택은 R5 범위에 넣지 않는다.

### 구조 차단 조건

- `submission_id` 중복 실행을 막는 저장 지점이 없으면 구현을 차단한다.
- claim 없이 result를 받는 구조면 구현을 차단한다.
- 현재 `legal_actions`에서 `selected_action_id`를 다시 찾지 않으면 구현을 차단한다.
- 오래된 `state_version` 행동을 실행할 수 있으면 구현을 차단한다.
- HTTP 실패가 게임 프레임을 멈출 수 있으면 구현을 차단한다.

### PASS 조건

- 브리지가 `POST /action/claim`과 `POST /action/result`를 제공한다.
- Codex가 제출한 `end_turn`을 모드가 claim한다.
- claim된 `submission_id`는 한 번만 실행된다.
- 상태가 맞지 않는 행동은 `stale`로 보고되고 실행되지 않는다.
- `play_card`는 R5.1에서 `unsupported`로 보고된다.
- 실행 결과가 브리지 로그에 남는다.
- 턴 종료 후 새 `combat_state`가 브리지로 들어온다.

### 검증 방법

- `node --check bridge/spiremind_bridge.js`
- `dotnet build src/SpireMindMod/SpireMindMod.csproj`
- 브리지 HTTP 스모크: `POST /state`, `POST /action/submit`, `POST /action/claim`, `POST /action/result`
- STS2 수동 검증: 전투에서 Codex가 `end_turn`을 제출하게 하고, 게임 화면에서 턴이 넘어가는지 확인한다.
- 로그 검증: `events.jsonl`, `latest_action.json`, `godot.log`에서 claim, 실행, result, 새 상태를 확인한다.

## 코어 판타지와 실험 정체성

SpireMind는 사람을 돕는 전투 보조 모드가 아니다. 플레이어가 직접 개입하지 않는 상태에서 AI가 STS2 전투를 얼마나 안정적으로 이해하고 실행하는지 측정하는 실험 장치다.

따라서 화면에서 플레이어가 목격해야 하는 흐름은 단순해야 한다.

```text
전투 상태가 관찰된다
-> Codex가 허용된 행동 중 하나를 고른다
-> 모드가 같은 상태인지 확인한다
-> 행동이 한 번 실행된다
-> 새 상태와 실행 결과가 기록된다
```

## R5에서 잠글 것과 열어둘 것

### 잠글 것

- 행동은 반드시 브리지에 저장된 `legal_actions` 안에서만 실행한다.
- 브리지의 `latest_action`만 믿고 실행하지 않는다. 실행 파라미터는 현재 `combat_state.state.legal_actions`에서 같은 `action_id`를 다시 찾아 얻는다.
- 행동 실행은 STS2 모드가 담당한다.
- Codex와 MCP 프록시는 게임 프로세스, 입력 장치, 파일을 직접 조작하지 않는다.
- 같은 `submission_id`는 한 번만 실행한다.
- 실행 직전에도 상태 일치 여부를 다시 확인한다.
- 상태가 같아도 손패 순서 기반 id가 다른 카드를 가리킬 수 있으므로, 카드 실행 단계에서는 현재 손패 객체를 다시 매핑한다.
- 실행 결과는 성공과 실패 모두 브리지에 보고한다.

### 열어둘 것

- 실제 STS2 내부 메서드 이름과 호출 방식.
- 카드 사용 실행 방식.
- 대상 지정 방식.
- 행동 실행 후 상태 안정화 판단 기준.
- 나중에 MCP 대신 전용 상주 에이전트로 바꾸는 선택지.

## 전체 흐름

```text
1. STS2 모드가 combat_state.json을 추출한다
2. STS2 모드가 같은 JSON을 브리지 POST /state로 보낸다
3. Codex가 MCP 프록시로 새 상태를 읽는다
4. Codex가 submit_action으로 action_id를 제출한다
5. 브리지가 action_id와 state_version을 검증한다
6. STS2 모드가 POST /action/claim으로 실행할 행동을 확보한다
7. STS2 모드가 게임 스레드에서 현재 상태를 다시 검증한다
8. STS2 모드가 행동을 실행한다
9. STS2 모드가 POST /action/result로 결과를 보고한다
10. 다음 combat_state가 추출되고 브리지에 전달된다
```

## 브리지 계약

R5에서는 기존 `POST /action/submit`에 더해 실행자용 endpoint를 추가한다.

### `POST /action/claim`

STS2 모드가 실행 가능한 최신 행동을 확보한다. 단순 조회가 아니라 claim을 쓰는 이유는 같은 행동의 중복 실행을 막기 위해서다.

요청 예시:

```json
{
  "executor_id": "sts2-mod-main",
  "observed_state_id": "combat_1777266116244",
  "observed_state_version": 4,
  "supported_action_types": ["end_turn"]
}
```

반환 예시:

```json
{
  "ok": true,
  "status": "claimed",
  "claim_token": "claim_...",
  "action": {
    "submission_id": "...",
    "state_id": "combat_1777266116244",
    "state_version": 4,
    "selected_action_id": "end_turn",
    "action_type": "end_turn"
  }
}
```

반환 상태:

- `empty`: 실행할 행동이 없다.
- `stale`: 브리지의 행동이 모드가 보고 있는 상태와 맞지 않는다.
- `unsupported`: 모드가 아직 지원하지 않는 행동이다.
- `claimed`: 실행할 행동을 확보했다.

브리지는 `claimed`를 반환할 때 `claim_token`을 발급한다. 같은 행동은 `claim_token` 없이 실행 결과를 받을 수 없다.

### `POST /action/result`

STS2 모드가 실행 결과를 보고한다.

요청 예시:

```json
{
  "submission_id": "...",
  "claim_token": "claim_...",
  "executor_id": "sts2-mod-main",
  "result": "applied",
  "observed_state_id": "combat_1777266116244",
  "observed_state_version": 4,
  "note": "end_turn executed"
}
```

결과 값:

- `applied`: 행동을 실행했다.
- `stale`: 실행 직전에 상태가 달라져 실행하지 않았다.
- `unsupported`: 아직 지원하지 않는 행동이라 실행하지 않았다.
- `failed`: 실행을 시도했지만 실패했다.
- `ignored_duplicate`: 이미 처리한 `submission_id`라 무시했다.

브리지는 결과를 `latest_action.json`, `events.jsonl`, `bridge.log`에 남긴다.

## 행동 수명 주기

```text
submitted
-> pending
-> claimed
-> applied
```

실패 흐름:

```text
pending -> stale
pending -> unsupported
claimed -> failed
claimed -> ignored_duplicate
```

`POST /state`로 새 상태가 들어왔는데 아직 실행되지 않은 행동의 `state_version`이 이전 값이면, 그 행동은 `stale`로 간주한다.

주의할 점이 있다. 현재 exporter는 `state_id`와 `exported_at_ms`를 매 export마다 새로 만든다. 실질 전투 상태가 같아도 브리지 `state_version`이 자주 증가할 수 있다. R5.1에서는 stale 판정을 엄격하게 유지하되, R5.2 전에 “실질 상태가 변했을 때만 새 판단 요청으로 볼지”를 별도 검토한다.

## STS2 모드 내부 구조

R5에서는 다음 책임을 분리한다.

### `CombatActionBridgeClient`

- 브리지에서 행동을 claim한다.
- 실행 결과를 브리지에 보고한다.
- 브리지가 꺼져 있어도 게임을 멈추지 않는다.
- timeout은 짧게 둔다.

### `CombatActionExecutor`

- claim된 행동을 게임 스레드에서 실행한다.
- 같은 `submission_id`를 중복 실행하지 않는다.
- 실행 직전 현재 전투 상태를 다시 확인한다.
- 현재 `legal_actions`에서 `selected_action_id`를 다시 찾고, 그 항목의 `type`, `card_instance_id`, `target_id`를 실행 입력으로 사용한다.
- R5.1에서는 `end_turn`만 지원한다.

### `CombatActionRuntimeContext`

- 최근 관찰한 전투 루트 객체를 보관한다.
- 최근 추출한 `state_id`, `state_version`, 손패, 적 목록을 보관한다.
- 카드 실행 단계에서 손패 객체와 적 객체를 다시 찾을 수 있게 한다.

## 실행 시점

행동 실행은 Harmony 관찰 지점의 postfix에서 바로 네트워크 요청까지 수행하지 않는다.

권장 흐름은 다음과 같다.

```text
관찰 지점 postfix
-> 최신 전투 context 갱신
-> export 가능 시 상태 전송
-> 비동기 claim 요청은 별도 client가 수행
-> claim된 행동은 게임 스레드의 다음 관찰 tick에서 실행
```

이렇게 나누는 이유는 명확하다.

- HTTP 지연이 게임 프레임을 멈추면 안 된다.
- STS2 객체 조작은 가능하면 게임 스레드에서 해야 한다.
- claim과 실행 사이에 상태가 변할 수 있으므로 실행 직전 검증이 필요하다.

## R5.1 범위: 턴 종료만 실행

첫 구현은 `end_turn`만 지원한다.

완료 조건:

- Codex가 `end_turn`을 제출한다.
- 모드가 `POST /action/claim`으로 해당 행동을 확보한다.
- 모드가 같은 `state_id`와 `state_version`인지 확인한다.
- 모드가 STS2 내부 턴 종료 경로를 호출한다.
- 모드가 `POST /action/result`에 `applied`를 보고한다.
- 다음 상태에서 손패와 턴 정보가 갱신된다.
- 같은 `submission_id`는 다시 실행되지 않는다.

R5.1에서 하지 않을 것:

- 카드 사용.
- 대상 지정.
- 마우스 클릭 기반 입력 시뮬레이션.
- 전체 전투 반복 자동화.

## R5.2 범위: 대상 없는 카드

대상 없는 카드부터 연다.

추가 검증:

- `card_instance_id`가 현재 손패에 있는가.
- 카드가 아직 플레이 가능한가.
- 에너지가 충분한가.
- 실행 후 손패, 버림 더미, 에너지 변화가 반영되는가.

## R5.3 범위: 대상 있는 카드

대상이 있는 카드를 연다.

추가 검증:

- `target_id`가 현재 살아 있는 적과 매칭되는가.
- 카드 타입과 대상 필요 여부가 맞는가.
- 실행 후 적 체력, 방어도, 버프, 더미 변화가 반영되는가.

## 실행 방식 우선순위

1. STS2 내부 메서드를 호출한다.
2. UI 노드가 이미 쓰는 핸들러를 호출한다.
3. 입력 시뮬레이션은 마지막 수단으로만 검토한다.

입력 시뮬레이션을 뒤로 미루는 이유:

- 해상도와 UI 상태에 약하다.
- 애니메이션 중 클릭이 빗나갈 수 있다.
- 실패 원인이 AI 판단인지 실행 오차인지 구분하기 어렵다.

## 실패 처리

실패는 게임을 멈추지 않고 기록으로 전환한다.

- claim 실패: 다음 관찰 tick에서 다시 시도한다.
- 상태 불일치: `stale`로 보고하고 실행하지 않는다.
- 미지원 행동: `unsupported`로 보고하고 실행하지 않는다.
- 실행 예외: `failed`로 보고하고 같은 `submission_id`를 다시 실행하지 않는다.
- 브리지 응답 없음: 일정 시간 무시하고 수동 플레이 가능한 상태를 유지한다.

## 로그와 검증

브리지 로그:

- `action_claimed`
- `action_result_reported`
- `action_marked_stale`
- `action_claim_rejected`

모드 로그:

- claim 요청 시작과 결과.
- 실행 직전 검증 결과.
- 실제 실행 시도.
- 실행 결과 보고 성공 또는 실패.

수동 검증 절차:

1. 브리지를 실행한다.
2. Codex MCP 프록시를 등록한다.
3. 전투에 들어간다.
4. `combat_state.json`이 브리지에 들어오는지 확인한다.
5. Codex가 `end_turn`을 제출하게 한다.
6. 모드 로그에서 claim과 실행 결과를 확인한다.
7. 게임 화면에서 턴이 넘어갔는지 확인한다.
8. 브리지 로그에서 `applied` 결과가 남았는지 확인한다.

## 구현 전 조사 항목

R5.1 구현 전에 다음 STS2 내부 경로를 조사한다.

- 현재 전투 방 객체에서 턴 종료를 담당하는 메서드.
- 턴 종료 버튼 또는 입력 처리기가 호출하는 메서드.
- 전투 상태의 현재 턴 주체를 판별하는 필드.
- 행동 실행 후 상태가 안정됐는지 확인할 수 있는 신호.

조사 결과가 불확실하면 R5.1은 턴 종료 버튼 핸들러 호출까지 허용한다. 그래도 마우스 좌표 클릭은 사용하지 않는다.
