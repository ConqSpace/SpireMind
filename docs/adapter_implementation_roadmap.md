# LLM 어댑터 구현 로드맵

마지막 갱신: 2026-05-02

이 문서는 `adapter_design_notes.md`의 목표와 현재 SpireMind 구현 사이의 차이를 정리하고, 실제 개발 순서를 제안한다. 목표는 휴리스틱을 똑똑하게 만드는 것이 아니다. 목표는 LLM이 현재 화면을 이해하고, 제공된 합법 행동 중 하나를 선택하며, 실행기가 안전하게 검증 후 조작하는 어댑터를 완성하는 것이다.

## 기준 원칙

모든 화면 어댑터는 같은 흐름을 따른다.

```text
화면 관찰 -> 구조화된 상태 JSON -> 안정 ID가 포함된 legal_actions
-> LLM이 action_id 선택 -> 실행 직전 현재 상태 재검증 -> 실행 결과 기록
```

현재 구현은 이 흐름의 뼈대를 이미 갖고 있다. `CombatActionExecutor`는 `state_id/state_version`을 확인하고, claim된 `selected_action_id`가 현재 `legal_actions`에 다시 존재하는지 확인한다. 브리지 역시 `selected_action_id`와 `state_version`을 기준으로 stale 처리를 한다.

남은 일은 화면별로 관찰 정보, 안정 ID, 행동 후보, 실행 검증의 깊이를 설계 노트 수준까지 끌어올리는 것이다.

## 현재 구현 요약

| 영역 | 현재 상태 | 설계 노트와의 차이 | 개발 판단 |
| --- | --- | --- | --- |
| 전투 카드 | `play_card`, `end_turn` 관찰/실행 가능. 실행 전 손패, 대상, `CanPlayTargeting`, `TryManualPlay` 확인. | `combat_summary`, 적 의도 요약, 카드 효과 요약, LLM 단일 행동 모드가 부족하다. | 기반이 가장 좋다. LLM 판단 정보 보강이 우선이다. |
| 전투 포션 | 설계 문서에는 있으나 현재 실행 지원 없음. | `use_potion` 관찰, legal action, 실행기, 지원 타입이 없다. | 전투 확장 1순위다. |
| 카드 선택 | `choose_card_selection`, `confirm_card_selection` 구현. `min/max/selected_count` 기반 휴리스틱 있음. | 실행 검증이 인덱스 중심이다. 카드 ID 재검증과 선택 종류 의미 정규화가 약하다. | 자주 재사용되므로 안정 ID를 강화해야 한다. |
| 보상 | 골드/유물/포션/카드 보상 claim, 카드 보상 선택/skip, 보상 화면 진행 가능. | 보상 ID가 버튼 인덱스 중심이다. 포션 슬롯 가득 찬 교체 흐름이 없다. 보상 모델 ID 검증이 약하다. | 보상 루프 안정화에 필요하다. |
| 지도 | 즉시 선택 가능한 다음 노드 관찰/실행 가능. `node_id`, `row`, `column` 재검증 있음. | 전체 지도 그래프와 장기 경로 평가는 없다. | 단기 진행은 충분하다. 나중에 경로 계획을 붙인다. |
| 이벤트 | 선택지 관찰과 `choose_event_option` 실행 가능. 잠금 선택지는 제외한다. | `page_index`, fingerprint, known outcome, 위험도, text key 재검증이 부족하다. | LLM 조작을 위해 의미 정규화가 필요하다. |
| 모닥불 | 옵션 관찰, 선택, 진행 구현. | 휴식/강화/기타 의미 정규화와 후속 카드 선택 연결이 약하다. | 카드 선택 어댑터 안정화 후 보강한다. |
| 상점 | 화면 관찰과 `proceed_shop`만 가능. 상품 후보 추출은 시작됨. | `buy_shop_item`, 제거 서비스, 가격/재고/구매 가능 여부, 슬롯 locator, 실행기가 없다. | 현재 사용자가 막힌 영역이다. 우선 개발 대상이다. |
| 보물상자/유물 선택 | 별도 어댑터 없음. | `choose_treasure_relic` 관찰/실행 전체가 없다. | 짧고 명확한 기능이라 중간 단계에 넣는다. |
| 브리지/결정 루프 | `action_id` 제출, claim, stale, result logging 구현. 다중 action plan도 있음. | 설계 노트는 LLM 기본값을 단일 행동으로 잡는다. command 프롬프트는 아직 다중 행동을 열어 둔다. | LLM 모드는 단일 행동 우선으로 정렬해야 한다. |

