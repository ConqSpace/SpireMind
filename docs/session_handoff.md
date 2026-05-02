# 세션 인수인계서

마지막 갱신: 2026-04-28

## 1. 프로젝트 목표

SpireMind는 Slay the Spire 2를 AI가 직접 플레이하게 만드는 실험용 모드다.

이 모드는 플레이어를 돕는 도구가 아니다. AI가 게임 상태를 보고 어떤 행동을 선택하는지, 그 선택이 런 진행에 어떤 결과를 만드는지 관찰하고 기록하는 도구다.

중요한 명명 규칙:

- 프로젝트 이름, 변수명, 문서에서 `benchmark` 또는 `벤치마크`라는 표현을 쓰지 않는다.
- 대신 `실험`, `실행 기록`, `시나리오`, `run record`, `play session` 같은 표현을 쓴다.
- AI 판단기 입력에도 평가 환경임을 직접 암시하는 표현을 넣지 않는다.

## 2. 현재 구조

전체 흐름은 다음과 같다.

1. STS2 모드가 게임 상태를 추출한다.
2. 상태를 `%APPDATA%\SlayTheSpire2\SpireMind\combat_state.json`에 저장한다.
3. 같은 상태를 브리지 서버에 게시한다.
4. 의사결정 루프가 브리지 서버에서 상태를 읽는다.
5. 휴리스틱 또는 LLM 판단기가 `legal_actions` 중 하나를 고른다.
6. 게임 쪽 실행기가 제출된 행동을 claim한다.
7. 실행기는 Godot 메인 스레드에서 행동을 실제 게임에 적용한다.
8. 실행 결과와 상태 변화가 실행 기록에 남는다.

주요 파일:

- 모드 프로젝트: `src/SpireMindMod`
- 상태 추출기: `src/SpireMindMod/CombatStateExporter.cs`
- 행동 실행기: `src/SpireMindMod/CombatActionExecutor.cs`
- 행동 문맥 저장: `src/SpireMindMod/CombatActionRuntimeContext.cs`
- 브리지 클라이언트: `src/SpireMindMod/CombatActionBridgeClient.cs`
- 메인 스레드 틱 처리: `src/SpireMindMod/AutotestMainThreadTicker.cs`
- 브리지 서버: `bridge/spiremind_bridge.js`
- 의사결정 루프: `bridge/spiremind_decision_loop.js`
- Codex 판단기 어댑터: `scripts/codex_decider.js`
- 스모크 검사: `scripts/decision_loop_smoke_check.ps1`

배포 대상:

- `I:\SteamLibrary\steamapps\common\Slay the Spire 2\mods\SpireMind`
- `%APPDATA%\SlayTheSpire2\SpireMind`

## 3. 구현된 범위

현재 자동 실행은 다음 화면과 행동을 처리한다.

- 전투 상태 추출
- 손패, 뽑을 카드 더미, 버린 카드 더미, 소멸 카드 더미 추출
- 플레이어 체력, 방어도, 에너지, 버프/디버프 추출
- 적 체력, 방어도, 버프/디버프, 의도 추출
- 카드 사용
- 턴 종료
- 보상 수령
- 카드 보상 선택
- 보상 화면 진행 버튼
- 지도 노드 선택
- 이벤트 옵션 선택
- 모닥불 선택과 진행
- 게임 오버 상태 추출
- 덱 카드 선택 화면 최소 처리

덱 카드 선택 화면은 다음 공용 화면을 우선 지원한다.

- `NDeckEnchantSelectScreen`
- `NDeckUpgradeSelectScreen`
- `NDeckTransformSelectScreen`
- `NDeckCardSelectScreen`

이 처리는 특정 이벤트 전용이 아니다. 이벤트 선택 뒤 공용 덱 선택 화면이 뜨면 `phase: "card_selection"`으로 내보내고, 다음 행동을 실행한다.

- `choose_card_selection`
- `confirm_card_selection`

## 4. 최근 작업 내용

최근 문제는 `?` 방의 `자기계발서` 이벤트였다.

이벤트 첫 선택지는 정상적으로 눌렸지만, 그 뒤 인챈트할 카드를 고르는 화면에서 자동 실행이 멈췄다. 이를 해결하기 위해 `card_selection` 단계를 추가했다.

변경된 주요 동작:

- `AutotestMainThreadTicker`에서 카드 선택 화면을 이벤트/보상/지도보다 먼저 감지한다.
- `CombatStateExporter`가 카드 선택 화면을 JSON으로 내보낸다.
- 카드 목록에는 `card_selection_index`, `card_selection_id`, 카드 이름, 카드 ID, 선택 여부가 들어간다.
- `min_select`, `max_select`, `selected_count`를 함께 내보낸다.
- `CombatActionExecutor`가 카드 홀더를 찾아 `OnCardClicked` 또는 카드 홀더 신호로 선택을 실행한다.
- 선택 완료 뒤 `CheckIfSelectionComplete`, `CompleteSelection`, `ConfirmSelection`, 확인 버튼 순서로 확정을 시도한다.
- 휴리스틱은 `card_selection` 상태에서 카드를 고르거나 선택을 확정한다.

