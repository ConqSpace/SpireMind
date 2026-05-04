# 포션 로직 레퍼런스 대조 메모

작성일: 2026-05-03

## 목적

포션은 보상이나 상점 후보에 보이는 순간 바로 쓸 수 있는 자원이 아니다. 먼저 플레이어 인벤토리의 포션 슬롯에 들어와야 한다. 따라서 포션 로직은 다음 두 흐름을 분리해야 한다.

1. 포션 획득 판단: 보상/상점 포션을 받을지, 빈 슬롯이 없으면 무엇을 버릴지 판단한다.
2. 포션 사용 판단: 현재 인벤토리 슬롯에 들어 있는 포션만 전투/비전투 상황에 맞게 사용한다.

이번 메모는 구현 변경 없이, 원본과 AI Teammate 레퍼런스를 기준으로 다음 구현 기준을 정리한다.

## STS2 원본 기준

확인 파일:

- `artifacts/decompiled/sts2/MegaCrit.Sts2.Core.GameActions/UsePotionAction.cs`
- `artifacts/decompiled/sts2/MegaCrit.Sts2.Core.Models/PotionModel.cs`
- `artifacts/decompiled/sts2/MegaCrit.Sts2.Core.Entities.Cards/ActionTargetExtensions.cs`
- `artifacts/decompiled/sts2/MegaCrit.Sts2.Core.AutoSlay.Handlers.Rooms/CombatRoomHandler.cs`

원본의 포션 사용 흐름은 아래와 같다.

1. `PotionModel.EnqueueManualUse(target)`가 호출된다.
2. `UsePotionAction(potion, target, CombatManager.Instance.IsInProgress)`가 만들어진다.
3. `UsePotionAction` 생성자는 포션의 `Owner`에서 실제 슬롯 번호를 다시 찾는다.
4. 실행 시점에는 `Player.GetPotionAtSlotIndex(PotionIndex)`로 포션을 다시 읽는다.
5. 전투 중이고 `potion.TargetType.IsSingleTarget()`이면 `TargetId`가 반드시 필요하다.
6. `PotionModel.OnUseWrapper(choiceContext, creature)`가 포션을 슬롯에서 제거하고, 훅과 실제 `OnUse`를 실행한다.

중요한 대상 규칙:

- 단일 대상 포션: `Self`, `AnyEnemy`, `AnyPlayer`, `AnyAlly`, `TargetedNoCreature`
- 대상 없는 포션: `AllEnemies`, `None` 등 단일 대상이 아닌 타입
- `Self` 포션도 원본 기준으로는 단일 대상이다. 자동 전투 예제는 `Self`를 플레이어 크리처로 해석한다.
- `AnyPlayer` 포션은 싱글 플레이에서도 플레이어 크리처를 대상으로 잡아야 한다. `BlockPotion`, `BloodPotion`, `StrengthPotion` 등이 여기에 속한다.

## AI Teammate 기준

확인 파일:

- `artifacts/ai_teammate_decompiled/AITeammate.Scripts/AiTeammateDummyController.cs`
- `artifacts/ai_teammate_decompiled/AITeammate.Scripts/PotionHeuristicEvaluator.cs`
- `artifacts/ai_teammate_decompiled/AITeammate.Scripts/PotionMetadataBuilder.cs`
- `artifacts/ai_teammate_decompiled/AITeammate.Scripts/AiPotionCombatUseWeights.cs`

AI Teammate는 포션을 세 단계로 나눈다.

1. 인벤토리 포션만 전투 사용 후보로 확장한다.
   - `player.Potions.Where(potion => !potion.IsQueued)`를 기준으로 한다.
   - 보상 후보나 상점 후보를 전투 사용 후보로 직접 취급하지 않는다.
2. 대상 타입에 따라 후보를 확장한다.
   - `AnyEnemy`, `AnyPlayer`, `AnyAlly`는 여러 대상 후보로 확장한다.
   - `Self`는 플레이어 크리처 하나로 고정한다.
   - 단일 대상이 아닌 포션은 `target = null`로 실행 후보를 만든다.
3. 실행 직전 다시 원본 액션을 만든다.
   - `UsePotionAction(potion, target, CombatManager.Instance.IsInProgress)`
   - `BeforeUse` 호출
   - `IsQueued = true`
   - `RunManager.Instance.ActionQueueSynchronizer.RequestEnqueue(action)`

AI Teammate의 `PotionHeuristicEvaluator`는 주로 획득/교체 판단을 담당한다. 전투 사용 자체와 섞지 않는다. 이 분리가 중요하다.

`PotionMetadataBuilder`는 포션 효과를 정적/부분/동적 정보로 분류한다. 특히 모든 포션을 안전한 공격 포션으로 보지 않는다. 예를 들어 Foul Potion처럼 자기 자신이나 아군까지 피해를 줄 수 있는 포션은 효과 메타데이터에서 위험으로 표시해야 한다.