## 차이점 상세

### 1. 전투 어댑터

현재 구현:

- `phase=combat_turn` 상태를 내보낸다.
- `player`, `piles`, `enemies`, `legal_actions`, `relics`, `debug`를 포함한다.
- 손패 카드는 `instance_id`, `combat_card_id`, `card_id`, `name`, `type`, `cost`, `playable`, `target_type`, `damage`, `block`, `hits`, `description` 등을 가진다.
- 공격 카드는 살아 있는 적마다 `play_card` 후보를 만든다.
- 비공격 카드는 `no_target` 후보를 만든다.
- 항상 `end_turn`을 만든다.

설계와의 차이:

- `combat_summary`가 없다.
- 적 의도와 예상 피해량이 LLM 친화적인 필드로 정리되지 않았다.
- 카드 효과 요약이 피해/방어 중심이다. 드로우, 에너지, 취약, 약화, 힘, 민첩 변화는 부족하다.
- LLM 모드에서 한 번에 여러 행동을 제출할 수 있다.
- 포션 사용이 빠져 있다.

개발 방향:

- 먼저 상태 요약을 추가한다.
- 그다음 `use_potion`을 붙인다.
- 마지막에 LLM command 프롬프트를 단일 행동 기본값으로 바꾼다.

### 2. 상점 어댑터

현재 구현:

- 상점 화면을 감지하고 `phase=shop`을 내보낸다.
- `items` 후보를 탐색한다.
- `proceed_shop` legal action만 생성한다.
- 실행기도 `proceed_shop`만 지원한다.

설계와의 차이:

- 상품 가격, 구매 가능 여부, 재고, 슬롯 locator가 안정적으로 확정되지 않았다.
- `buy_shop_item` 행동이 없다.
- 카드 제거 서비스 행동이 없다.
- 상점 인벤토리 열기/닫기 행동이 없다.
- 실행 직전 가격/재고/골드 재검증이 없다.

개발 방향:

- `MerchantInventory` 기반 관찰을 우선한다.
- 화면 탐색으로 얻은 후보는 보조 정보로 둔다.
- 구매 실행은 `MerchantEntry.OnTryPurchaseWrapper(...)` 계열을 우선 조사한다.
- 첫 구현은 “구매하지 않고 정확히 관찰하기”까지를 완료 조건으로 둔다.

### 3. 이벤트 어댑터

현재 구현:

- 이벤트 화면을 감지한다.
- 이벤트 제목, 설명, 선택지 목록을 내보낸다.
- 잠긴 선택지를 제외하고 `choose_event_option`을 만든다.
- 실행은 현재 버튼 인덱스와 잠금 여부를 확인한 뒤 버튼 release를 호출한다.

설계와의 차이:

- `page_index`와 fingerprint가 없다.
- 선택지의 효과를 `known_outcome`으로 정규화하지 않는다.
- 위험도, 신뢰도, 안전 실행 여부가 없다.
- `text_key`를 실행 직전 강하게 재검증하지 않는다.
- 공유 이벤트 분기가 약하다.

개발 방향:

- 1차는 fingerprint와 `text_key` 재검증을 넣는다.
- 2차는 1막 이벤트부터 known outcome handler를 붙인다.
- 알 수 없는 효과는 LLM에게 긍정적으로 보이지 않게 `has_unknown_effects=true`로 둔다.

