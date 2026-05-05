# 세션 인수인계서

마지막 갱신: 2026-05-05

## 1. 현재 목표

SpireMind는 Slay the Spire 2를 LLM이 직접 플레이할 수 있도록 게임 상태를 안정적으로 내보내고, LLM이 선택 가능한 행동만 제출하도록 만드는 어댑터 프로젝트다.

이번 작업의 핵심 목표는 “잘하는 휴리스틱 AI”가 아니라 “LLM이 판단할 수 있는 충분한 상태와 합법 행동을 제공하는 어댑터”다. 위험 판단, 카드 가치 판단, 경로 판단은 LLM의 몫으로 두고, 어댑터는 다음 책임에 집중한다.

- 현재 화면과 게임 상태를 안정적으로 구분한다.
- 불안정한 중간 상태를 LLM에 노출하지 않는다.
- 실제 게임이 받을 수 있는 합법 행동만 `legal_actions`로 노출한다.
- 행동 실행 후 실제 상태 변화가 관찰될 때만 성공으로 보고한다.
- 카드 선택, 보상, 상점, 이벤트, 지도, 전투, 포션 흐름을 좌표 클릭이 아니라 내부 실행 경로로 처리한다.

## 2. 현재 깃 상태

브랜치:

- `codex/adapter-roadmap-work`

원격:

- `origin/codex/adapter-roadmap-work`에 푸시 완료

최근 커밋:

- `3c0d6df Remove bundled AI teammate reference dump`
  - `sts2AITeammate-default-366-1-2-5-1776564401` 폴더를 깃 추적에서 제거했다.
  - 로컬 파일은 남겼고, `.gitignore`에 추가해 재추적을 막았다.
- `b095884 Implement LLM adapter legal actions`
  - 이번 세션의 주요 어댑터 구현을 묶은 커밋이다.

현재 작업 트리:

- 코드/문서는 커밋 및 푸시 완료.
- `logs/`만 미추적 상태로 남아 있다. 실행 산출물이므로 커밋하지 않는 것이 맞다.

## 3. 현재 실행 상태

사용자 요청에 따라 실행 중이던 프로세스를 종료했다.

종료한 대상:

- Slay the Spire 2 게임 프로세스
- `bridge/spiremind_bridge.js`
- `scripts/spiremind_agent_daemon.js`
- 남아 있던 이 프로젝트 관련 Node 프로세스

현재는 게임과 브리지가 꺼져 있어야 한다. 다음 세션 시작 시 먼저 확인한다.

```powershell
Get-CimInstance Win32_Process |
  Where-Object {
    $_.Name -eq 'SlayTheSpire2.exe' -or
    (($_.Name -eq 'node.exe') -and ($_.CommandLine -match 'STSAutoplay|spiremind_bridge|spiremind_agent_daemon'))
  } |
  Select-Object ProcessId,Name,CommandLine
```

## 4. 이번 세션에서 구현한 핵심 내용

### 전투 상태와 합법 행동

주요 파일:

- `src/SpireMindMod/CombatStateExporter.cs`
- `src/SpireMindMod/CombatLegalActionBuilder.cs`
- `src/SpireMindMod/CombatSnapshotBuilder.cs`
- `src/SpireMindMod/RuntimePhaseResolver.cs`

구현 내용:

- 전투 상태 스냅샷 생성을 분리했다.
- 카드 비용은 표시값이 아니라 런타임에서 실제 변경된 비용을 읽도록 보강했다.
- `runtime_can_play_no_target`를 추가해 실제 `CanPlayTargeting(null)` 기준으로 무대상 카드 사용 가능 여부를 반영했다.
- `연무`처럼 “이번 턴 스킬 1장만 사용 가능” 상태에서 손패 카드가 보이더라도 실제로 못 쓰는 카드는 합법 행동에서 제외했다.
- 전투 중인데 화면 루트가 지도/보상/이벤트처럼 보이는 불안정한 중간 export를 차단했다.
- 전투 진입 직후 손패가 빈 배열이거나 `end_turn`만 보이는 중간 상태를 LLM에 노출하지 않도록 막았다.

### 행동 실행과 결과 검증