## 5. 검증 결과

마지막 검증 결과:

- `dotnet build .\src\SpireMindMod\SpireMindMod.csproj` 통과
- `dotnet build .\src\SpireMindMod\SpireMindMod.csproj -c Release` 통과
- `node --check .\bridge\spiremind_decision_loop.js` 통과
- 합성 `card_selection` 상태에서 선택 행동 결정 통과
- 합성 `card_selection` 상태에서 확인 행동 결정 통과
- `.\scripts\decision_loop_smoke_check.ps1 -BridgeUrl http://127.0.0.1:17833` 통과
- Release DLL 배포 완료

배포된 DLL 크기:

- `I:\SteamLibrary\steamapps\common\Slay the Spire 2\mods\SpireMind\SpireMind.dll`: 306688 bytes
- `%APPDATA%\SlayTheSpire2\SpireMind\SpireMind.dll`: 306688 bytes

## 6. 현재 작업 트리 상태

마지막 확인 기준으로 다음 파일에 수정이 남아 있다.

- `bridge/spiremind_decision_loop.js`
- `scripts/decision_loop_smoke_check.ps1`
- `src/SpireMindMod/AutotestCommandChannel.cs`
- `src/SpireMindMod/AutotestMainThreadTicker.cs`
- `src/SpireMindMod/CombatActionBridgeClient.cs`
- `src/SpireMindMod/CombatActionExecutor.cs`
- `src/SpireMindMod/CombatActionRuntimeContext.cs`
- `src/SpireMindMod/CombatStateExporter.cs`

주의:

- 이 변경들은 모두 되돌리지 않는다.
- 일부는 이번 카드 선택 처리 이전의 자동 실행, 지도, 모닥불, 실행 기록 작업에서 나온 변경이다.
- 새 세션에서는 먼저 `git status --short`와 `git diff --stat`를 확인한다.

## 7. 남은 위험

아직 주의해야 할 부분은 다음과 같다.

- 실제 게임에서 `card_selection` 실행은 합성 상태보다 더 복잡할 수 있다.
- 다중 카드 선택 이벤트에서 휴리스틱이 너무 빨리 `confirm_card_selection`을 고를 수 있다.
- `min_select < max_select`인 선택 화면에서는 “더 고를지, 여기서 멈출지” 정책이 필요하다.
- 카드 제거 화면이 `NDeckCardSelectScreen`으로 처리되는지 실제 확인이 필요하다.
- 특수 이벤트 화면과 상점형 화면은 아직 공용 실행기로 정리되지 않았다.
- 모르는 오버레이가 뜨면 현재 자동 실행이 의미 없는 이전 상태를 계속 처리할 수 있다.
- 브리지에 합성 상태를 올린 뒤 실제 상태로 되돌렸지만, 새 세션에서는 항상 `/health`와 `/state/current`를 확인한다.

## 8. 다음 작업 순서

## 8. 2026-04-29 이벤트 관측 추가

- 고정 시드 `7MJCUHEB5Q`에서 직접 `?` 방까지 이동해 다음 이벤트를 확인했다.
- 이벤트 ID: `EVENT.THE_FUTURE_OF_POTIONS`
- 이벤트 제목: `포션의 미래?`
- 현재 선택지:
  - `choose_event_option_0`: `약화 포션`을 잃고 강화된 일반 스킬 카드를 얻는다.
  - `choose_event_option_1`: `폭발성 앰플`을 잃고 강화된 일반 스킬 카드를 얻는다.
- 대응 규칙:
  - 둘 다 즉사 위험은 없다.
  - 휴리스틱은 `약화 포션`을 더 낮은 가치의 소모 대상으로 보고 `choose_event_option_0`을 우선 선택한다.
- 코드 반영:
  - `bridge/spiremind_decision_loop.js`에 `EVENT.THE_FUTURE_OF_POTIONS` 전용 점수 함수를 추가했다.
  - `node --check .\bridge\spiremind_decision_loop.js` 통과.
  - 현재 이벤트 상태에서 `--dry-run` 결과는 `choose_event_option_0`이다.

권장 순서는 다음과 같다.

