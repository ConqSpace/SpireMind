# LLM 조작 어댑터 설계 노트

마지막 갱신: 2026-05-02

이 문서는 `sts2AITeammate` 소스 조사를 바탕으로, SpireMind가 LLM 조작 어댑터를 구현할 때 따라야 할 구체 설계 지침을 정리한다.

## 공통 패턴

AI Teammate에서 반복되는 안전한 흐름은 다음과 같다.

```text
런타임 객체 관측
-> 중립 상태 객체로 정규화
-> 안정 ID와 fingerprint 생성
-> 행동 후보 생성
-> 판단기 선택
-> 실행 직전 재관측
-> ID, 인덱스, 비용, 잠금 상태, 대상 생존 여부 재검증
-> 내부 메서드 또는 명령 실행
-> 큐 안정화 또는 화면 전환 대기
-> 실패하면 안전 행동 또는 무실행
```

SpireMind에서는 런타임 객체를 JSON에 직접 싣지 않는다. JSON에는 LLM이 판단할 수 있는 구조화된 값과 안정 ID만 내보낸다. 실제 `Player`, `MerchantEntry`, `EventOption`, `CardModel` 같은 객체는 executor가 실행 직전에 다시 찾아야 한다.

## 공통 어댑터 규칙

- ID는 인덱스만 쓰지 않는다. `{종류}_{인덱스}_{게임 내부 ID}` 형태를 기본으로 한다.
- LLM 응답은 `action_id` 하나만 선택하게 한다.
- executor는 `action_id`를 신뢰하지 않고 현재 상태에서 후보를 다시 만든 뒤 일치 여부를 확인한다.
- 실행 전 재검증 실패는 `failed`보다 `stale` 또는 안전 중단으로 처리한다.
- 알 수 없는 효과는 긍정적으로 해석하지 않는다.
- 휴리스틱은 같은 JSON과 같은 `legal_actions`만 사용한다. 별도 내부 지름길을 만들지 않는다.

## 상점 어댑터

### 관측 소스

AI Teammate 기준:

- `Player`
- `MerchantRoom`
- `MerchantInventory`
- `MerchantCardEntry`
- `MerchantRelicEntry`
- `MerchantPotionEntry`
- `MerchantCardRemovalEntry`
- UI 상태: `NRun.Instance.MerchantRoom.Inventory.IsOpen`

SpireMind 적용:

- UI 노드 탐색보다 `MerchantRoom.Inventory` 또는 대응 인벤토리 객체를 우선한다.
- 화면에 보이는 카드, 유물, 포션, 제거 서비스를 모두 같은 구조로 정규화한다.

### 안정 ID

권장 형식:

```text
character_card_{index}_{card_id}
colorless_card_{index}_{card_id}
relic_{index}_{relic_id}
potion_{index}_{potion_id}
card_removal_service
```

실행 위치 정보는 JSON에 다음처럼 둔다.

```json
{
  "locator_id": "relic:0",
  "slot_group": "relic",
  "slot_index": 0
}
```

런타임 `Entry` 객체는 JSON에 넣지 않는다. executor가 현재 인벤토리에서 다시 찾는다.

### JSON 필드

```json
{
  "phase": "shop",
  "shop": {
    "room_visit_key": "...",
    "snapshot_fingerprint": "...",
    "gold": 102,
    "inventory_open": true,
    "max_potion_slots": 3,
    "filled_potion_slots": 1,
    "has_open_potion_slots": true,
    "card_removal_available": true,
    "items": [
      {
        "shop_item_id": "relic_0_RELIC.MEAL_TICKET",
        "kind": "relic",
        "name": "...",
        "model_id": "RELIC.MEAL_TICKET",
        "cost": 159,
        "is_stocked": true,
        "is_affordable": false,
        "is_purchase_legal_now": false,
        "rarity": "Uncommon",
        "is_on_sale": false,
        "locator_id": "relic:0",
        "slot_group": "relic",
        "slot_index": 0
      }
    ]
  }
}
```

### 행동 계약

- `open_shop_inventory`
- `buy_shop_item`
- `remove_card_at_shop`
- `close_shop_inventory`
- `proceed_shop`

