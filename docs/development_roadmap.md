# 개발 로드맵

이 문서는 SpireMind의 개발 순서를 정의한다.

목표는 빠르게 자동 플레이를 붙이는 것이 아니다. 먼저 STS2 상태를 정확히 관찰하고, 그 다음 Codex가 제한된 행동 중 하나를 고르게 하며, 마지막으로 실험 결과를 비교 가능한 지표로 전환한다.

## 전체 방향

```text
상태 추출
-> 행동 후보 생성
-> 브리지 서버에 상태 전달
-> Codex가 브리지 도구로 action_id 선택
-> 선택 검증
-> 행동 실행
-> 로그 저장
-> 실험 분석
```

처음 잠글 것은 전투 1개다. 지도, 보상, 상점, 이벤트는 전투 자동화가 안정화된 뒤 추가한다.

## 전략 단계와 구현 단계의 대응

| 전략 단계 | 의미 | 대응 구현 단계 |
|---|---|---|
| S0 관찰 가능성 확보 | STS2 상태를 정확히 읽는다 | R1, R2, R3 |
| S1 전투 지능 측정 | Codex의 전투 판단을 측정한다 | R4, R5, R6의 전투 지표 |
| S2 성장 판단 측정 | 카드 보상과 덱 방향을 측정한다 | R7 중 보상 선택 |
| S3 등반 판단 측정 | 지도와 위험 선택을 측정한다 | R7 중 지도/상점/휴식 |
| S4 전체 런 벤치마크 | 한 판 전체를 비교 가능한 실험으로 만든다 | R6 확장, R7 완료 후 |
| S5 보정된 AI 비교 | 계산기, 규칙, 기억의 효과를 비교한다 | 별도 R8 후보 |
| S6 학습형 정책 비교 | 강화학습/모방 정책을 비교한다 | 별도 R9 후보 |

## 단계 요약

| 단계 | 전략 단계 | 목표 | 주요 산출물 | 상태 |
|---|---|---|---|---|
| R1 | S0 | STS2 모드 골격 | C# 모드 프로젝트, 빌드/배포 스크립트 | 완료 |
| R2 | S0 | 전투 상태 추출 | `combat_state.v1` JSON exporter | 완료 |
| R3 | S0 | 행동 후보 생성 | `legal_actions` 생성기 | 완료 |
| R4 | S1 | 상주 브리지와 Codex 연결 | HTTP 브리지, MCP 프록시, 판단 요청 큐, 검증 로그 | 진행 중 |
| R5 | S1 | 전투 행동 실행 | 실행 claim 계약, 턴 종료 실행, 결과 보고 | 진행 중 |
| R6 | S1/S4 일부 | 실험 러너 | 시드 반복, 지표 집계 | 예정 |
| R7 | S2/S3 | 범위 확장 | 보상/지도/상점 선택 | 예정 |

## R1: STS2 모드 골격

### 목표

STS2에서 로드되는 최소 C# 모드를 만든다.

### 완료 기준

- STS2 설정 화면에서 SpireMind 모드가 보인다.
- STS2 로더 로그에서 SpireMind manifest 발견, DLL 로드, 초기화 완료가 확인된다.
- 모드를 켜고 새 전투에 진입해도 게임이 멈추지 않는다.

### 구현 상태

- 모드 골격은 `src/SpireMindMod/`에 있다.
- 빌드는 `scripts/build_mod.ps1`로 확인한다.
- 배포는 `scripts/deploy_mod.ps1`로 수행한다.
- 현재 프로젝트는 로컬 검증 가능성을 우선해 `net8.0`으로 빌드한다.

## R2: 전투 상태 추출

### 목표

`combat_state.v1` 형식으로 전투 상태를 JSON 파일에 쓴다.

### 완료 기준

- 전투 시작, 카드 사용, 턴 종료, 적 사망 후 JSON이 갱신된다.
- 카드 더미 개수와 목록이 실제 게임과 일치한다.
- 적 의도, 버프, 디버프는 가능한 원본 정보와 계산 값을 함께 남긴다.

### 구현 상태

