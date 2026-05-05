# 런타임 상태 어댑터 재설계 기준

마지막 갱신: 2026-05-03

## 1. 결론

기존 `CombatStateExporter` 중심 구조는 폐기 대상으로 본다.

이유는 명확하다. 현재 구조는 `ActiveScreenContext.GetCurrentScreen()`과 화면 객체 탐색 결과를 게임 단계 판정의 주된 근거로 사용한다. 하지만 STS2 원본의 `ActiveScreenContext`는 UI 초점과 열린 화면을 알려주는 보조 장치다. 실제 게임 진행 단계의 권위자가 아니다.

대표 증상:

- 실제 전투가 진행 중인데 `phase=map`이 export됐다.
- `phase=map` 상태 안에 `combat_in_progress=true`, `combat_play_phase=true`, `current_room=CombatRoom`이 동시에 들어갔다.
- 그 결과 LLM이 전투 중에 지도 노드 선택 행동을 받을 수 있는 위기 상황이 생겼다.

새 구조는 화면 탐색을 먼저 하지 않는다. 항상 런타임 권위자를 먼저 확인한다.

## 2. 권위자 우선순위

게임 단계 판정은 아래 순서를 따른다.

1. `CombatManager.Instance`
   - `IsInProgress=true`이면 전투가 최우선이다.
   - `DebugOnlyGetState()`가 null이 아니면 전투 snapshot의 기준 객체로 사용한다.
   - `IsPlayPhase=true`이면 `phase=combat_turn`이다.
   - 전투가 끝나는 중이면 보상, 게임 오버, 지도 전환보다 먼저 전투 종료 안정화를 기다린다.

2. 명시적 후속 선택 화면
   - 카드 선택, 보상, 이벤트 선택, 유물 선택처럼 플레이어 입력을 요구하는 화면이다.
   - 단, 전투 진행 중인 경우에는 전투보다 앞서지 않는다.

3. `RunManager.Instance.DebugOnlyGetState()`
   - 현재 방, 지도 좌표, 플레이어, 덱, 골드, 방문 상태의 권위자다.
   - 상점은 `CurrentRoom is MerchantRoom`과 `MerchantInventory`를 기준으로 한다.
   - 지도는 `NMapScreen.IsOpen`만으로 판정하지 않는다. 런 상태의 현재 방과 이동 가능 노드를 함께 확인한다.

4. UI 화면 객체
   - 실제 클릭 가능성, 버튼 활성화, 인벤토리 열림 여부를 확인하는 보조 신호다.
   - UI 신호만으로 `phase`를 결정하지 않는다.

## 3. 금지 상태

아래 상태는 안정 상태로 내보내지 않는다.

- `phase=map`이면서 `combat_in_progress=true`
- `phase=shop`이면서 현재 방이 `MerchantRoom`이 아님
- `phase=combat_turn`인데 플레이어, 손패, 적 목록을 얻지 못함
- `phase=reward`인데 선택 가능한 보상이나 진행 버튼 상태를 확인하지 못함
- `legal_actions`가 현재 `phase`와 다른 종류의 행동을 포함함

이런 상태가 발견되면 `phase=unstable` 또는 마지막 안정 상태 유지 중 하나를 선택한다. LLM에는 잘못된 행동 후보를 넘기지 않는다.

## 4. 새 모듈 경계

기존 거대 exporter를 다음 책임으로 분리한다.

### `RuntimePhaseResolver`

역할:

- 현재 런타임 단계 판정
- 권위자별 진단 정보 수집
- 모순 상태 감지

출력:

- `phase`
- `authority`
- `is_stable`
- `unstable_reason`
- `runtime_refs`

### `CombatSnapshotBuilder`

권위자:

- `CombatManager.Instance`
- `CombatManager.Instance.DebugOnlyGetState()`
- `LocalContext.GetMe(RunManager.Instance.DebugOnlyGetState())` 또는 동등한 플레이어 해석

역할:

- 플레이어, 손패, 적, 포션, 유물, 전투 행동 후보 생성
- 전투 중에는 지도, 상점, 보상 행동을 생성하지 않음

### `MapSnapshotBuilder`

권위자:

- `RunManager.Instance.DebugOnlyGetState()`
- `NMapScreen`은 열림 여부와 클릭 가능 노드 확인에만 사용
- 이동 완료는 `RunManager.RoomEntered`로 검증

역할:

- 현재 선택 가능한 다음 노드 생성
- 장기 경로 정보는 나중에 보강

### `ShopSnapshotBuilder`

권위자:

- `player.RunState.CurrentRoom is MerchantRoom`
- `MerchantInventory`

역할:

- 카드, 유물, 포션, 카드 제거 서비스를 `shop.items[]`로 정규화
- 구매 가능 여부와 가격을 런타임 모델 기준으로 산출
- UI는 인벤토리 열림과 실행 가능성 보조 확인에만 사용

### `RewardSnapshotBuilder`

권위자:

- `RewardsSet`, reward model, 현재 보상 화면
- 전투 종료 직후에는 전투 종료 안정화가 끝난 뒤 활성화

역할:

- 보상 ID 안정화
- 보상 수령, 카드 보상 선택, 진행 후보 생성