### 4. 카드 선택 / 보상 어댑터

현재 구현:

- 카드 선택 화면은 `min_select`, `max_select`, `selected_count`, 카드 후보를 내보낸다.
- 카드 선택과 확인이 가능하다.
- 보상 화면은 골드, 유물, 포션, 카드 보상, 진행 행동을 만든다.
- 카드 보상은 보상 버튼을 누른 뒤 카드 선택 화면에서 예약 인덱스를 선택한다.

설계와의 차이:

- 카드 선택 실행 검증이 인덱스 중심이다.
- 카드 ID와 이름 일치 확인이 충분하지 않다.
- 보상 ID도 버튼 인덱스 중심이다.
- 포션 보상에서 슬롯이 가득 찬 경우의 버리기 흐름이 없다.

개발 방향:

- 카드 선택 ID를 `{selection_kind}_{index}_{card_id}` 형태로 강화한다.
- 실행 직전 같은 인덱스의 카드 ID가 같은지 확인한다.
- 보상도 `reward_type + model_id + index`를 함께 검증한다.
- 포션 교체는 전투 포션 다음 단계로 구현한다.

### 5. 지도 / 모닥불 어댑터

현재 구현:

- 지도는 다음 선택 가능한 노드를 감지하고 선택할 수 있다.
- 모닥불은 옵션 선택과 진행을 할 수 있다.

설계와의 차이:

- 지도는 전체 경로 평가가 없다.
- 모닥불은 옵션 의미가 버튼 텍스트 중심이다.
- 강화 선택처럼 후속 카드 선택 화면이 필요한 흐름과 직접 연결된 계획이 약하다.

개발 방향:

- 지도는 지금 당장 큰 변경보다 장기 경로 정보만 보강한다.
- 모닥불은 카드 선택 어댑터 안정화 후 “강화 -> 카드 선택 -> 확인 -> 진행” 흐름을 검증한다.

### 6. 브리지 / LLM 계약

현재 구현:

- `selected_action_id` 제출이 가능하다.
- `actions` 배열로 다중 행동 계획도 가능하다.
- stale 결과를 기록한다.
- 실행 결과는 `applied`, `stale`, `unsupported`, `failed`, `ignored_duplicate`로 기록한다.

설계와의 차이:

- 설계 노트는 LLM 기본 응답을 action 하나로 제한한다.
- command 프롬프트는 아직 다중 행동을 권장한다.
- `--wait-result`가 선택 사항이라, LLM 평가 루프에서는 결과 확인이 빠질 수 있다.
- 단일 행동 stale 이후 자동 재판단 흐름이 약하다.

개발 방향:

- LLM command 모드 기본 계약을 `selected_action_id` 하나로 바꾼다.
- 다중 행동 계획은 휴리스틱과 테스트 전용으로 남긴다.
- LLM 루프에서는 결과 확인을 기본값으로 둔다.

## 실제 개발 로드맵

### 0단계: 계약 정렬

목표: LLM이 어떤 형식으로 조작해야 하는지 먼저 고정한다.

작업:

- command 모드 프롬프트를 단일 `selected_action_id` 중심으로 바꾼다.
- `actions` 배열은 휴리스틱/테스트용 확장 계약으로 분리한다.
- 결과 대기와 stale 재판단 정책을 문서화한다.
- `LegalActionSnapshot`에 앞으로 필요한 필드를 추가할 위치를 정한다.

완료 기준:

- LLM 프롬프트가 “legal_actions 중 하나만 고르라”고 명확히 말한다.
- 단일 행동 제출, claim, 결과 기록이 한 턴에서 끝까지 추적된다.

리스크:

- 기존 휴리스틱의 다중 행동 테스트 속도가 느려질 수 있다.

대안:

- 휴리스틱 모드는 다중 행동 유지, LLM 모드만 단일 행동으로 분리한다.

