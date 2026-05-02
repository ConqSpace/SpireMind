# 세션 인수인계서

마지막 갱신: 2026-05-02

## 1. 현재 목표

SpireMind는 Slay the Spire 2를 AI가 직접 플레이하게 만드는 실험용 모드다.

현재 핵심 목표는 휴리스틱을 똑똑하게 만드는 것이 아니다. LLM이 게임 상태를 관찰하고, 가능한 행동을 선택하고, 그 행동 결과를 검증할 수 있는 어댑터를 만드는 것이다.

중요한 명명 규칙:

- 프로젝트 이름, 변수명, 문서에서 `benchmark` 또는 `벤치마크`라는 표현을 쓰지 않는다.
- 대신 `실험`, `실행 기록`, `시나리오`, `run record`, `play session` 같은 표현을 쓴다.
- AI 판단기 입력에도 평가 환경임을 직접 암시하는 표현을 넣지 않는다.

## 2. 새 세션에서 먼저 읽을 문서

다음 스레드는 아래 순서로 문서를 읽으면 된다.

1. `docs/session_handoff.md`
   - 현재 상태, 남은 문제, 실행 절차를 확인한다.
2. `docs/adapter_design_notes.md`
   - LLM 조작 가능 어댑터의 설계 방향을 확인한다.
3. `docs/adapter_implementation_roadmap.md`
   - 실제 개발 순서와 남은 작업을 확인한다.
4. `docs/ai_teammate_gap_roadmap.md`
   - AI Teammate 레퍼런스와 우리 프로젝트의 차이를 확인한다.
5. `docs/action_schema.md`
   - `legal_actions`와 action payload 구조를 확인한다.
6. `docs/action_execution_design.md`
   - action claim, 실행, 결과 보고 흐름을 확인한다.
7. `docs/state_schema.md`
   - 상태 JSON의 큰 구조를 확인한다.
8. `docs/run_memory_logging.md`
   - 실행 기록과 이벤트 로그가 남는 방식을 확인한다.
9. `docs/game_launch_guide.md`
   - 게임 실행, 모드 배포, 고정 시드 시작 절차를 확인한다.

상점 작업을 이어갈 때는 `data/sts2_knowledge/action_flows.json`도 함께 확인한다. 이 파일은 상점 행동 절차처럼 매번 프롬프트에 넣지 않을 문맥을 저장하는 용도다.

## 3. 레퍼런스 위치

AI Teammate 원본 모드:

- `sts2AITeammate-default-366-1-2-5-1776564401/sts2AITeammate.dll`
- `sts2AITeammate-default-366-1-2-5-1776564401/sts2AITeammate.json`
- `sts2AITeammate-default-366-1-2-5-1776564401/config/ai-behavior/*.aiconfig`

AI Teammate 디컴파일 결과:

- `artifacts/ai_teammate_decompiled/AITeammate.Scripts`

특히 다시 볼 파일:

- 이벤트 관찰과 선택:
  - `artifacts/ai_teammate_decompiled/AITeammate.Scripts/EventRuntimeLocator.cs`
  - `artifacts/ai_teammate_decompiled/AITeammate.Scripts/EventSnapshotBuilder.cs`
  - `artifacts/ai_teammate_decompiled/AITeammate.Scripts/EventPlanner.cs`
  - `artifacts/ai_teammate_decompiled/AITeammate.Scripts/EventHandlerRegistry.cs`
  - `artifacts/ai_teammate_decompiled/AITeammate.Scripts/*EventHandler.cs`
- 상점:
  - `artifacts/ai_teammate_decompiled/AITeammate.Scripts/ShopRuntimeLocator.cs`
  - `artifacts/ai_teammate_decompiled/AITeammate.Scripts/ShopSnapshotBuilder.cs`
  - `artifacts/ai_teammate_decompiled/AITeammate.Scripts/ShopInventoryResolver.cs`
  - `artifacts/ai_teammate_decompiled/AITeammate.Scripts/ShopPlanner.cs`
- 보물, 지도:
  - `artifacts/ai_teammate_decompiled/AITeammate.Scripts/AiTeammateMapAndTreasurePatches.cs`
- 모닥불:
  - `artifacts/ai_teammate_decompiled/AITeammate.Scripts/AiTeammateRestSitePatches.cs`