- `CombatStateExporter`가 `%APPDATA%\SlayTheSpire2\SpireMind\combat_state.json`을 쓴다.
- 손패, 뽑을 카드 더미, 버려진 카드 더미, 소멸된 카드 더미, 유물, 적 목록을 추출한다.
- 플레이어 체력과 적 의도 피해량은 아직 추출 휴리스틱을 더 확인해야 한다.

## R3: 행동 후보 생성

### 목표

현재 상태에서 실행 가능한 행동만 `legal_actions`로 만든다.

### 완료 기준

- 에너지가 부족한 카드는 행동 후보에 들어가지 않는다.
- 죽은 적은 대상 후보에 들어가지 않는다.
- 대상 없는 카드는 `target_id: null`로 생성된다.
- 모든 `action_id`가 현재 `state_id` 안에서 유일하다.

### 구현 상태

- `combat_state.json`에 `legal_actions` 배열이 포함된다.
- 현재 범위는 손패 카드의 `play_card` 후보와 항상 생성되는 `end_turn` 후보까지다.
- LLM 호출, 카드 실행, 자동 행동 실행, 게임 상태 변경은 포함하지 않는다.

## R4: 상주 브리지와 Codex 연결

### 목표

Codex CLI를 매 행동마다 새로 실행하지 않는다. 실제 게임 상태는 상주 HTTP 브리지가 보관하고, Codex는 별도의 MCP 프록시를 통해 그 상태를 읽고 행동을 제출한다.

```text
STS2 모드
-> 브리지 HTTP 서버
-> Codex MCP 프록시
-> Codex 세션
-> 행동 선택
-> 브리지 검증과 로그 저장
```

### 작업

- 브리지 서버를 상주 프로세스로 실행한다.
- 게임 또는 테스트가 `POST /state`로 최신 `combat_state`를 보낸다.
- 브리지는 `GET /state/current`로 최신 상태를 돌려준다.
- 브리지는 `POST /action/submit`로 행동 제출을 저장하고 검증한다.
- Codex는 MCP 프록시의 `wait_for_decision_request`로 새 판단 요청을 기다린다.
- Codex는 `legal_actions` 중 하나를 고른 뒤 `submit_action`을 호출한다.
- 프록시는 브리지 HTTP 서버로만 요청을 전달한다.
- 모든 상태 수신과 행동 제출을 로그로 남긴다.

### 산출물

- `bridge/spiremind_bridge.js`
- `bridge/spiremind_mcp_proxy.js`
- `docs/bridge_architecture.md`
- 브리지 실행 로그
- Codex MCP 등록 방법

### 완료 기준

- 브리지 서버가 상태 수신과 최신 상태 조회, 행동 제출 저장을 처리한다.
- MCP 프록시는 자체 HTTP 서버 없이 stdin/stdout MCP 서버로 동작한다.
- `wait_for_decision_request`는 `GET /state/current`를 폴링해 새 상태를 찾는다.
- `submit_action`은 `POST /action/submit`로 전달되고 검증 결과가 저장된다.
- 게임 행동 실행은 하지 않는다.

### 주요 위험

- MCP 세션이 오래 대기하는 동안 사용자가 상태를 이해하기 어렵다.
- 상태가 너무 자주 들어오면 Codex가 오래된 상태를 보고 선택할 수 있다.
- Codex가 파일이나 게임을 직접 만지면 실험 경계가 흐려진다.

### 대응

- `state_version`을 두고 Codex가 본 버전과 제출 시점의 버전을 비교한다.
- Codex에는 MCP 프록시만 등록하고, 실제 상태는 브리지 HTTP 서버에서 읽는다.
- R4에서는 행동 실행을 금지하고, 선택 결과만 로그에 남긴다.
- 프록시와 브리지의 연결이 병목으로 확인되면, 같은 검증 규칙을 유지한 채 전용 WebSocket, SSE, 또는 stdio 기반 상주 에이전트로 바꿀 수 있다.

### 나중에 열어둘 선택지

R4의 MCP 프록시 구조는 표준 연결 방식이다. 최종 구조로 고정하지 않는다.

전용 상주 에이전트를 검토하는 조건은 다음과 같다.