### `EventSnapshotBuilder`

권위자:

- 현재 방의 이벤트 모델
- 선택지의 `text_key`, `option_index`, 잠김 여부

역할:

- 이벤트 fingerprint
- 알 수 없는 효과는 `has_unknown_effects=true`

## 5. 레거시에서 남길 수 있는 것

삭제 대상:

- `ActiveScreenContext` 결과만으로 phase를 정하는 흐름
- 화면별 export 함수가 서로 독립적으로 상태 파일을 덮어쓰는 구조
- 한 파일에서 전투, 지도, 상점, 이벤트, 보상, 카드 선택을 모두 만드는 구조
- `phase`와 맞지 않는 `legal_actions`를 그대로 내보내는 구조

재사용 가능:

- reflection 헬퍼
- JSON 정규화 유틸리티
- 브리지 게시 코드
- action submit, claim, result 계약
- 일부 legal action 직렬화 형태
- 검증 결과 기록과 `state_delta` 계산

## 6. 구현 순서

1. `RuntimePhaseResolver`를 새로 만든다.
   - 아직 기존 exporter를 삭제하지 않고, 현재 phase 판정 결과와 새 resolver 결과를 diagnostics에 함께 기록한다.

2. 전투 export를 새 resolver 기준으로 교체한다.
   - `CombatManager.IsInProgress=true`이면 반드시 전투 snapshot만 내보낸다.
   - `phase=map && combat_in_progress=true`를 차단한다.

3. `CombatSnapshotBuilder`를 분리한다.
   - 전투 snapshot 생성과 전투 legal action 생성을 기존 exporter에서 떼어낸다.

4. 지도 export를 새 resolver 기준으로 교체한다.
   - 지도는 전투가 완전히 끝난 뒤에만 활성화한다.
   - 지도 이동 완료는 `RoomEntered`로만 성공 처리한다.

5. 상점, 보상, 이벤트를 같은 방식으로 분리한다.
   - 각 화면은 모델 권위자와 UI 보조 신호를 따로 기록한다.

6. 기존 `CombatStateExporter`를 얇은 라우터로 축소하거나 삭제한다.

## 6.1 현재 진행 상태

2026-05-03 기준 진행 상태:

- `RuntimePhaseResolver` 생성 완료.
- 전투 중 화면 export 차단 완료.
- 전투 중에는 `CombatManager.DebugOnlyGetState()` 기준 전투 상태를 우선 export하도록 변경 완료.
- 전투 JSON 조립 책임을 `CombatSnapshotBuilder`로 1차 분리 완료.
- 전투 `legal_actions` 생성 책임을 `CombatLegalActionBuilder`로 분리 완료.
- 상점 관찰은 `MerchantRoom.Inventory`를 우선 사용하도록 보강 시작.
- 상점 카드 슬롯은 `character_card`, `colorless_card`를 보존하도록 수정.
- 상점 상태에 현재 방과 런타임 인벤토리 진단 필드를 추가.
- 상점 JSON 조립 책임을 `ShopSnapshotBuilder`로 1차 분리 완료.
- 상점 `legal_actions` 생성 책임을 `ShopLegalActionBuilder`로 분리 완료.
- 상점 상품 정규화 책임을 `ShopInventorySnapshotBuilder`로 분리 완료.
- 상점 원시 상품 후보 수집 책임을 `ShopItemCandidateCollector`로 분리 완료.
- 상점 화면, 현재 방, 런타임 인벤토리 탐색 책임을 `ShopRuntimeLocator`로 분리 완료.
- 상점 구매/카드 제거 실행기 1차 구현 완료.
- 상점 실행 직전 `model_id`, 가격, 슬롯 그룹, 슬롯 번호, locator 재검증 보강 완료.

아직 남은 분리:

- 전투 런타임 객체 탐색.
- 플레이어, 손패, 적, 포션, 유물 추출.
- 전투 debug 정보 생성.
- 상점 구매 실행 후 상태 변화 검증과 실제 상점 화면 테스트.

다음 작업은 `CombatSnapshotBuilder`가 단순 조립기를 넘어 전투 snapshot 생성 책임을 더 많이 가져가도록 단계적으로 옮기는 것이다. 단, 기존 JSON 계약이 깨지지 않는지 매 단계마다 빌드와 실제 상태 파일로 확인한다.

## 7. 완료 기준

다음 상황에서 잘못된 행동 후보가 나오지 않아야 한다.

- 전투 중 지도 화면 객체가 열려 있어도 `choose_map_node`가 나오지 않는다.
- 지도 화면이 열려 있어도 `CombatManager.IsInProgress=true`이면 `phase=combat_turn` 또는 `phase=unstable`이다.
- 상점이 아닌 방에서는 `buy_shop_item`이 나오지 않는다.
- 보상 화면이 아닌 상태에서는 보상 claim 행동이 나오지 않는다.
- `phase`와 `legal_actions[].type`의 계열이 항상 일치한다.

검증은 매 단계마다 다음 순서로 한다.

1. 빌드 통과
2. 상태 JSON 확인
3. legal action 계열 확인
4. action submit/claim/result 확인
5. 실제 화면과 상태 JSON 비교