- 카드 선택:
  - `artifacts/ai_teammate_decompiled/AITeammate.Scripts/AiTeammateCardSelectionPatches.cs`
- 보상:
  - `artifacts/ai_teammate_decompiled/AITeammate.Scripts/AiTeammateRewardPatches.cs`
- 전투:
  - `artifacts/ai_teammate_decompiled/AITeammate.Scripts/AiTeammateCombatTurnPatches.cs`
  - `artifacts/ai_teammate_decompiled/AITeammate.Scripts/CombatTurnLinePlanner.cs`
  - `artifacts/ai_teammate_decompiled/AITeammate.Scripts/CombatActionScorer.cs`
- 포션:
  - `artifacts/ai_teammate_decompiled/AITeammate.Scripts/PotionHeuristicEvaluator.cs`
  - `artifacts/ai_teammate_decompiled/AITeammate.Scripts/PotionMetadataBuilder.cs`

게임 원본 디컴파일 결과:

- `artifacts/decompiled/sts2`

방, 지도, 보물, 자동 실행 레퍼런스를 볼 때 우선 확인할 위치:

- `artifacts/decompiled/sts2/MegaCrit.Sts2.Core.AutoSlay`
- `artifacts/decompiled/sts2/MegaCrit.Sts2.Core.AutoSlay.Handlers`
- `artifacts/decompiled/sts2/MegaCrit.Sts2.Core.Map`
- `artifacts/decompiled/sts2/MegaCrit.Sts2.Core.Rooms`
- `artifacts/decompiled/sts2/MegaCrit.Sts2.Core.Rewards`
- `artifacts/decompiled/sts2/MegaCrit.Sts2.Core.Screens`
- `artifacts/decompiled/sts2/MegaCrit.Sts2.Core.Saves.Runs`

## 4. 프로젝트 폴더 구조

주요 폴더:

- `src/SpireMindMod`
  - STS2 안에서 실행되는 C# 모드 코드.
- `bridge`
  - Node 기반 브리지 서버와 의사결정 루프.
- `scripts`
  - 빌드, 배포, 실행 확인, Codex 판단기 스크립트.
- `docs`
  - 설계 문서와 작업 로드맵.
- `data/sts2_knowledge`
  - LLM 또는 휴리스틱이 필요할 때 참고할 절차형 지식.
- `artifacts/decompiled/sts2`
  - STS2 원본 디컴파일 참고 자료.
- `artifacts/ai_teammate_decompiled`
  - AI Teammate 모드 디컴파일 참고 자료.
- `sts2AITeammate-default-366-1-2-5-1776564401`
  - 사용자가 가져온 AI Teammate 원본 모드.
- `artifacts/verify-mods`
  - 배포 확인용 모드 산출물.

핵심 파일:

- `src/SpireMindMod/CombatStateExporter.cs`
  - 현재 게임 상태와 `legal_actions`를 JSON으로 추출한다.
- `src/SpireMindMod/CombatActionExecutor.cs`
  - 브리지에서 claim한 행동을 실제 게임에 적용한다.
- `src/SpireMindMod/CombatActionBridgeClient.cs`
  - 게임과 브리지 서버 사이의 action claim/result 통신을 담당한다.
- `src/SpireMindMod/CombatActionRuntimeContext.cs`
  - 실행기가 필요한 런타임 문맥을 저장한다.
- `src/SpireMindMod/AutotestMainThreadTicker.cs`
  - Godot 메인 스레드에서 상태 추출과 행동 실행을 호출한다.
- `bridge/spiremind_bridge.js`
  - 상태 수신, action 제출, action 결과 기록, 실행 로그 생성을 담당한다.
- `bridge/spiremind_decision_loop.js`
  - 현재 휴리스틱 의사결정 루프.
- `scripts/codex_decider.js`
  - Codex 판단기 연결 지점.
- `scripts/deploy_mod.ps1`
  - Release DLL을 게임 모드 폴더로 배포한다.

## 5. 현재 실행 상태

사용자가 게임을 직접 종료했다.

브리지는 종료하지 않았다고 했다. 따라서 다음 세션에서는 브리지가 아직 떠 있을 수 있다. 다만 게임이 꺼졌기 때문에 `/health`가 살아 있어도 최신 게임 상태가 아닐 수 있다.

마지막으로 확인된 브리지 run directory 예시:

- `%APPDATA%\SlayTheSpire2\SpireMind\bridge_runs\20260502_232256_161_39080_ccc693`

새 세션에서는 먼저 다음을 확인한다.

```powershell
git status --short --branch
git diff --stat
Invoke-RestMethod -Method Get -Uri "http://127.0.0.1:17832/health" -TimeoutSec 2
Invoke-RestMethod -Method Get -Uri "http://127.0.0.1:17832/state/current" -TimeoutSec 2
Invoke-RestMethod -Method Get -Uri "http://127.0.0.1:17832/action/latest" -TimeoutSec 2
```

브리지가 오래 떠 있으면 새 코드가 반영되지 않았을 수 있다. 코드 변경 뒤 테스트하려면 브리지와 게임을 모두 새로 시작하는 편이 안전하다.

## 6. 현재 작업 트리 상태

현재 브랜치:

- `codex/adapter-roadmap-work`

현재 커밋되지 않은 파일:

- `bridge/spiremind_bridge.js`
- `src/SpireMindMod/CombatActionExecutor.cs`
- `src/SpireMindMod/CombatStateExporter.cs`

마지막 diff 규모:

- `bridge/spiremind_bridge.js`: action 결과와 다음 상태 연결, 상태 변화 기록 확장.
- `src/SpireMindMod/CombatActionExecutor.cs`: 지도 이동 비동기 완료 판정, 전투 행동 결과 검증.
- `src/SpireMindMod/CombatStateExporter.cs`: 전투와 이벤트 action execution 메타데이터, event fingerprint 추가.

주의:

- 이 변경들은 되돌리지 않는다.
- 다음 세션에서 먼저 빌드와 문법 검사를 통과시킨 뒤 커밋한다.

## 7. 이번 세션에서 완료한 작업

상점:

- 상점에서 카드, 유물, 포션, 카드 제거 행동을 LLM이 판단할 수 있도록 관찰 및 행동 지원을 확장했다.
- `cancel_card_selection`을 추가했다.
- 포션 슬롯 제한 검증을 추가했다.
- 상점 행동 절차 메타데이터 JSON을 추가했다.
- 상점 방문 중 행동 기록을 추가했다.
- 유물과 포션 설명 추출을 안정화했다.
- 상점 유물 아이콘 문제는 “지도 노드 아이콘”이 아니라 “상점 판매 유물 아이콘이 상태에서 비어 보이는 문제”로 정정했다.

보물:

- 보물상자 열기 action을 추가했다.
- 보물 유물 획득과 진행 action을 추가했다.
- 관련 커밋:
  - `aa4954d Add treasure chest open action`
  - `3d968dd Handle treasure relic pickup and proceed`

지도:

- 지도 노드 클릭 뒤 `Thread.Sleep`으로 기다리던 구조를 제거했다.
- `RoomEntered` 이벤트와 상태 전환을 비동기로 관찰해 완료를 판정하도록 바꿨다.
- 목적은 “실제로 이동했는데 action 결과만 failed로 찍히는 문제”를 줄이는 것이다.

전투:

- `legal_actions[].execution` 메타데이터를 추가했다.
- `play_card`, `use_potion`, `end_turn`에 실행 전 검증값을 붙였다.
- 전투 행동은 입력 성공만으로 `applied` 처리하지 않는다.
- 손패 수, 에너지, 대상 체력, 포션 상태처럼 실제 상태 변화가 관찰될 때 `applied`로 처리한다.
- 상태 변화가 제한 시간 안에 없으면 `failed`로 처리한다.

이벤트:

- 이벤트 전체 fingerprint와 옵션별 fingerprint를 추가했다.
- 이벤트 legal action에 `event_action_execution.v1` 메타데이터를 붙였다.
- 옵션 index, 잠김 여부, 실행 전 재검증 정보를 포함했다.

브리지:

- action 제출 시 `pre_action_state_summary`를 저장한다.
- action 결과가 보고된 뒤 다음 상태를 받으면 `post_result_state_summary`와 `state_delta`를 계산한다.
- `action_state_observed` 이벤트를 `events.jsonl`에 기록한다.

## 8. 검증한 내용

통과한 명령:

```powershell
dotnet build .\src\SpireMindMod\SpireMindMod.csproj -c Release
node --check .\bridge\spiremind_bridge.js
```