`buy_shop_item`에는 다음 필드가 필요하다.

```json
{
  "type": "buy_shop_item",
  "shop_item_id": "potion_0_POTION.ATTACK_POTION",
  "slot_group": "potion",
  "slot_index": 0,
  "model_id": "POTION.ATTACK_POTION",
  "cost": 50
}
```

### 실행 메서드

AI Teammate 참고:

- 구매: `MerchantEntry.OnTryPurchaseWrapper(...)`
- 카드 제거: 골드 차감, 덱 카드 제거, 제거 서비스 사용 처리, 구매 훅 호출
- 나가기: `ProceedButton` 또는 방 진행 메서드

SpireMind 적용:

- 구매는 `MerchantEntry`를 현재 인벤토리에서 재탐색해 실행한다.
- 카드 제거는 중복 카드 구분을 위해 `deck_index` 또는 카드 인스턴스 ID가 필요하다.

### 실행 전 재검증

- 현재 방이 상점인지 확인한다.
- `room_visit_key` 또는 fingerprint가 크게 달라졌는지 확인한다.
- 상품 인덱스와 `model_id`가 일치하는지 확인한다.
- 가격이 LLM 관측 시점과 일치하는지 확인한다.
- `is_stocked`, `EnoughGold`, 포션 슬롯, 제거 가능 여부를 다시 확인한다.

### 실패 처리

- 인벤토리가 열려 있으면 닫는다.
- 닫혀 있고 나갈 수 있으면 `proceed_shop`.
- 구매 검증 실패는 구매하지 않는다.

## 이벤트 어댑터

### 관측 소스

AI Teammate 기준:

- `RunManager.Instance.EventSynchronizer`
- `synchronizer.GetEventForPlayer(player)`
- `EventModel`
- `EventModel.CurrentOptions`
- 내부 페이지: `EventSynchronizer._pageIndex`

SpireMind 적용:

- UI 버튼 텍스트보다 `EventModel.CurrentOptions`를 우선한다.
- 이벤트 화면을 LLM이 읽기 쉬운 `EventVisitState` 형태로 정규화한다.

### fingerprint / 안정 ID

권장 fingerprint 구성:

```text
event_id
is_finished
page_index
option text_key + is_locked + is_proceed 목록
```

선택 액션 ID:

```text
event_option_{index}_{sanitized_fingerprint}
```

개별 선택지에는 다음 위치자를 둔다.

```json
{
  "locator_id": "event_option:0:EVENT.X.options.ACCEPT",
  "event_option_index": 0,
  "text_key": "EVENT.X.options.ACCEPT"
}
```

### JSON 필드

```json
{
  "phase": "event",
  "event": {
    "event_id": "...",
    "event_type_name": "...",
    "title": "...",
    "description": "...",
    "page_index": 0,
    "is_shared": false,
    "is_finished": false,
    "options": [
      {
        "event_option_index": 0,
        "text_key": "...",
        "title": "...",
        "description": "...",
        "is_locked": false,
        "is_proceed": false,
        "is_likely_leave_or_exit": false,
        "will_kill_player": false,
        "support_level": "special_high_confidence",
        "trust_level": "high",
        "safe_for_llm_execution": true,
        "known_outcome": {
          "hp_delta": -11,
          "max_hp_delta": 0,
          "gold_delta": 0,
          "remove_count": 1,
          "upgrade_count": 0,
          "transform_count": 0,
          "relic_ids": [],
          "potion_ids": [],
          "fixed_card_ids": [],
          "curse_card_ids": [],
          "starts_combat": false,
          "has_randomness": false,
          "has_unknown_effects": false
        },
        "risk_notes": [],
        "summary_for_llm": "체력 11을 잃고 카드 1장을 제거합니다."
      }
    ]
  }
}
```

### 정규화 구조

공통 관측기는 기본 정보만 채운다.

- `TextKey`
- 제목
- 설명
- 잠김 여부
- 진행 여부
- 즉사 위험
- 붙어 있는 유물
- hover tip 종류

효과를 모르면:

```json
{
  "support_level": "generic_partial",
  "trust_level": "low",
  "known_outcome": {
    "has_unknown_effects": true
  }
}
```

