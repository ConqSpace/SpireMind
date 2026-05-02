# AI Teammate 참고 LLM 조작 어댑터 로드맵

마지막 갱신: 2026-05-02

이 문서는 `sts2AITeammate` 모드가 처리하는 흐름을 참고해, SpireMind가 LLM이 조작할 수 있는 게임 어댑터가 되기 위한 구현 순서를 정리한다.

## 목표

최종 목표는 휴리스틱 자동 플레이어가 아니다. 목표는 LLM이 현재 게임 상황을 이해하고, 가능한 행동 중 하나를 선택하고, 그 선택이 실제 게임에 안전하게 적용되는 어댑터를 만드는 것이다.

현재 구조는 유지한다.

```text
게임 상태 추출
-> LLM이 이해할 수 있는 관측 JSON 생성
-> legal_actions 생성
-> 브리지에 상태 게시
-> LLM 또는 임시 휴리스틱이 action_id 선택
-> 게임 모드가 행동 claim
-> 메인 스레드에서 재검증 후 실행
-> 결과 보고
```

AI Teammate 코드를 그대로 옮기지 않는다. 그 모드는 내부 플레이어와 동기화 객체를 직접 다루는 구조다. 우리는 같은 기능을 `관측 -> 행동 계약 -> 재검증 -> 실행` 흐름에 맞춰 얇게 추가한다.

휴리스틱은 최종 판단기가 아니다. 휴리스틱은 LLM을 붙이기 전에 exporter, legal action, executor, 화면 전환이 제대로 작동하는지 확인하는 검증용 대체 판단기다.

## 공통 구현 원칙

각 단계는 같은 순서로 구현한다.

1. 화면 또는 게임 상태를 안정적으로 식별한다.
2. LLM이 판단할 수 있는 구조화된 관측 필드를 만든다.
3. 현재 상태에서 가능한 `legal_actions`를 만든다.
4. executor가 실행 직전에 상태 ID, 화면 종류, 대상 ID를 재검증한다.
5. 실행 결과와 다음 상태 변화를 로그에 남긴다.
6. 휴리스틱은 같은 JSON과 같은 `legal_actions`만 사용해 최소 흐름을 검증한다.

LLM용 관측에는 다음 정보가 함께 있어야 한다.

- 화면의 실제 의미
- 선택 가능한 대상 목록
- 각 대상의 중요한 수치와 설명
- 위험 요소
- 알 수 없는 효과
- 행동 실행 후 예상되는 다음 화면

## 1단계: 행동 claim 지원 목록 정합성

목표: exporter와 executor가 만든 행동을 bridge claim 단계에서 거절하지 않게 한다.

LLM 어댑터 관점:

- LLM이 선택한 `action_id`가 executor 지원 목록과 일치해야 한다.
- 지원하지 않는 행동은 애초에 `legal_actions`에 나오지 않아야 한다.

작업:

- `supported_action_types`를 exporter/executor와 맞춘다.
- 새 action type을 추가할 때 문서, bridge, executor 지원 목록을 함께 갱신한다.

휴리스틱 검증:

- 각 phase에서 첫 번째 안전 행동을 제출해 claim과 result가 왕복되는지 확인한다.

완료 기준:

- `legal_actions`에 있는 행동이 claim 단계에서 `unsupported`로 거절되지 않는다.

## 2단계: 상점 관측 어댑터

목표: LLM이 상점의 모든 상품과 서비스를 비교할 수 있게 한다.

AI Teammate 참고 지점:

- `ShopSnapshotBuilder`
- `ShopPlanner`
- `AiTeammateDummyController.ShopExecution`

LLM 관측 필드:

- 현재 골드
- 보유 포션 슬롯과 빈 슬롯 수
- 보유 유물 목록
- 덱 요약
- 상점 상품 목록
  - `shop_item_id`
  - 상품 종류: 카드, 유물, 포션, 카드 제거
  - 이름, 내부 ID, 설명
  - 가격
  - 재고 여부
  - 구매 가능 여부
  - 희귀도
  - 카드라면 비용, 타입, 강화 여부
  - 포션이라면 현재 획득 가능 여부
- 예상 다음 화면

행동 계약:

- `open_shop_inventory`
- `buy_shop_item`
- `remove_card_at_shop`
- `close_shop_inventory`
- `proceed_shop`

실행 재검증:

- 같은 상점 방문인지 확인한다.
- 상품 ID, 가격, 재고, 현재 골드를 다시 확인한다.
- 포션 구매는 빈 슬롯을 다시 확인한다.
- 카드 제거는 제거 대상 카드가 현재 덱에 있는지 다시 확인한다.

휴리스틱 검증:

- 처음에는 구매하지 않고 `proceed_shop`만 실행한다.
- 관측이 안정화되면 저렴한 포션 또는 명확히 구매 가능한 상품 1개만 구매한다.

완료 기준:

- LLM이 JSON만 보고 상점 상품을 비교할 수 있다.
- 가격이 `null`로 남지 않는다.
- 구매하지 않는 선택과 구매하는 선택이 모두 안전하게 실행된다.