주요 파일:

- `src/SpireMindMod/CombatActionExecutor.cs`
- `src/SpireMindMod/CombatActionRuntimeContext.cs`
- `src/SpireMindMod/CombatActionBridgeClient.cs`
- `bridge/spiremind_bridge.js`

구현 내용:

- `play_card`, `use_potion`, `end_turn`, 보상 수령, 지도 이동, 이벤트 선택, 상점 구매, 카드 선택 등을 claim/result 구조로 처리한다.
- 행동 입력이 들어갔다는 사실만으로 성공 처리하지 않는다.
- 손패 수, 에너지, 방어도, 대상 체력, 포션 슬롯, 골드, 화면 전환 등 실제 변화가 관찰되어야 `applied`로 본다.
- 실패, 미지원, 무효, 오래된 상태는 같은 상태에서 다시 판단할 수 있도록 데몬 쪽 재시도 정책을 보강했다.
- `dismiss_game_over`, `continue_run`, `start_new_run` 등 메인 메뉴/게임오버 행동도 지원 목록에 포함했다.

### 카드 후속 선택

주요 파일:

- `src/SpireMindMod/AdapterCardSelectionBridge.cs`
- `src/SpireMindMod/CardSelectCmdHarmonyPatch.cs`
- `data/sts2_knowledge/card_selection_flows.json`
- `docs/card_selection_bridge_design.md`

구현 내용:

- `전투장비`, `박치기`, `지옥검`, `짓밟기` 등 후속 조작 또는 비용 변화가 있는 카드 흐름을 조사했다.
- UI 카드 플레이와 같은 내부 실행 경로를 쓰는 방향으로 설계를 바꿨다.
- 카드가 후속 카드 선택을 열면 `adapter_card_selection` phase로 export한다.
- LLM은 선택 가능한 카드 후보를 보고 `choose_card_selection`을 제출한다.
- 선택 완료 후 원래 `play_card`는 중간 대기 상태로 남지 않고 정상적으로 완료 처리된다.

### 포션

주요 파일:

- `src/SpireMindMod/CombatActionExecutor.cs`
- `docs/potion_reference_logic_review.md`

구현 내용:

- 포션은 인벤토리에 있을 때만 `legal_actions`로 노출한다.
- 포션 슬롯이 가득 찬 보상에서는 “받을 포션 / 버릴 포션” 흐름을 합법 행동으로 노출한다.
- 포션 사용은 게임 내부 포션 사용 경로를 기준으로 보강했다.
- 비용 감소 또는 카드 선택이 이어지는 포션은 후속 선택 흐름과 연결되도록 처리했다.

### 보상

주요 파일:

- `src/SpireMindMod/CombatStateExporter.cs`
- `src/SpireMindMod/CombatActionExecutor.cs`

구현 내용:

- 카드 보상은 `reward_type + model_id + index` 기준으로 안정 ID를 보강했다.
- 골드, 유물, 포션, 카드 보상 선택 및 넘기기를 각각 합법 행동으로 노출한다.
- 게임오버 이후 보상/메뉴 전환도 `dismiss_game_over`로 처리한다.

### 상점

주요 파일:

- `src/SpireMindMod/ShopRuntimeLocator.cs`
- `src/SpireMindMod/ShopSnapshotBuilder.cs`
- `src/SpireMindMod/ShopInventorySnapshotBuilder.cs`
- `src/SpireMindMod/ShopItemCandidateCollector.cs`
- `src/SpireMindMod/ShopLegalActionBuilder.cs`

구현 내용:

- 상점 화면에서 구매 가능한 카드, 유물, 포션, 카드 제거 행동을 export한다.
- 보유 골드와 실제 구매 가능 여부를 합법 행동에 반영한다.
- 상점 나가기 흐름도 지원한다.

### 이벤트

주요 파일:

- `src/SpireMindMod/EventOutcomeInterpreter.cs`
- `src/SpireMindMod/EventOutcomeInterpreterClearEvents.cs`
- `src/SpireMindMod/EventOutcomeInterpreterComplexEvents.cs`
- `src/SpireMindMod/EventOutcomeInterpreterVariedEvents.cs`