- 행동 1회당 응답 시간이 실험 진행을 크게 늦춘다.
- Codex가 대기 도구 호출을 오래 유지하지 못한다.
- 반복 실험에서 전투별 기억과 이벤트 처리를 더 직접 제어해야 한다.

이 선택지를 열더라도 핵심 계약은 바꾸지 않는다. 에이전트는 게임을 직접 조작하지 않고, 브리지가 제공한 `legal_actions` 안에서 하나의 `action_id`만 제출한다.

## R5: 전투 행동 실행

### 목표

검증된 `action_id`를 실제 게임 행동으로 한 번만 실행한다. R5의 1차 목표는 카드 사용이 아니라 `end_turn` 실행을 안전하게 닫는 것이다.

상세 설계는 [R5 전투 행동 실행 설계](action_execution_design.md)를 따른다.

### 작업

- 브리지에 `POST /action/claim`을 추가한다.
- 브리지에 `POST /action/result`를 추가한다.
- 모드는 claim된 행동의 `selected_action_id`를 현재 `legal_actions`에서 다시 찾는다.
- 모드는 `state_id`, `state_version`, `submission_id`를 실행 직전에 다시 확인한다.
- R5.1에서는 `end_turn`만 실행한다.
- 실행 결과는 `applied`, `stale`, `unsupported`, `failed`, `ignored_duplicate` 중 하나로 보고한다.
- 같은 `submission_id`는 다시 실행하지 않는다.
- 카드 사용, 대상 지정, 턴 종료 내부 API를 조사한다. 단, 카드 사용은 R5.2 이후로 미룬다.
- 실행 후 새 상태가 안정될 때까지 기다린다.

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

- `bridge/spiremind_bridge.js`: `POST /action/claim`, `POST /action/result`, action 상태 전이, 실행 결과 로그를 담당한다.
- `SpireMindBridgePoster.cs`: 기존 상태 전송 책임을 유지한다. R5에서 행동 claim/result HTTP 클라이언트를 별도 파일로 분리한다.
- `CombatActionBridgeClient.cs`: 브리지에서 행동을 claim하고 실행 결과를 보고한다.
- `CombatActionExecutor.cs`: claim된 행동을 게임 스레드에서 검증하고 실행한다.
- `CombatActionRuntimeContext.cs`: 최근 전투 루트, 최근 상태 id, 최근 상태 버전, 현재 `legal_actions`를 보관한다.
- `CombatStateHarmonyPatch.cs`: 실행 루프가 붙을 수 있는 전투 tick 관찰 지점을 제공한다.
- `CombatStateExporter.cs`: 상태 추출과 `legal_actions` 생성을 유지하고, 실행 context가 현재 `legal_actions`를 참조할 수 있게 한다.

### 수정 금지 파일 또는 금지 책임

- Codex MCP 프록시가 게임 행동을 직접 실행하면 안 된다.
- 브리지가 STS2 프로세스, 입력 장치, 게임 파일을 직접 조작하면 안 된다.
- R5.1에서 `play_card` 실행을 열면 안 된다.
- 마우스 좌표 클릭 기반 입력 시뮬레이션을 기본 실행 경로로 넣으면 안 된다.
- 보상, 지도, 상점, 이벤트 선택은 R5 범위에 넣지 않는다.

### claim/result 계약

- Codex는 계속 `submit_action`만 호출한다.
- 브리지는 `submit_action`을 검증한 뒤 유효한 행동을 `pending` 상태로 둔다.
- 모드는 `POST /action/claim`으로 행동을 확보한다.
- claim 요청에는 `executor_id`, `observed_state_id`, `observed_state_version`, `supported_action_types`를 포함한다.
- 브리지는 상태가 맞고 아직 처리되지 않은 행동에만 `claimed`와 `claim_token`을 반환한다.
- 모드는 실행 후 반드시 `POST /action/result`로 결과를 보고한다.
- 결과 값은 `applied`, `stale`, `unsupported`, `failed`, `ignored_duplicate` 중 하나다.
- 같은 `submission_id`는 성공, 실패, 무시 여부와 관계없이 다시 실행하지 않는다.

### R5.1: end_turn 우선 범위