## 현재 SpireMind 코드와 차이

확인 파일:

- `src/SpireMindMod/CombatStateExporter.cs`
- `src/SpireMindMod/CombatLegalActionBuilder.cs`
- `src/SpireMindMod/CombatActionExecutor.cs`

현재 코드의 좋은 점:

- `potion_slots`를 플레이어 상태에 포함한다.
- 비어 있는 슬롯은 `use_potion` 행동으로 노출하지 않는다.
- 실행 직전에 슬롯 범위, 슬롯의 실제 포션, 포션 ID 변경 여부를 다시 확인한다.
- 실행은 `PotionModel.EnqueueManualUse`를 우선 시도하고, 실패하면 `UsePotionAction`을 직접 생성하는 구조다.

현재 코드의 위험:

- `PotionRequiresTarget`가 원본의 `IsSingleTarget()`보다 좁다.
- `Self`, `AnyPlayer`, `AnyAlly`, `TargetedNoCreature`가 단일 대상인데도 현재 판정에서 대상 없음으로 처리될 수 있다.
- `AnyPlayer` 계열 포션은 적 대상 행동이 아니라 플레이어 대상 행동이어야 한다.
- `Self` 포션은 `target = null`이 아니라 플레이어 크리처를 넘겨야 원본 단일 대상 규칙과 맞다.
- `is_usable_now`가 `PassesCustomUsabilityCheck`, `Usage`, 전투 진행 여부, 플레이 페이즈를 충분히 반영하지 못할 수 있다.
- 포션 효과의 위험 정보가 아직 합법 행동에 반영되지 않는다. 아군/자기 피해 가능 포션은 자동 사용 정책에서 별도로 억제해야 한다.

## 다음 구현 기준

포션 사용 로직은 아래 순서로 다시 세운다.

1. 관측
   - `slot_index`
   - `potion_id`
   - `target_type`
   - `usage`
   - `is_queued`
   - `passes_custom_usability_check`
   - 효과 메타데이터

2. 합법 행동 생성
   - 실제 인벤토리 슬롯에 들어 있는 포션만 후보로 만든다.
   - `IsQueued == true`면 제외한다.
   - `CombatOnly`는 전투 중 플레이 가능 단계에서만 후보로 만든다.
   - `AnyTime`은 전투/비전투 조건을 나누어 다룬다.
   - `Self`는 플레이어 크리처 대상 행동으로 만든다.
   - `AnyPlayer`, `AnyAlly`는 살아 있는 플레이어 측 크리처 대상 행동으로 만든다.
   - `AnyEnemy`는 살아 있고 타격 가능한 적 대상 행동으로 만든다.
   - `AllEnemies`, `None`은 대상 없는 행동으로 만든다.

3. 실행 전 재검증
   - 같은 슬롯에 같은 포션이 있는지 확인한다.
   - 포션이 아직 큐에 들어가지 않았는지 확인한다.
   - 현재 전투/비전투 상태와 `Usage`가 맞는지 확인한다.
   - 단일 대상 포션이면 원본 규칙에 맞는 크리처를 찾는다.
   - 대상 크리처가 살아 있고 전투 ID를 갖는지 확인한다.

4. 실행
   - 가능하면 `PotionModel.EnqueueManualUse(target)`만 호출한다.
   - 직접 `UsePotionAction`을 만들 때도 원본과 같은 순서로 `BeforeUse`, `IsQueued`, `RequestEnqueue`를 맞춘다.

5. 확인
   - 슬롯이 비었는지 확인한다.
   - 전투 상태 변화가 관찰되는지 확인한다.
   - HP, 방어도, 힘/민첩, 에너지, 손패, 적 HP, 포션 사용 기록 중 하나 이상이 바뀌는지 확인한다.

## 구현 우선순위 제안

1. 대상 판정부터 원본 `IsSingleTarget()` 기준으로 교체한다.
2. `Self`/`AnyPlayer` 대상 해결을 추가한다.
3. `Usage`와 `PassesCustomUsabilityCheck`를 관측에 포함한다.
4. 합법 행동의 `target_kind`를 `enemy`, `player`, `self`, `none`, `all_enemies`로 분리한다.
5. 효과 메타데이터는 자동 사용 정책에 연결한다. 특히 자기 피해/아군 피해/무작위 효과는 기본적으로 보수적으로 처리한다.

## 결론

포션 로직은 현재 “슬롯에 들어온 포션만 쓴다”는 큰 방향은 맞다. 다만 대상 타입 판정이 원본보다 좁아서 실제 전투에서 플레이어 대상 포션과 자기 대상 포션이 잘못 실행될 수 있다. 다음 구현은 포션 획득/교체 판단과 전투 사용 판단을 분리한 상태에서, 원본의 `UsePotionAction`과 `TargetType.IsSingleTarget()` 규칙을 그대로 따라가는 쪽이 맞다.