구현 내용:

- 이벤트 선택지에 안정 ID, fingerprint, 알려진 결과 요약을 붙인다.
- 위험 여부는 LLM이 판단하도록 두고, 어댑터는 선택지와 알려진 변화만 제공한다.
- 선택 직후 버튼이 비활성화되지만 전투 전환이 끝나지 않은 이벤트는 `continue_chosen_event_option` 형태로 다시 진행할 수 있게 했다.
- 실제 확인된 문제: `난타전(EVENT.PUNCH_OFF)`에서 선택지가 `was_chosen=true`, `is_enabled=false`인 상태로 멈췄다. 이를 처리하기 위해 이미 선택된 이벤트 선택지도 전환 계속 행동으로 노출했다.

### 지도

주요 파일:

- `src/SpireMindMod/CombatStateExporter.cs`
- `src/SpireMindMod/CombatActionExecutor.cs`

구현 내용:

- LLM이 “앞의 몇 칸”만 보는 것이 아니라 해당 스테이지의 전체 지도 연결을 볼 수 있도록 지도 스냅샷을 보강했다.
- 지도 노드에는 row/column, 자식 연결, 현재 선택 가능 여부가 포함된다.

### 지속 실행 데몬

주요 파일:

- `scripts/spiremind_agent_daemon.js`
- `docs/persistent_agent_daemon.md`
- `docs/llm_test_guide.md`

구현 내용:

- 매번 Codex를 새로 띄우는 방식 대신, 앱서버 방식으로 LLM 판단 루프를 계속 돌릴 수 있게 했다.
- 단순 행동 자동화 정책은 넣지 않았다. 현재 실행은 전부 LLM 판단으로 기록된다.
- 런 종료 후 사후 리포트를 생성하도록 했다.
- `run-count`, `max-decisions`, `timeout-ms`, `result-timeout-ms`, `model`, `effort`, `run-log-dir` 옵션을 사용한다.

## 5. 검증 결과

확인한 명령:

```powershell
dotnet build .\src\SpireMindMod\SpireMindMod.csproj
```

결과:

- 경고 0개
- 오류 0개

실전 실행에서 확인한 내용:

- 메인 메뉴 `continue_run` 정상 처리.
- 보상 수령, 카드 선택, 포션 수령, 보상 넘기기 정상 처리.
- 지도 이동과 이벤트 진입 정상 처리.
- `난타전` 이벤트 전환 문제를 발견하고 수정.
- 전투 중 카드 사용, 스킬 제한, 후속 카드 선택, 게임오버 처리 정상 동작 확인.
- LLM 세션에서 15층까지 도달한 런이 있었다.
- 1차/2차 사후 리포트가 생성됐다.

중요한 관찰:

- 최근 사망 원인은 어댑터 문제가 아니라 LLM 전략 문제로 보인다.
- 리포트상 `legal_actions` 위반이나 실행 실패는 핵심 원인이 아니었다.
- LLM은 체력이 낮은 상황에서도 방어/공격 전환 판단이 미흡했다.
- 단순히 많은 런을 돌리는 방식은 비교 기준이 흔들린다.

## 6. 다음 검증 설계 방향

사용자 판단:

- 검증을 제대로 하려면 시드를 고정하고 플레이해야 한다.
- 다음 검증 설계는 사용자가 따로 진행한다.

다음 세션에서 바로 벤치 실행을 이어가지 말 것.

권장 기준:

- 같은 시드
- 같은 캐릭터
- 같은 모델
- 같은 prompt/decision 정책
- 같은 모드 배포본
- 같은 최대 판단 횟수 또는 같은 종료 조건

이 기준이 없으면 “LLM이 나아졌는지”와 “운이 좋았는지”를 분리하기 어렵다.

## 7. 레퍼런스와 주의 사항

깃에서 제거한 것:

- `sts2AITeammate-default-366-1-2-5-1776564401/`

이 폴더는 로컬에는 남아 있지만 깃 추적에서 제거했고 `.gitignore`에 추가했다. 다시 커밋하지 않는다.

깃에 남긴 레퍼런스 문서/데이터:

- `data/sts2_knowledge/card_selection_flows.json`
- `docs/ironclad_card_effect_reference.md`
- `docs/ironclad_card_execution_classification.md`
- `docs/ironclad_followup_reference_implementation.md`
- `docs/potion_reference_logic_review.md`

참고용 디컴파일 산출물:

- `artifacts/decompiled/sts2`
- `artifacts/ai_teammate_decompiled`

이 산출물들은 다음 조사 때 참고하되, 원본 모드 덤프 폴더를 다시 추적하지 않도록 주의한다.

## 8. 다음 세션 시작 절차

1. 상태 확인

```powershell
git status --short --branch
git log --oneline -5
```

2. 실행 프로세스 확인

```powershell
Get-CimInstance Win32_Process |
  Where-Object {
    $_.Name -eq 'SlayTheSpire2.exe' -or
    (($_.Name -eq 'node.exe') -and ($_.CommandLine -match 'STSAutoplay|spiremind_bridge|spiremind_agent_daemon'))
  } |
  Select-Object ProcessId,Name,CommandLine
```

3. 빌드 확인

```powershell
dotnet build .\src\SpireMindMod\SpireMindMod.csproj
```

4. 배포

```powershell
.\scripts\deploy_mod.ps1 -Configuration Debug -ModsDir "I:\SteamLibrary\steamapps\common\Slay the Spire 2\mods" -AlsoDeployAppData
```

5. 브리지 실행

```powershell
Start-Process -FilePath "node" -ArgumentList @("bridge\spiremind_bridge.js") -WorkingDirectory "F:\Antigravity\STSAutoplay" -WindowStyle Hidden
```

6. 게임 실행

반드시 Steam 경로로 실행한다. 직접 exe를 실행하면 Steam 초기화 오류가 난다.

```powershell
Start-Process "steam://rungameid/2868840"
```

## 9. 자주 발생한 문제

### 직접 exe 실행 문제

증상:

- Steam 초기화 실패
- `No appID found`

대응:

- `Start-Process "steam://rungameid/2868840"` 사용.

### 전투 화면인데 `combat_play_phase=false`

의미:

- 화면은 전투처럼 보이지만 게임 런타임이 아직 플레이 가능한 전투 턴으로 확정되지 않았다는 뜻일 수 있다.
- 중간 export를 그대로 LLM에 주면 손패가 비거나 `end_turn`만 보이는 문제가 생긴다.

현재 대응:

- `RuntimePhaseResolver`와 불안정 export 차단 로직으로 보완했다.

### 카드가 보이지만 실제로 못 쓰는 상황

예시:

- `연무`: 한 턴에 스킬 1장만 사용 가능.

현재 대응:

- `runtime_can_play_no_target`를 기준으로 합법 행동에서 제외한다.

### 이벤트 선택 후 멈춤

예시:

- `난타전(EVENT.PUNCH_OFF)`.

현재 대응:

- 이미 선택된 비활성 선택지도 전환 계속 행동으로 노출한다.

## 10. 다음 작업 후보

사용자가 벤치마크 설계를 따로 하겠다고 했으므로, 다음 작업은 설계가 정해진 뒤 진행한다.

그 전까지 우선순위가 높은 후보:

1. 고정 시드 실행 프로토콜 설계 반영
2. 앱서버 데몬의 run record 구조 정리
3. 사후 리포트 인코딩 깨짐 원인 수정
4. LLM 판단 로그와 상태 스냅샷을 비교하기 쉬운 요약 생성
5. 포션/후속 카드 선택이 포함된 고정 시드 회귀 테스트 구성

## 11. 다음 세션 첫 메시지 예시

```text
F:\Antigravity\STSAutoplay에서 이어서 작업해줘.
먼저 docs/session_handoff.md를 읽고 git status를 확인해.
게임과 브리지는 현재 꺼져 있어야 해.
최근 커밋은 3c0d6df이고, sts2AITeammate 원본 덤프 폴더는 깃에서 제거했어.
다음 검증은 시드 고정 방식으로 설계해야 하니, 무작정 런을 돌리지 말고 먼저 실행 프로토콜을 맞춰.
```