### 1단계: 상점 관찰 안정화

목표: 상점 상품을 LLM이 이해할 수 있는 구조로 정확히 내보낸다.

작업:

- `MerchantRoom.Inventory` 또는 동등한 런타임 인벤토리 객체를 우선 관찰한다.
- 카드, 유물, 포션, 제거 서비스를 공통 `shop.items[]` 구조로 정규화한다.
- 각 상품에 `shop_item_id`, `kind`, `model_id`, `name`, `cost`, `is_stocked`, `is_affordable`, `slot_group`, `slot_index`를 넣는다.
- `gold`, `inventory_open`, `card_removal_available`를 추가한다.

완료 기준:

- 현재 상점 화면에서 카드, 유물, 포션, 제거 서비스가 모두 JSON에 보인다.
- 가격이 null 없이 채워진다.
- 구매 행동은 아직 없어도 된다.

리스크:

- UI 그래프 기반 탐색은 화면 구성 변화에 약하다.

대안:

- 런타임 인벤토리 객체를 찾지 못하면 UI 후보를 `debug`로만 노출하고 구매 행동은 만들지 않는다.

### 2단계: 상점 행동 실행

목표: LLM이 상점에서 구매하거나 나갈 수 있게 한다.

작업:

- `buy_shop_item` legal action을 만든다.
- `remove_card_at_shop` legal action을 만든다.
- 실행 직전 같은 슬롯의 `model_id`, 가격, 재고, 골드, 제거 가능 여부를 확인한다.
- `CombatActionBridgeClient` 지원 타입에 상점 행동을 추가한다.
- 실행 결과를 `applied/stale/failed/unsupported`로 기록한다.

완료 기준:

- 살 수 있는 포션 또는 저가 카드 하나를 구매할 수 있다.
- 살 수 없는 상품은 legal action에 없거나 `is_purchase_legal_now=false`로 표시된다.
- 가격 또는 슬롯이 바뀌면 실행하지 않고 stale 처리한다.

리스크:

- 구매 메서드 호출이 게임 내부 부작용을 누락할 수 있다.

대안:

- 첫 실행은 실제 구매 대신 버튼 release 경로를 사용하고, 실패 시 내부 메서드 호출을 별도 분기한다.

### 3단계: 전투 관찰 강화

목표: LLM이 전투 상황을 판단할 수 있게 한다.

작업:

- `combat_summary`를 추가한다.
- 적 의도, 예상 피해량, 반복 횟수를 추출한다.
- 카드별 `estimated_damage`, `estimated_block`, 효과 요약을 보강한다.
- `legal_actions`의 `summary`를 한국어 또는 명확한 짧은 영어로 정리한다.

완료 기준:

- LLM 입력만 보고 “막아야 하는 턴인지”, “죽일 수 있는 적이 있는지”를 판단할 수 있다.
- 기존 `play_card`/`end_turn` 실행은 유지된다.

리스크:

- 피해량 계산을 잘못 추정하면 LLM이 잘못된 판단을 할 수 있다.

대안:

- 확신 없는 값은 `null`과 `unknown_reason`으로 둔다.

### 4단계: 전투 포션

목표: 전투 중 포션 사용을 legal action으로 제공한다.

작업:

- 플레이어 포션 슬롯을 관찰한다.
- `use_potion` legal action을 만든다.
- 대상 필요 포션은 대상별 action을 만든다.
- 실행기는 포션 슬롯, 포션 ID, 대상 생존 여부, 큐잉 여부를 재검증한다.
- 지원 타입에 `use_potion`을 추가한다.

완료 기준:

- 대상 없는 포션과 대상 있는 포션을 각각 한 번씩 실행 검증할 수 있다.
- 포션이 사라졌거나 대상이 죽었으면 실행하지 않는다.

리스크:

- 포션마다 내부 실행 경로가 다를 수 있다.

대안:

- AI Teammate의 `UsePotionAction` 경로를 우선 참고한다.