- R5.1에서 모드가 지원하는 행동 타입은 `end_turn`뿐이다.
- `play_card`는 claim 단계에서 `unsupported`로 보고한다.
- `end_turn` 실행 직전에는 현재 `legal_actions`에서 `selected_action_id`를 다시 찾는다.
- 상태 id나 상태 버전이 맞지 않으면 실행하지 않고 `stale`로 보고한다.
- 실행 API가 불확실하면 턴 종료 버튼 핸들러 호출까지 조사한다.
- 그래도 마우스 좌표 클릭은 사용하지 않는다.

### 완료 기준

- Codex가 제출한 `end_turn`을 모드가 claim한다.
- claim된 `submission_id`가 한 번만 실행된다.
- 오래된 `state_version`의 행동은 실행하지 않고 `stale`로 보고한다.
- 미지원 행동은 실행하지 않고 `unsupported`로 보고한다.
- 실행 실패나 브리지 응답 없음 때문에 게임이 멈추지 않는다.
- 턴 종료 후 새 `combat_state`가 브리지에 들어온다.
- 브리지 로그에 claim, 실행 결과, 다음 상태가 남는다.

### 주요 위험

- 현재 exporter의 `state_id`가 export마다 바뀌므로, 실질 상태가 같아도 행동이 빠르게 오래된 것으로 보일 수 있다.
- `hand_0` 같은 `card_instance_id`는 손패 순서 기반이라 상태 변화 후 다른 카드를 가리킬 수 있다.
- STS2 내부 턴 종료 API를 잘못 호출하면 게임 흐름이 멈출 수 있다.

### 대응

- R5.1에서는 `end_turn`만 지원해 실행 표면을 줄인다.
- 실행 파라미터는 `latest_action`이 아니라 현재 `legal_actions`에서 다시 찾는다.
- claim과 result를 분리해 중복 실행을 막는다.
- 카드 실행은 대상 없는 카드, 대상 있는 카드 순서로 나중에 연다.

## R6: 실험 러너와 분석

### 목표

여러 시드와 반복 실행 결과를 비교 가능한 지표로 저장한다.

### 완료 기준

- 고정 시드 20개를 같은 조건으로 반복 실행할 수 있다.
- 사망 층, 받은 피해, 응답 실패율, 평균 응답 시간을 집계한다.
- 기준선 정책과 Codex 결과를 같은 표로 비교할 수 있다.

## R7: 전체 등반 확장

### 목표

전투 밖 선택을 추가해 1막 등반 실험을 만든다.

R7은 한 번에 모든 비전투 선택을 열지 않는다. 보상 선택, 지도 선택, 상점 선택 순서로 확장한다.

### 완료 기준

- 20개 고정 시드에서 1막 보스 도달률을 계산할 수 있다.
- 전투 실패와 비전투 선택 실패를 구분할 수 있다.
- 카드 보상 이후 덱 방향 실패를 태그로 남길 수 있다.

## 지금 하지 않을 것

- Codex가 직접 게임 파일을 수정하거나 조작하는 구조
- 예쁜 오버레이 UI
- 전체 카드 평가 시스템
- 사람 플레이 데이터를 대규모 수집하는 기능
- 자동 순위표 기록

## R4.5: combat_state.json 직접 전송

R4.5에서는 `CombatStateExporter`가 `combat_state.json`을 쓴 뒤 같은 JSON을 로컬 브리지 `http://127.0.0.1:17832/state`로도 보냅니다.

- 기존 파일 출력은 그대로 유지합니다.
- 브리지 전송은 `%APPDATA%\SlayTheSpire2\SpireMind\bridge_config.json`이나 `SPIREMIND_BRIDGE_ENABLED` 환경 변수로 끌 수 있습니다.
- 브리지 주소는 `bridge_config.json`의 `state_url`이나 `SPIREMIND_BRIDGE_STATE_URL` 환경 변수로 바꿀 수 있습니다.
- 브리지가 꺼져 있거나 전송이 실패해도 게임 흐름은 멈추지 않습니다.
- 전송은 짧은 timeout과 중복 방어를 둡니다.
- 이번 범위는 상태 전송까지만 포함하고, 게임 행동 실행은 포함하지 않습니다.