이벤트별 해석기는 `known_outcome`을 채우는 역할만 한다. 자동 선택기가 아니라 “LLM용 결과 번역기”다.

### 행동 계약

```json
{
  "type": "choose_event_option",
  "event_id": "...",
  "page_index": 0,
  "event_option_index": 0,
  "text_key": "..."
}
```

### 실행 메서드

AI Teammate 참고:

- 일반 이벤트: `EventSynchronizer.ChooseOptionForEvent(player, optionIndex)`
- 공유 이벤트: `EventSynchronizer.PlayerVotedForSharedOptionIndex(player, optionIndex, pageIndex)`

SpireMind 적용:

- 가능하면 UI 클릭 대신 synchronizer 메서드를 사용한다.
- 공유 이벤트는 일반 선택과 별도 action type 또는 별도 실행 분기로 둔다.

### 실행 전 재검증

- 현재 방이 이벤트 방인지 확인한다.
- 현재 이벤트 ID가 일치하는지 확인한다.
- 페이지 인덱스가 일치하는지 확인한다.
- fingerprint 또는 선택지 목록이 일치하는지 확인한다.
- 선택지 인덱스가 범위 안인지 확인한다.
- 선택지가 잠겨 있지 않은지 확인한다.
- `TextKey`가 일치하는지 확인한다.
- 즉사 선택지는 LLM이 골라도 실행하지 않는 안전장치를 둘 수 있다.

### 실패 처리

- 선택지가 없으면 아무 것도 하지 않는다.
- LLM이 고른 선택지가 stale이면 실행하지 않는다.
- 휴리스틱 검증 모드에서는 첫 번째 잠기지 않은 진행/나가기 선택지를 고른다.

## 보물상자 / 유물 선택 어댑터

### 관측 소스

- `TreasureRoomRelicSynchronizer.CurrentRelics`
- `TreasureRoomRelicSynchronizer.GetPlayerVote(player)`
- 일반 유물 선택 화면: `RelicSelectCmd.FromChooseARelicScreen(player, relics)`

### 안정 ID

```text
treasure_relic_{index}_{relic_id}
```

index와 relic ID를 함께 둔다. 같은 유물이 중복될 수 있으므로 ID만 쓰지 않는다.

### JSON 필드

```json
{
  "phase": "treasure",
  "treasure": {
    "is_relic_picking": true,
    "vote_received": false,
    "relics": [
      {
        "treasure_relic_id": "treasure_relic_0_RELIC.X",
        "treasure_relic_index": 0,
        "relic_id": "RELIC.X",
        "name": "...",
        "description": "...",
        "rarity": "Rare"
      }
    ]
  }
}
```

### 행동 계약

- `open_treasure_chest`
- `choose_treasure_relic`
- `proceed_treasure`

`choose_treasure_relic`:

```json
{
  "type": "choose_treasure_relic",
  "treasure_relic_index": 0,
  "relic_id": "RELIC.X"
}
```

### 실행 메서드

- 보물방 유물 선택: `TreasureRoomRelicSynchronizer.OnPicked(player, relicIndex)`
- 일반 유물 선택 화면은 해당 선택 명령의 반환값 또는 선택 콜백을 조사해 연결한다.

### 실행 전 재검증

- 아직 보물방인지 확인한다.
- 이미 투표했는지 확인한다.
- `CurrentRelics`가 비어 있지 않은지 확인한다.
- 인덱스가 범위 안인지 확인한다.
- 인덱스 위치의 `relic.Id.Entry`가 일치하는지 확인한다.

### 실패 처리

- 선택 목록이 없으면 실행하지 않는다.
- LLM 선택이 stale이면 첫 번째 유물 fallback은 휴리스틱 모드에서만 허용한다.

## 전투 포션 어댑터

### 관측 소스

- `player.Potions`
- `PotionModel`
- `PotionModel.TargetType`
- `PotionModel.IsQueued`
- `CombatManager.Instance.IsInProgress`
- 적/플레이어 대상 목록

### 안정 ID

AI Teammate는 다음 형태를 사용한다.

```text
use_potion_{potion_id}_{potion_index}_target_{target_id}
```