### 5단계: 이벤트 어댑터 정규화

목표: LLM이 이벤트 선택지를 의미와 위험을 보고 고를 수 있게 한다.

작업:

- 이벤트 fingerprint를 추가한다.
- `page_index`, `event_id`, `text_key`를 실행 직전 재검증한다.
- 공통 선택지 필드에 `adapter_confidence`, `outcome_known_level`, `runtime_warnings`, `known_outcome`을 추가한다.
- 1막에서 확인된 이벤트부터 known outcome handler를 만든다.

완료 기준:

- 알 수 없는 이벤트도 선택지는 노출하되, 알 수 없는 효과가 `known_outcome.has_unknown_effects`와 `runtime_warnings`로 보인다.
- 알려진 이벤트는 체력/골드/카드/유물 변화가 JSON에 보인다.

리스크:

- 이벤트 효과를 잘못 정규화하면 LLM이 손실을 이득으로 판단할 수 있다.

대안:

- 모르는 효과는 항상 `has_unknown_effects=true`로 둔다.

### 6단계: 카드 선택과 보상 안정화

목표: 여러 화면에서 쓰는 카드 N개 선택 기능을 안정화한다.

작업:

- `card_selection_id`에 카드 ID를 더 강하게 포함한다.
- 실행 직전 인덱스와 카드 ID를 함께 확인한다.
- 보상 `reward_id`에 보상 타입과 모델 ID를 보강한다.
- 포션 슬롯이 가득 찼을 때 `discard_potion_for_reward`를 추가한다.

완료 기준:

- 강화, 제거, 보상 선택에서 잘못된 카드가 선택되지 않는다.
- 포션 보상에서 슬롯 가득 참 상황을 멈추지 않고 처리할 수 있다.

리스크:

- 카드 선택 화면마다 내부 카드 홀더 구조가 다를 수 있다.

대안:

- 화면 종류별 adapter를 나누되 외부 JSON 계약은 통일한다.

### 7단계: 보물상자 / 유물 선택

목표: 보물방과 유물 선택 화면을 처리한다.

작업:

- `TreasureRoomRelicSynchronizer` 또는 현재 화면의 유물 후보를 관찰한다.
- `choose_treasure_relic` legal action을 만든다.
- 실행 직전 유물 인덱스와 유물 ID를 확인한다.
- 중복 선택 방지를 넣는다.

완료 기준:

- 보물방에서 유물을 획득하고 다음 화면으로 진행한다.
- 여러 유물 중 하나를 골라야 하는 화면에서 LLM이 선택할 수 있다.

리스크:

- 보물방과 보상 유물 화면의 내부 구조가 다를 수 있다.

대안:

- 보물방 전용 어댑터와 보상 화면 유물 claim을 분리한다.

### 8단계: 지도와 장기 경로

목표: 즉시 다음 노드 선택을 넘어 경로 의도를 반영한다.

작업:

- 전체 지도 그래프를 내보낸다.
- 각 노드의 방 종류, 다음 갈림길, 엘리트/상점/모닥불 접근성을 요약한다.
- LLM은 다음 노드 하나만 고르되, 판단 근거로 장기 경로 정보를 받는다.

완료 기준:

- LLM이 다음 노드 선택에서 몇 층 뒤의 상점/모닥불을 고려할 수 있다.

리스크:

- 장기 경로 정보가 너무 커지면 LLM 입력이 지저분해진다.

대안:

- 전체 그래프와 별도로 `path_options_summary`만 제공한다.

### 9단계: 런 시작 / 이어하기

목표: 반복 테스트를 안정화한다.

작업:

- 저장 런이 있을 때 메인 메뉴의 `계속` 화면을 감지한다.
- 새 시드 런 시작과 이어하기를 명확히 분리한다.
- 테스트 스크립트가 동일 시드, 동일 캐릭터, 동일 모드로 재현 가능하게 만든다.

완료 기준:

- 저장 런이 있어도 새 시드 런 시작이 실패하지 않는다.
- 자동 테스트가 상점, 이벤트, 보물상자, 전투 포션을 반복 재현할 수 있다.

리스크:

- 저장 상태를 건드리는 기능은 되돌리기 어렵다.

대안:

- 초기에는 사용자 확인이 필요한 수동 정리 단계로 남긴다.

## 검증 전략

각 단계는 다음 순서로 검증한다.

1. JSON 상태 파일에 필요한 필드가 보이는지 확인한다.
2. `legal_actions`에 기대한 행동만 생기는지 확인한다.
3. 브리지에 `selected_action_id`를 제출한다.
4. 실행기가 현재 상태를 재검증하는지 확인한다.
5. 성공은 `applied`, 상태 변화는 `stale`, 미지원은 `unsupported`, 실제 실패는 `failed`로 기록되는지 확인한다.
6. 휴리스틱은 같은 JSON과 같은 `legal_actions`만 사용하게 한다.

LLM 연결은 각 단계의 마지막에 붙인다. 먼저 휴리스틱으로 배관을 검증하고, 그다음 LLM이 같은 행동 후보를 고르게 한다.

## 권장 즉시 작업

다음 개발 세션의 첫 목표는 상점이다.

1. 상점 상품 관찰을 런타임 인벤토리 기반으로 보강한다.
2. 가격과 슬롯 ID를 안정화한다.
3. 구매 행동은 만들지 않고, JSON과 `legal_actions` 후보 설계만 먼저 검증한다.
4. 검증 후 `buy_shop_item` 실행기를 붙인다.

이 순서가 좋은 이유는 현재 실제 플레이가 상점에서 막혀 있고, 상점은 LLM 어댑터의 핵심 구조인 “관찰값, 가격, 합법 여부, 실행 전 재검증”을 가장 잘 드러내기 때문이다.
## 2026-05-03 추가 로드맵: 전투 중 카드 선택과 아이언클래드 특수 카드

### 목표

아이언클래드 특수 카드를 임시로 숨기는 방식에서 벗어난다.
원본 게임 흐름처럼 `play_card` 이후에 카드 선택 화면이 열리면, 어댑터가 그 화면을 별도 상태로 노출하고 LLM이 선택을 이어서 수행하게 만든다.

이 작업의 핵심은 휴리스틱 판단이 아니다.
LLM이 판단할 수 있도록 현재 선택 화면의 목적, 후보 카드, 선택 수, 확정 가능 여부를 안정적인 `legal_actions`로 제공하는 것이다.

### 레퍼런스 확인 결과

원본 레퍼런스에서 카드 사용 중 추가 선택은 `CardSelectCmd`가 담당한다.

- `Armaments`: 손패에서 강화할 카드 1장 선택
- `Brand`: 손패에서 소멸할 카드 1장 선택
- `BurningPact`: 손패에서 소멸할 카드 1장 선택
- `Headbutt`: 버린 카드 더미에서 뽑을 카드 더미 맨 위로 올릴 카드 1장 선택
- `TrueGrit+`: 강화된 경우 손패에서 소멸할 카드 1장 선택
- `TrueGrit`: 미강화 상태에서는 손패 카드 1장을 무작위로 소멸하므로 선택 화면이 열리지 않는다.

원본 흐름은 다음과 같다.

```text
카드 사용
-> 카드의 즉시 효과 적용
-> CardSelectCmd가 PlayerChoice 시작
-> 선택 화면 표시
-> 선택 결과 동기화
-> 카드의 남은 효과 적용
-> 전투 상태로 복귀
```

따라서 어댑터도 같은 흐름을 따라야 한다.

```text
combat_turn의 play_card 선택
-> 게임이 card_selection 화면 표시
-> phase=card_selection 상태 export
-> choose_card_selection / confirm_card_selection 제공
-> LLM 선택
-> 게임이 카드 효과 마무리
-> 다음 combat_turn 상태 export
```