실제 런에서 확인한 내용:

- 카드 사용 action은 상태 변화가 감지되어 `applied`로 기록됐다.
- 관찰된 변화:
  - 플레이어 에너지 변화
  - 손패 수 감소
  - 선택한 카드가 손패에서 사라짐
  - 대상 적 체력 변화
- 포션 사용 action은 큐 입력은 되었지만 상태 변화가 없어 `failed`로 기록됐다.

중요한 해석:

- 포션 실패는 새 검증 로직이 의도대로 작동한 결과다.
- 하지만 포션 실행 경로 자체는 아직 불완전하다. AI Teammate와 STS2 원본 레퍼런스를 다시 확인해야 한다.

## 9. 남은 문제와 우선순위

1. 포션 실행 경로 재조사
   - 현재 `use_potion`은 입력만 들어가고 실제 상태 변화가 없었다.
   - `UsePotionAction`, `BeforeUse`, action queue, 대상 지정 흐름을 레퍼런스에서 다시 확인해야 한다.

2. 이벤트 실제 화면 검증
   - fingerprint와 execution 메타데이터는 빌드만 확인했다.
   - 실제 `?` 방에서 옵션 선택, 후속 카드 선택, 결과 기록까지 검증해야 한다.

3. 지도 이동 재검증
   - 비동기 완료 판정으로 수정했지만, 다음 노드 이동에서 한 번 더 실측해야 한다.

4. 상점 판매 유물 아이콘/설명 재확인
   - 유물 상태가 안정적으로 JSON에 나오는지 확인한다.
   - 아이콘 자체가 필요하면 atlas 또는 texture key를 어떻게 표현할지 별도 판단한다.

5. 현재 dirty 변경 커밋
   - 빌드와 문법 검사를 통과한 뒤 커밋한다.

## 10. 배포와 재시작 절차

빌드:

```powershell
dotnet build .\src\SpireMindMod\SpireMindMod.csproj -c Release
node --check .\bridge\spiremind_bridge.js
```

배포:

```powershell
.\scripts\deploy_mod.ps1 -ModsDir "I:\SteamLibrary\steamapps\common\Slay the Spire 2\mods"
```

브리지 실행:

```powershell
node .\bridge\spiremind_bridge.js --http-host 127.0.0.1 --http-port 17832
```

게임 실행:

```powershell
Start-Process "steam://rungameid/2868840"
```

주의:

- C# 모드 변경은 게임을 재시작해야 반영된다.
- Node 브리지 변경은 브리지 프로세스를 재시작해야 반영된다.
- 지금은 게임은 꺼졌고 브리지는 켜져 있을 수 있다.

## 11. 새 세션 첫 지시문

새 세션에는 아래처럼 요청하면 된다.

```text
F:\Antigravity\STSAutoplay에서 이어서 작업해줘.
먼저 docs/session_handoff.md를 읽고, git status와 최근 diff를 확인해.
게임은 꺼져 있고 브리지는 아직 떠 있을 수 있어.
목표는 휴리스틱 강화가 아니라 LLM이 조작 가능한 어댑터를 완성하는 거야.
현재 dirty 파일은 bridge/spiremind_bridge.js, src/SpireMindMod/CombatActionExecutor.cs, src/SpireMindMod/CombatStateExporter.cs야.
우선 빌드와 node 문법 검사를 통과시킨 뒤 커밋하고, 그 다음 포션 실행 경로와 이벤트 실제 화면 검증을 이어가.
프로젝트와 문서, 변수명에 benchmark/벤치마크라는 표현은 쓰지 마.
```

## 12. 빠른 확인 명령

```powershell
git status --short --branch
git diff --stat
git diff -- bridge/spiremind_bridge.js src/SpireMindMod/CombatActionExecutor.cs src/SpireMindMod/CombatStateExporter.cs
dotnet build .\src\SpireMindMod\SpireMindMod.csproj -c Release
node --check .\bridge\spiremind_bridge.js
Invoke-RestMethod -Method Get -Uri "http://127.0.0.1:17832/health" -TimeoutSec 2
Invoke-RestMethod -Method Get -Uri "http://127.0.0.1:17832/state/current" -TimeoutSec 2
Invoke-RestMethod -Method Get -Uri "http://127.0.0.1:17832/action/latest" -TimeoutSec 2
```