SpireMind에서도 index와 ID를 함께 둔다. 포션 인스턴스 ID가 있다면 추가한다.

### JSON 필드

```json
{
  "potions": [
    {
      "potion_slot_index": 0,
      "potion_id": "POTION.ATTACK_POTION",
      "name": "...",
      "description": "...",
      "rarity": "Common",
      "target_type": "Enemy",
      "is_queued": false,
      "is_usable_now": true
    }
  ]
}
```

### 행동 계약

```json
{
  "type": "use_potion",
  "potion_slot_index": 0,
  "potion_id": "POTION.ATTACK_POTION",
  "target_id": "enemy_0"
}
```

### 실행 메서드

AI Teammate 참고:

- `BeforeUse` 호출
- `new UsePotionAction(potion, target, CombatManager.Instance.IsInProgress)`
- `ActionQueueSynchronizer.RequestEnqueue(usePotionAction)`
- `IsQueued` 필드 설정

### 실행 전 재검증

- 같은 슬롯에 같은 포션이 있는지 확인한다.
- 포션이 이미 queued 상태인지 확인한다.
- 대상이 필요한 포션이면 대상이 아직 살아 있는지 확인한다.
- 대상이 필요 없는 포션이면 `target_id`가 없어도 실행 가능해야 한다.

### 실패 처리

- 포션이 바뀌었거나 대상이 사라졌으면 실행하지 않는다.
- 휴리스틱 모드에서는 명확한 방어/회복/딜 포션만 사용한다.

## 포션 보상 / 교체 어댑터

### 관측 소스

- `PotionReward.Potion`
- `player.Potions`
- `player.HasOpenPotionSlots`

### 행동 계약

- `claim_potion_reward`
- `skip_potion_reward`
- `discard_potion_for_reward`

### 실행 메서드

AI Teammate 참고:

- 새 포션을 받을 수 있으면 `potionReward.OnSelectWrapper()`
- 교체가 필요하면 `PotionCmd.Discard(currentPotion)` 후 `potionReward.OnSelectWrapper()`

### 실행 전 재검증

- 새 포션 보상이 아직 남아 있는지 확인한다.
- 버릴 포션 슬롯의 ID가 일치하는지 확인한다.
- 빈 슬롯 여부를 다시 확인한다.

## 카드 선택 / 보상 어댑터

### 관측 소스

AI Teammate 기준:

- 카드 보상: `CardReward`의 private `_cards`
- 선택 명령: `CardSelectCmd` 계열 인자
- 덱 선택: `PileType.Deck.GetPile(player).Cards`
- 손패 선택: `PileType.Hand.GetPile(player).Cards`
- bundle 선택: `bundles`

### 안정 ID

권장 형식:

```text
candidate_{index}_{card_id}
deck_{index}_{card_id}
reward_card_{index}_{card_id}
```

가능하면 카드 인스턴스 ID를 추가한다.

### JSON 필드

```json
{
  "phase": "card_selection",
  "card_selection": {
    "selection_kind": "upgrade",
    "min_select": 1,
    "max_select": 1,
    "selected_count": 0,
    "can_skip": false,
    "cards": [
      {
        "card_selection_id": "deck_0_CARD.STRIKE",
        "card_selection_index": 0,
        "card_id": "CARD.STRIKE",
        "name": "...",
        "type": "Attack",
        "rarity": "Basic",
        "cost": 1,
        "upgraded": false,
        "description": "..."
      }
    ]
  }
}
```

### 행동 계약

- `choose_card_selection`
- `confirm_card_selection`
- `choose_card_reward`
- `skip_card_reward`

### 실행 메서드

SpireMind 현재 구조는 UI 선택 화면에서 카드 클릭/확인을 수행한다.

AI Teammate 참고:

- 보상 카드는 `CardPileCmd.Add(selected, PileType.Deck)` 후 보상 기록을 직접 갱신한다.
- 일반 선택 화면은 명령 패치가 `CardModel` 또는 `IEnumerable<CardModel>`을 반환한다.

현재 SpireMind 구조에서는 UI 기반 선택을 유지하되, 실행 직전 후보 목록 재생성과 ID 재검증을 강화하는 편이 안전하다.