### 현재 문제

`Headbutt`은 현재 `legal_actions`에서 임시 제외되어 있다.
이 처리는 실패를 숨길 뿐, 레퍼런스 흐름을 구현한 것은 아니다.

더 큰 문제는 export 우선순위다.
전투 중에는 `TryRefreshCombatStateFromCombatManager()`가 먼저 성공할 수 있다.
그러면 실제 화면에는 `NSimpleCardSelectScreen`이 떠 있어도 어댑터 상태는 계속 `combat_turn`으로 남을 수 있다.

LLM 입장에서는 카드 선택 화면이 보이지 않는다.
결과적으로 다음 행동을 고를 수 없고, 실행기는 “카드는 사용한 것처럼 보이지만 상태 변화가 없다”는 실패를 기록한다.

### 1단계: 카드 선택 화면 export 우선순위 수정

목표:

- 전투 중이라도 카드 선택 화면이 실제로 보이면 `card_selection`을 먼저 export한다.

작업:

- `AutotestMainThreadTicker`의 export 순서를 조정한다.
- `TryExportCardSelectionStateIfVisible()`를 전투 상태 export보다 먼저 호출한다.
- 단, 게임 오버 화면은 계속 최우선으로 둔다.
- 전투 중 일반 보상, 이벤트, 지도 화면이 잘못 끼어드는 기존 방어 로직은 유지한다.

완료 기준:

- `Headbutt` 사용 후 상태 JSON의 `phase`가 `card_selection`으로 바뀐다.
- `legal_actions`에 `choose_card_selection`이 나타난다.
- 선택 후 전투 상태로 자연스럽게 돌아온다.

리스크:

- 전투 위에 다른 오버레이가 뜬 경우 카드 선택과 혼동할 수 있다.

대응:

- 카드 선택 화면 타입을 `NSimpleCardSelectScreen`, `NDeckCardSelectScreen`, `NDeckUpgradeSelectScreen` 등으로 제한한다.

### 2단계: 카드 선택 상태의 의미 정보 보강

목표:

- LLM이 “무엇을 위해 고르는지” 알 수 있게 한다.

작업:

- `card_selection` 상태에 다음 정보를 추가한다.
  - `selection_kind`
  - `selection_purpose`
  - `source_card_id`
  - `source_card_name`
  - `source_pile`
  - `min_select`
  - `max_select`
  - `selected_count`
  - `can_confirm`
- 각 후보 카드에 다음 정보를 안정적으로 넣는다.
  - `card_selection_id`
  - `card_selection_index`
  - `card_id`
  - `name`
  - `type`
  - `cost`
  - `upgraded`
  - `description`
  - `pile`

완료 기준:

- Headbutt 선택 화면에서 `source_card_name=Headbutt`, `source_pile=discard_pile`에 해당하는 정보가 보인다.
- Burning Pact / Brand 선택 화면에서 `selection_purpose=exhaust_from_hand`에 해당하는 정보가 보인다.
- Armaments 선택 화면에서 `selection_purpose=upgrade_from_hand`에 해당하는 정보가 보인다.

리스크:

- 원본 화면 객체에서 원인 카드 정보를 직접 찾기 어려울 수 있다.

대응:

- 처음에는 화면 타입과 후보 카드 더미로 목적을 추론한다.
- 이후 `PlayerChoiceContext.LastInvolvedModel` 또는 선택 화면 내부 prompt/source 필드를 찾아 정확도를 높인다.

### 3단계: 선택 실행 검증 강화

목표:

- 카드 선택 액션이 “눌린 것처럼 보이는 상태”에서 멈추지 않게 한다.

작업:

- `choose_card_selection` 실행 직전 현재 화면의 카드 ID와 index를 다시 검증한다.
- 선택 후 `selected_count` 증가 또는 화면 종료를 관찰한다.
- `confirm_card_selection` 실행 후 화면 종료 또는 전투 상태 복귀를 관찰한다.
- 결과를 `applied`, `failed`, `stale`, `terminal_transition`으로 명확히 기록한다.