## 3단계: 전투 포션 어댑터

목표: LLM이 전투 중 포션 사용 여부와 대상을 선택할 수 있게 한다.

AI Teammate 참고 지점:

- `AiTeammateDummyController.CombatActions`
- `PotionHeuristicEvaluator`

LLM 관측 필드:

- 보유 포션 목록
  - `potion_id`
  - 이름, 설명, 희귀도
  - 사용 가능 여부
  - 대상 타입
  - 예상 효과 요약
- 적 목록과 각 적의 상태
- 플레이어 체력, 방어도, 에너지, 손패
- 현재 전투 위험 요약

행동 계약:

- `use_potion`
  - `potion_slot_index`
  - `potion_id`
  - `target_id` 또는 `null`

실행 재검증:

- 포션 슬롯에 같은 포션이 있는지 확인한다.
- 포션이 이미 대기열에 들어갔는지 확인한다.
- 대상이 필요한 포션은 대상 생존 여부를 확인한다.

휴리스틱 검증:

- 즉시 이득이 명확한 대상 없는 포션부터 사용한다.
- 대상 포션은 체력이 가장 낮거나 가장 위협적인 적을 대상으로 사용한다.

완료 기준:

- 대상 없는 포션과 단일 대상 포션을 각각 안전하게 사용할 수 있다.

## 4단계: 포션 획득/교체 어댑터

목표: LLM이 포션 보상과 교체 상황을 조작할 수 있게 한다.

LLM 관측 필드:

- 새 포션 정보
- 현재 포션 슬롯 목록
- 빈 슬롯 여부
- 각 포션의 가치 요약
- 버릴 수 있는 포션 목록

행동 계약:

- `claim_potion_reward`
- `skip_potion_reward`
- `discard_potion_for_reward`

실행 재검증:

- 새 포션이 아직 보상에 남아 있는지 확인한다.
- 버릴 포션 슬롯에 같은 포션이 있는지 확인한다.

휴리스틱 검증:

- 빈 슬롯이 있으면 받는다.
- 슬롯이 가득 차면 새 포션 가치가 낮을 때 건너뛴다.

완료 기준:

- 포션 슬롯이 가득 찬 상태에서도 자동화가 멈추지 않는다.

## 5단계: 이벤트 조작 어댑터

목표: LLM이 이벤트를 읽고 선택지를 의도적으로 고를 수 있게 한다.

AI Teammate 참고 지점:

- `EventSnapshotBuilder`
- `EventPlanner`
- `Events/Handlers/*`

LLM 관측 필드:

- `event_id`
- `event_type_name`
- 제목
- 설명
- 페이지 인덱스
- 공유 이벤트 여부
- 선택지 목록
  - `event_option_index`
  - `text_key`
  - 제목, 설명
  - 잠김 여부
  - 진행/나가기 여부
  - 즉사 위험
  - `known_outcome`
  - `trust_level`
  - `support_level`
  - `risk_notes`
  - `summary_for_llm`

`known_outcome` 필드:

- `hp_delta`
- `max_hp_delta`
- `gold_delta`
- `card_reward_count`
- `remove_count`
- `upgrade_count`
- `transform_count`
- `enchant_count`
- `relic_ids`
- `potion_ids`
- `fixed_card_ids`
- `curse_card_ids`
- `starts_combat`
- `has_randomness`
- `has_unknown_effects`

행동 계약:

- `choose_event_option`
  - `event_id`
  - `page_index`
  - `event_option_index`
  - `text_key`

실행 재검증:

- 현재 이벤트 ID가 일치하는지 확인한다.
- 페이지 인덱스가 일치하는지 확인한다.
- 선택지 인덱스와 `text_key`가 일치하는지 확인한다.
- 잠긴 선택지와 즉사 선택지는 실행하지 않는다.
- 가능하면 `EventSynchronizer.ChooseOptionForEvent`를 사용한다.
- 공유 이벤트는 `PlayerVotedForSharedOptionIndex`를 사용한다.

휴리스틱 검증:

- 잠긴 선택지는 고르지 않는다.
- 즉사 선택지는 고르지 않는다.
- 알 수 없는 효과가 크면 진행/나가기 선택지를 우선한다.
- 알려진 선택지 중 명확히 이득인 선택지만 고른다.

완료 기준:

- LLM 프롬프트 없이도 사람이 JSON만 보고 선택지를 비교할 수 있다.
- 이벤트 선택 후 다음 페이지, 카드 선택, 보상, 전투, 지도 전환 중 하나로 안정적으로 넘어간다.

## 6단계: 보물상자 어댑터

목표: LLM이 보물방 상태를 이해하고 상자 또는 유물 선택을 조작할 수 있게 한다.

AI Teammate 참고 지점:

- `TreasureRoomRelicSynchronizer`
- `AiTeammateMapAndTreasurePatches`

LLM 관측 필드:

- 보물방 상태
- 상자 열림 여부
- 선택 가능한 유물 목록
  - `treasure_relic_index`
  - 유물 ID, 이름, 설명, 희귀도
- 진행 가능 여부
- 예상 다음 화면