1. 다중 카드 선택 정책 보강

   `min_select`, `max_select`, `selected_count`를 기준으로 행동을 고른다.

   - `selected_count < min_select`: 반드시 추가 선택
   - `min_select == max_select`이고 `selected_count < max_select`: 계속 추가 선택
   - `selected_count >= min_select`: 확인 가능
   - `min_select < max_select`: 휴리스틱은 기본적으로 최소 수만 채우고 확인
   - LLM 모드에서는 카드 목록과 선택 목적을 보고 더 고를지 판단

2. 실제 `?` 방 카드 선택 검증

   `자기계발서` 또는 유사 이벤트에서 다음 흐름을 확인한다.

   - 이벤트 옵션 선택
   - `phase: "card_selection"` 게시
   - 카드 선택 실행
   - 확인 실행
   - 이벤트 종료 또는 다음 화면 전환

3. 알 수 없는 오버레이 안전 처리

   지원하지 않는 화면이 뜨면 `phase: "unknown_overlay"`로 내보내고 자동 행동을 멈춘다.

   JSON에는 최소한 다음 값을 남긴다.

   - 화면 타입
   - 보이는 버튼 후보
   - 현재 기존 phase
   - 자동 실행 중지 사유

4. 이벤트 관측 로그 추가

   `?` 방마다 다음 정보를 실행 기록에 남긴다.

   - 이벤트 ID
   - 선택지 목록
   - 선택한 선택지
   - 후속 화면 타입
   - 획득한 보상
   - 잃은 체력
   - 얻거나 잃은 카드, 유물, 골드, 포션

5. 모닥불, 지도, 이벤트, 보상까지 런 지속 안정화

   목표는 게임 오버가 날 때까지 사람이 개입하지 않아도 런을 이어가는 것이다.

## 9. 새 세션 첫 지시문

새 세션에는 아래처럼 요청하면 된다.

```text
F:\Antigravity\STSAutoplay에서 이어서 작업해줘.
먼저 git status와 최근 diff를 확인하고, docs/session_handoff.md를 읽어.
목표는 SpireMind의 자동 실행 안정화야.
지금은 card_selection 최소 구현까지 끝났고, 다음 작업은 다중 카드 선택 정책 보강이야.
프로젝트와 문서, 변수명에 benchmark/벤치마크라는 표현은 쓰지 마.
```

## 10. 빠른 확인 명령

```powershell
git status --short
git diff --stat
dotnet build .\src\SpireMindMod\SpireMindMod.csproj
node --check .\bridge\spiremind_decision_loop.js
.\scripts\decision_loop_smoke_check.ps1 -BridgeUrl http://127.0.0.1:17833
Invoke-RestMethod -Method Get -Uri "http://127.0.0.1:17832/health" -TimeoutSec 2
```

## 11. 카드 선택 n개 처리 구현

카드 목록에서 n개를 고르는 공용 동작을 추가했다. 강화, 제거, 변환, 이벤트 보상처럼 `phase: "card_selection"`을 쓰는 화면에서 재사용하는 목적이다.

- `bridge/spiremind_decision_loop.js`
  - `card_selection.selected_count`, `min_select`, `max_select`를 기준으로 더 골라야 하는지 판단한다.
  - 필요한 수만큼 고른 뒤 `confirm_card_selection`을 선택한다.
  - `upgrade` 또는 `enchant` 목적이면 강타, 소용돌이, 완벽한 타격 같은 핵심 공격 카드와 높은 비용 카드를 우선한다.
  - `remove` 또는 `transform` 목적이면 저주, 상태 이상, 타격, 수비 순서로 우선한다.
- `bridge/spiremind_bridge.js`
  - LLM 응답의 `actions` 배열에서 `choose_card_selection`과 `confirm_card_selection`을 해석할 수 있게 했다.
  - 카드 선택은 `card_selection_index`, `card_selection_id`, `card_id`, `name`으로 legal action과 매칭한다.
- `src/SpireMindMod/CombatStateExporter.cs`
  - `max_select`를 넘기면 추가 `choose_card_selection` action을 내보내지 않는다.
  - legal action에 `card_type`, `rarity`, `cost`, `upgraded` 정보를 추가했다.

검증:

```powershell
node --check .\bridge\spiremind_decision_loop.js
node --check .\bridge\spiremind_bridge.js
dotnet build .\src\SpireMindMod\SpireMindMod.csproj -c Release --no-restore
git diff --check -- bridge/spiremind_decision_loop.js bridge/spiremind_bridge.js src/SpireMindMod/CombatStateExporter.cs docs/session_handoff.md
```

주의:

- 현재 실행 중인 게임에는 새 C# exporter DLL이 자동 반영되지 않는다. `CombatStateExporter.cs` 변경을 실제 게임에 적용하려면 모드를 다시 배포하고 게임을 재시작해야 한다.
- Node 쪽 bridge/decision loop 변경도 실행 중인 Node 프로세스가 오래 떠 있다면 재시작해야 확실하다.