완료 기준:

- 잘못된 index 선택은 실행 전에 `stale` 또는 `failed`로 막힌다.
- 올바른 선택은 `applied`가 되고 다음 상태가 기록된다.

리스크:

- `NSimpleCardSelectScreen`은 선택 즉시 닫히는 경우와 확정 버튼이 필요한 경우가 섞일 수 있다.

대응:

- `min_select`, `max_select`, `selected_count`, confirm 버튼 활성 상태를 함께 본다.

### 4단계: Headbutt 임시 제외 제거

목표:

- `Headbutt`을 다시 정상 `play_card` 후보로 노출한다.

작업:

- `CombatLegalActionBuilder.RequiresUnsupportedAdapterFlow()`에서 `CARD.HEADBUTT` 제외 조건을 제거한다.
- Headbutt 사용 후 이어지는 `card_selection`을 LLM이 처리하게 한다.

완료 기준:

- 손패에 Headbutt이 있고 에너지와 대상 조건이 맞으면 `play_card` legal action에 나타난다.
- 사용 후 버린 카드 더미 선택 화면이 legal action으로 노출된다.
- 선택한 카드가 뽑을 카드 더미 맨 위로 이동한다.

리스크:

- 버린 카드 더미가 비어 있으면 선택 화면 없이 카드 효과가 끝날 수 있다.

대응:

- 이 경우는 실패가 아니다. 상태 변화가 전투 복귀로 이어졌는지 확인한다.

### 5단계: 아이언클래드 특수 카드 실전 검증

목표:

- 실제 런에서 특수 카드 흐름이 어댑터 계약을 깨지 않는지 확인한다.

검증 카드:

- Headbutt
- Armaments
- BurningPact
- Brand
- TrueGrit+

검증 방법:

- 각 카드가 손패에 있을 때 `play_card`를 실행한다.
- 선택 화면이 열리는 카드에서는 `card_selection` 상태가 export되는지 확인한다.
- `choose_card_selection`과 `confirm_card_selection`이 정상 적용되는지 확인한다.
- 전투가 끝나거나 사망하면 `terminal_transition`으로 처리되는지 확인한다.

완료 기준:

- 특수 카드 때문에 `action_failed`가 발생하지 않는다.
- 휴리스틱 판단 실패와 어댑터 실행 실패가 로그에서 분리된다.
- LLM은 클릭 없이 legal action만으로 카드 선택을 마칠 수 있다.

### 6단계: 일반화

목표:

- 아이언클래드 외 카드도 같은 카드 선택 흐름을 재사용하게 만든다.

작업:

- 전체 카드 모델에서 `CardSelectCmd.*` 호출 카드를 목록화한다.
- 선택 원천을 다음 범주로 정규화한다.
  - `hand`
  - `discard_pile`
  - `draw_pile`
  - `deck`
  - `generated_choices`
- 선택 목적을 다음 범주로 정규화한다.
  - `discard_from_hand`
  - `exhaust_from_hand`
  - `upgrade_from_hand`
  - `move_from_discard_to_draw_top`
  - `transform`
  - `choose_generated_card`
  - `put_back_on_draw_top`

완료 기준:

- 새 카드가 추가되어도 선택 화면 타입이 같으면 어댑터가 별도 예외 없이 처리한다.
- 카드별 하드코딩은 설명 보강용으로만 사용하고, 실행은 공통 `card_selection` 액션으로 처리한다.

### 구현 우선순위

1. export 우선순위 수정
2. Headbutt 실전 재현
3. 카드 선택 의미 정보 보강
4. Headbutt 필터 제거
5. Armaments / BurningPact / Brand / TrueGrit+ 검증
6. 전체 `CardSelectCmd` 카드 목록화와 일반화

다음 작업은 1단계부터 시작한다.