행동 계약:

- `open_treasure_chest`
- `choose_treasure_relic`
- `proceed_treasure`

실행 재검증:

- 아직 보물방인지 확인한다.
- 유물 선택 인덱스가 현재 목록과 일치하는지 확인한다.
- 진행 버튼은 보상 또는 유물 선택이 끝난 뒤에만 누른다.

휴리스틱 검증:

- 상자가 닫혀 있으면 연다.
- 선택형 유물은 첫 번째 유물을 고른다.
- 완료되면 진행한다.

완료 기준:

- 보물방 진입 후 상자 열기, 유물 획득, 지도 복귀가 자동으로 끝난다.

## 7단계: 카드 선택/보상 어댑터 품질 개선

목표: 이미 있는 카드 선택 실행 기능을 LLM이 더 잘 사용할 수 있게 한다.

LLM 관측 필드:

- 선택 목적: 보상, 강화, 제거, 변화, 인챈트, 기타
- `min_select`, `max_select`, `selected_count`
- 카드 목록
  - 카드 ID, 이름, 타입, 희귀도, 비용, 강화 여부
  - 설명
  - 덱 내 중복 수
  - 추천/주의 요약
- 확인 가능 여부

행동 계약:

- `choose_card_selection`
- `confirm_card_selection`
- `choose_card_reward`
- `skip_card_reward`

실행 재검증:

- 선택 카드 인덱스와 카드 ID가 일치하는지 확인한다.
- `max_select`를 넘기지 않는다.
- `min_select` 미만이면 확인하지 않는다.

휴리스틱 검증:

- 선택 목적별 단순 규칙으로 최소 수만 선택한다.
- 최종 선택 품질은 LLM 판단에 맡긴다.

완료 기준:

- LLM이 카드 목록과 목적만 보고 선택 이유를 설명할 수 있다.
- 선택, 확인, 보상 화면 전환이 안정적이다.

## 8단계: 지도/방 선택 어댑터

목표: LLM이 경로 선택을 할 수 있게 지도 상태를 충분히 제공한다.

LLM 관측 필드:

- 현재 층과 액트
- 현재 노드
- 선택 가능한 다음 노드
- 각 노드의 방 타입
- 이후 2~3층의 보이는 경로
- 엘리트, 상점, 휴식, 보물, 이벤트 밀도
- 현재 체력과 위험 요약

행동 계약:

- `choose_map_node`

실행 재검증:

- 현재 지도 상태와 선택 가능한 노드가 일치하는지 확인한다.
- 이미 선택한 노드는 다시 선택하지 않는다.

휴리스틱 검증:

- 가장 왼쪽 또는 첫 번째 합법 노드를 선택한다.
- 이후에는 체력이 낮으면 휴식 경로, 골드가 많으면 상점 경로 같은 단순 규칙을 추가한다.

완료 기준:

- LLM이 지도 JSON만 보고 위험한 경로와 안전한 경로를 비교할 수 있다.

## 9단계: 액트 전환 어댑터

목표: LLM 또는 휴리스틱이 액트 전환 화면을 끝낼 수 있게 한다.

LLM 관측 필드:

- 현재 액트
- 다음 액트
- 전환 준비 여부
- 남은 보상 또는 선택 화면 여부

행동 계약:

- `proceed_act_transition`

실행 재검증:

- 액트 전환 화면인지 확인한다.
- 보상 화면이 남아 있으면 전환을 누르지 않는다.

휴리스틱 검증:

- 보상과 선택이 모두 끝난 뒤 진행한다.

완료 기준:

- 1막 보스 이후 다음 액트 시작 상태까지 자동으로 진행된다.

## 10단계: 저장/이어하기 어댑터

목표: LLM이 런 시작, 이어하기, 같은 시드 재시작을 명령할 수 있게 한다.

LLM 관측 필드:

- 메인 메뉴 상태
- 저장 런 존재 여부
- 계속 버튼 존재 여부
- 선택된 프로필
- 커스텀 모드 설정
- 입력된 시드

행동 계약:

- `continue_run`
- `start_new_seeded_run`
- `abandon_current_run`
- `set_custom_seed`

실행 재검증:

- 저장 런이 있을 때 새 런 시작과 이어하기를 혼동하지 않는다.
- 파괴적인 abandon은 별도 명시 명령이 있을 때만 실행한다.

휴리스틱 검증:

- 사용자가 지정한 고정 시드로 새 런을 시작한다.
- 저장 런이 있으면 우선 continue만 테스트한다.

완료 기준:

- 저장 런 유무와 관계없이 같은 시드로 새 런을 시작할 수 있다.

## 당장 진행할 구현 순서

1. 행동 claim 지원 목록 정합성 보강.
2. 상점 관측 어댑터를 내부 인벤토리 기반으로 재작성.
3. 이벤트 조작 어댑터의 관측 필드와 실행 재검증 추가.
4. 보물상자 어댑터 최소 구현.
5. 전투 포션 어댑터 최소 구현.
6. 포션 교체 어댑터 구현.
7. 카드 선택/보상 관측 품질 개선.