### 실행 전 재검증

- 선택 화면 종류가 일치하는지 확인한다.
- `min_select`, `max_select`가 현재도 같은지 확인한다.
- 인덱스와 카드 ID가 일치하는지 확인한다.
- 이미 선택된 카드인지 확인한다.
- `max_select`를 넘기지 않는다.
- `min_select` 미만이면 확인하지 않는다.

### 실패 처리

- skip 가능하면 skip.
- skip 불가능하면 휴리스틱 모드에서 최소 선택 수만큼 앞 후보를 선택.
- LLM 모드에서는 stale이면 재판단을 요청한다.

## 구현 우선순위

1. 상점: `MerchantInventory` 기반 관측과 상품 안정 ID.
2. 이벤트: `EventSynchronizer` 기반 관측, `text_key` 포함 선택 재검증.
3. 보물상자: `TreasureRoomRelicSynchronizer` 기반 유물 후보와 선택 실행.
4. 전투 포션: `use_potion` 관측과 실행.
5. 포션 교체: 슬롯 가득 찬 보상 처리.
6. 카드 선택: 실행 직전 후보 재생성과 ID 재검증 강화.
## 전투 어댑터

전투는 자동화 빈도가 가장 높기 때문에, 휴리스틱을 강하게 만드는 것보다 LLM이 안전하게 조작할 수 있는 입력판을 먼저 만드는 것이 중요하다. 현재 SpireMind 전투 구조는 이미 `legal_actions`를 만들고, 실행 직전에 `combat_card_id`, 손패 존재 여부, 대상, `CanPlayTargeting`, `TryManualPlay`를 다시 확인한다. 따라서 기본 실행 흐름은 올바른 방향이다.

부족한 부분은 LLM이 전투 상황을 판단할 수 있는 관찰 정보와 행동 후보의 설명이다.

### 관찰 목표

LLM이 매 턴 판단하려면 다음 정보가 구조화되어야 한다.

- 플레이어 체력, 최대 체력, 방어도, 에너지.
- 플레이어 주요 상태: 힘, 민첩, 취약, 약화, 손상, 인공물, 가시, 무형 등.
- 적 목록: 안정 ID, 이름, 체력, 방어도, 생존 여부.
- 적 의도: 공격, 방어, 강화, 약화, 디버프, 도주, 알 수 없음.
- 적 예상 피해량과 반복 횟수.
- 손패 카드: 안정 ID, `combat_card_id`, 카드 ID, 이름, 비용, 타입, 대상 필요 여부, 설명.
- 카드 효과 추정치: 피해량, 방어도, 드로우, 에너지 생성, 취약/약화 부여 등 가능한 범위의 요약.
- 사용할 수 있는 포션: 포션 ID, 이름, 대상 타입, 현재 사용 가능 여부.

이 정보는 내부 객체를 그대로 노출하지 않고, JSON에 안전한 값과 안정 ID만 제공한다.

### LLM용 전투 요약

원본 JSON만으로는 LLM 판단 비용이 커진다. 별도 요약 필드를 제공한다.

```json
{
  "combat_summary": {
    "incoming_damage": 12,
    "player_block": 3,
    "missing_block": 9,
    "can_kill_enemy_ids": ["enemy_0"],
    "low_hp_warning": false,
    "recommended_attention": [
      "이번 턴 방어도가 9 부족합니다.",
      "enemy_0은 타격 1장으로 처치할 수 있습니다."
    ]
  }
}
```

요약은 결정을 대신하지 않는다. LLM이 전투 상태를 빠르게 이해하도록 돕는 보조 정보다.

### 행동 계약

전투 행동은 `legal_actions` 중심으로 제공한다.

```json
{
  "action_id": "play_card_12_target_enemy_0",
  "type": "play_card",
  "combat_card_id": 12,
  "card_id": "CARD.STRIKE",
  "card_name": "타격",
  "target_id": "enemy_0",
  "target_combat_id": 31,
  "energy_cost": 1,
  "is_currently_playable": true,
  "summary": "enemy_0에게 타격을 사용합니다.",
  "validation_note": "카드가 손패에 있고 대상이 살아 있으며 CanPlayTargeting이 true여야 합니다."
}
```

대상이 필요 없는 카드는 `target_id`를 비워 둔다. 대상이 여러 명이면 대상별로 별도 행동 후보를 만든다. 이렇게 해야 LLM은 좌표나 손패 순서를 추측하지 않고 명확한 행동 하나를 선택할 수 있다.

필수 행동:

- `play_card`
- `end_turn`

확장 행동:

- `use_potion`
- `choose_combat_card_selection`
- `choose_combat_discard`

### 한 번에 한 행동 원칙

LLM 모드에서는 긴 행동 묶음을 한 번에 실행하지 않는 편이 안전하다.

전투에서는 첫 번째 카드 사용 후 에너지, 손패, 적 체력, 적 생존 여부가 즉시 바뀐다. 따라서 LLM이 여러 행동을 한 번에 제출하면 두 번째 행동이 낡은 상태를 기준으로 할 수 있다.

권장 흐름:

```text
관찰 -> LLM이 행동 1개 선택 -> 실행 직전 재검증 -> 실행 -> 화면 안정화 대기 -> 재관찰
```

휴리스틱 모드에서는 빠른 검증을 위해 여러 행동 묶음을 유지할 수 있다. 단, LLM 모드의 기본 계약은 단일 행동이어야 한다.

### 포션 행동

포션은 전투 어댑터의 첫 확장 지점으로 적합하다.

관찰 필드:

- `potion_id`
- `potion_index`
- `name`
- `target_type`
- `requires_target`
- `is_usable_now`
- `summary`

행동 예시:

```json
{
  "action_id": "use_potion_0_POTION.FIRE_target_enemy_0",
  "type": "use_potion",
  "potion_index": 0,
  "potion_id": "POTION.FIRE",
  "target_id": "enemy_0",
  "target_combat_id": 31,
  "summary": "화염 포션을 enemy_0에게 사용합니다."
}
```

실행 전 검증:

- 같은 슬롯에 같은 포션이 남아 있는지 확인한다.
- 포션이 이미 대기열에 들어가지 않았는지 확인한다.
- 대상이 필요하면 대상이 살아 있는지 확인한다.
- 전투 중 사용 가능한 포션인지 확인한다.

### 실행 전 재검증

`play_card`는 현재 구조를 유지하되, 다음 검증을 명시적인 실패 사유로 남긴다.

- `state_id`, `state_version`이 제출 시점과 맞는지 확인한다.
- `action_id`가 현재 `legal_actions`에 다시 생성되는지 확인한다.
- `combat_card_id`가 아직 손패에 있는지 확인한다.
- 카드 ID와 비용이 관찰 시점과 크게 어긋나지 않는지 확인한다.
- 대상이 필요한 카드라면 대상이 현재 전투에 존재하고 살아 있는지 확인한다.
- `CanPlayTargeting`이 false이면 실행하지 않는다.
- `TryManualPlay`가 false이면 실행 실패로 기록한다.

### 실패 처리

- 상태가 바뀌었으면 `stale`로 처리하고 다시 판단을 요청한다.
- 카드가 손패에서 사라졌으면 실행하지 않는다.
- 대상이 죽었으면 실행하지 않는다.
- 쓸 수 있는 카드나 포션이 없으면 `end_turn`만 남긴다.
- 알 수 없는 카드 효과는 긍정적으로 추정하지 않는다.

### 구현 순서

1. 전투 JSON에 `combat_summary`를 추가한다.
2. 카드별 `summary`, `energy_cost`, `requires_target`, `estimated_damage`, `estimated_block`을 보강한다.
3. 적 의도와 예상 피해량을 안정적으로 추출한다.
4. `legal_actions`를 대상별 `action_id`로 명확히 만든다.
5. LLM 모드에서는 단일 행동 제출을 기본값으로 만든다.
6. `use_potion` 관찰과 실행기를 추가한다.
7. 실패 결과를 `stale`, `invalid`, `unsupported`, `applied`로 명확히 기록한다.

이 순서로 진행하면 전투 휴리스틱은 얇게 유지하면서도, LLM이 실제 전투를 조작할 수 있는 안전한 어댑터를 만들 수 있다.
