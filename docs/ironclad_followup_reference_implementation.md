# 아이언클래드 후속 조작/확인 카드 원본 구현

## 목적

이 문서는 `docs/ironclad_card_execution_classification.md`에서 후속 조작 또는 후속 확인이 필요하다고 분류한 52장을 원본 구현 기준으로 다시 정리한다.

어댑터 기준은 다음과 같다.

- 후속 조작 필요: 원본 카드가 `CardSelectCmd`를 호출한다. LLM에게 선택지를 legal action으로 제공해야 한다.
- 후속 확인 필요: 추가 선택은 없지만 원본 카드가 더미, 권능, 비용, 무작위, 자동 사용, 카드 내부 상태를 바꾼다. 상태 출력 또는 다음 행동 결과로 확인해야 한다.
- 직접 재구현 금지: 아래 명령을 어댑터가 흉내 내지 않는다. 원본 `OnPlay`와 전투 훅이 실행되게 두고, 관찰 가능한 상태만 보강한다.

## 후속 조작 필요 카드

| 카드 | 원본 구현 | 어댑터가 열어야 하는 선택 |
|---|---|---|
| Armaments | `CreatureCmd.GainBlock` 후, 비강화면 `CardSelectCmd.FromHandForUpgrade`로 손패 강화 후보를 선택한다. 강화 상태면 손패의 `IsUpgradable` 카드를 전부 `CardCmd.Upgrade`한다. | 비강화 상태에서 강화할 손패 카드 1장. |
| Brand | 자해 피해를 먼저 적용하고, `CardSelectCmd.FromHand(...ExhaustSelectionPrompt...)`로 손패 1장을 고른 뒤 `CardCmd.Exhaust`한다. 이후 힘을 적용한다. | 소멸할 손패 카드 1장. |
| BurningPact | `CardSelectCmd.FromHand(...ExhaustSelectionPrompt...)`로 손패 1장을 고르고 `CardCmd.Exhaust`한 뒤 `CardPileCmd.Draw`를 실행한다. | 소멸할 손패 카드 1장. |
| Headbutt | 대상에게 `DamageCmd.Attack`을 실행한 뒤, 버림 더미 `PileType.Discard`를 `CardSelectCmd.FromSimpleGrid`에 넣고 선택된 카드를 `CardPileCmd.Add(...PileType.Draw, CardPilePosition.Top)`으로 올린다. | 버림 더미에서 뽑을 더미 위로 올릴 카드 1장. |
| TrueGrit | 먼저 `CreatureCmd.GainBlock`한다. 강화 상태면 `CardSelectCmd.FromHand(...ExhaustSelectionPrompt...)` 후 `CardCmd.Exhaust`한다. 비강화면 `Rng.CombatCardSelection.NextItem`으로 손패 1장을 무작위 소멸한다. | 강화 상태에서 소멸할 손패 카드 1장. |

## 후속 확인 필요 카드

| 카드 | 원본 구현 | 확인해야 하는 상태 |
|---|---|---|
| Aggression | `PowerCmd.Apply<AggressionPower>`를 적용한다. | 권능 목록과 다음 전투 흐름에서 권능 효과. |
| Anger | 공격 후 `CardPileCmd.AddGeneratedCardToCombat(...PileType.Discard...)`로 복사 카드를 버림 더미에 추가한다. | 버림 더미에 생성 카드가 추가됐는지. |
| Barricade | `PowerCmd.Apply<BarricadePower>`를 적용한다. 업그레이드는 `EnergyCost.UpgradeBy(-1)`이다. | 턴 종료 후 방어도 유지 여부. |
| BattleTrance | `CardPileCmd.Draw` 후 `PowerCmd.Apply<NoDrawPower>`를 적용한다. | 드로우 결과와 이후 추가 드로우 차단. |
| Cascade | `HasEnergyCostX = true`이고, 소비 에너지 수만큼 `CardPileCmd.AutoPlayFromDrawPile(...CardPilePosition.Top...)`를 실행한다. | 에너지 소비량, 자동 사용된 카드, 더미 변화. |
| Cinder | 공격 후 손패에서 `Rng.CombatCardSelection.NextItem`으로 1장을 고르고 `CardCmd.Exhaust`한다. | 무작위 소멸 카드와 소멸 더미 변화. |
| Colossus | `CreatureCmd.GainBlock` 후 `PowerCmd.Apply<ColossusPower>`를 적용한다. | 방어도와 권능 목록. |
| Corruption | `PowerCmd.Apply<CorruptionPower>`를 적용한다. 업그레이드는 비용 감소다. | 이후 스킬 비용과 소멸 키워드 처리. |
| CrimsonMantle | `PowerCmd.Apply<CrimsonMantlePower>` 후 반환된 권능에 `IncrementSelfDamage()`를 호출한다. | 권능 수치와 이후 자해 증가. |
| Cruelty | `PowerCmd.Apply<CrueltyPower>`를 적용한다. | 이후 피해 계산에 권능이 반영되는지. |
| DarkEmbrace | `PowerCmd.Apply<DarkEmbracePower>`를 적용한다. | 이후 카드 소멸 시 드로우 반응. |
| DemonForm | `PowerCmd.Apply<DemonFormPower>`를 적용한다. | 턴 시작/종료 흐름에서 힘 증가. |
| DrumOfBattle | `CardPileCmd.Draw` 후 `PowerCmd.Apply<DrumOfBattlePower>`를 적용한다. | 드로우 결과와 권능 목록. |
| ExpectAFight | 계산 변수로 에너지를 얻고 `PowerCmd.Apply<NoEnergyGainPower>`를 적용한다. | 획득 에너지와 이후 에너지 획득 차단. |
| Feed | `DamageCmd.Attack` 결과로 대상이 죽었고 사망 트리거 조건이 맞으면 `CreatureCmd.GainMaxHp`를 실행한다. | 처치 직후 최대 체력 증가. |
| FeelNoPain | `PowerCmd.Apply<FeelNoPainPower>`를 적용한다. | 이후 카드 소멸 시 방어 획득. |
| FiendFire | 손패 카드를 모두 `CardCmd.Exhaust`하고, 소멸 수만큼 `DamageCmd.Attack(...WithHitCount(cardCount)...)`를 실행한다. | 소멸 카드 수, 다단 피해, 손패/소멸 더미 변화. |
| FlameBarrier | `CreatureCmd.GainBlock` 후 `PowerCmd.Apply<FlameBarrierPower>`를 적용한다. | 방어도와 이후 반사 피해. |
| Havoc | `CardPileCmd.AutoPlayFromDrawPile(...1, CardPilePosition.Top, forceExhaust: true)`를 실행한다. | 자동 사용 카드와 강제 소멸 여부. |
| Hellraiser | `PowerCmd.Apply<HellraiserPower>`를 적용하고 전용 연출 훅을 가진다. | 권능 목록과 이후 반응 효과. |
| HowlFromBeyond | `BeforeHandDraw` 훅에서 카드가 소멸 더미에 있고 같은 소유자면 `CardCmd.AutoPlay`를 실행한다. | 드로우 전 자동 사용 발생 여부. |
| InfernalBlade | 캐릭터 카드 풀에서 무작위 공격을 생성하고 `SetToFreeThisTurn()` 후 `CardPileCmd.AddGeneratedCardToCombat(...PileType.Hand...)`로 손패에 넣는다. | 생성 카드, 이번 턴 무료 비용, 손패 변화. |
| Inferno | `PowerCmd.Apply<InfernoPower>` 후 반환된 권능에 `IncrementSelfDamage()`를 호출한다. | 권능 수치와 이후 자해 증가. |
| Inflame | `PowerCmd.Apply<StrengthPower>`를 적용한다. | 힘 증가와 다음 공격 피해. |
| Juggernaut | `PowerCmd.Apply<JuggernautPower>`를 적용한다. | 이후 방어 획득 시 피해 반응. |
| Juggling | `PowerCmd.Apply<JugglingPower>`를 적용한다. | 권능 목록과 이후 효과. |
| Mangle | 공격 후 대상에게 `PowerCmd.Apply<ManglePower>`를 적용한다. | 대상 권능과 이후 힘/피해 계산. |
| OneTwoPunch | `PowerCmd.Apply<OneTwoPunchPower>`를 적용한다. | 다음 공격 카드 사용 시 반응. |
| Pillage | 공격 후 조건을 만족하면 반복해서 `CardPileCmd.Draw`를 실행한다. | 처치 여부와 드로우 수. |
| PrimalForce | 손패를 순회하며 `GiantRock`을 만들고, 기존 카드가 강화됐으면 생성 카드도 `CardCmd.Upgrade`한 뒤 `CardCmd.Transform`한다. | 손패 카드가 GiantRock으로 변환됐는지. |
| Pyre | `PowerCmd.Apply<PyrePower>`를 적용한다. | 이후 에너지 관련 권능 효과. |
| Rage | `PowerCmd.Apply<RagePower>`를 적용한다. | 이후 공격 카드 사용 시 방어 획득. |
| Rampage | 공격 후 카드 내부 피해 변수를 증가시킨다. `AfterDowngraded`에서 증가값을 되돌린다. | 같은 카드 인스턴스의 피해 수치 증가. |
| Rupture | `PowerCmd.Apply<RupturePower>`를 적용한다. | 이후 체력 손실 발생 시 힘 증가. |
| SecondWind | 공격 타입이 아닌 손패 카드를 전부 `CardCmd.Exhaust`하고, 각 소멸마다 `CreatureCmd.GainBlock`을 실행한다. | 소멸 카드 수와 방어도 증가. |
| SetupStrike | 공격 후 `PowerCmd.Apply<SetupStrikePower>`를 적용한다. | 이후 조건부 힘 증가 반응. |
| Stampede | `PowerCmd.Apply<StampedePower>`를 적용한다. 업그레이드는 비용 감소다. | 이후 카드 비용 감소 효과. |
| Stoke | 손패 전체를 `CardCmd.Exhaust`하고, 같은 수만큼 카드 풀에서 무작위 카드를 생성해 `CardPileCmd.AddGeneratedCardsToCombat(...PileType.Hand...)`로 넣는다. | 손패 소멸 수, 생성 카드 수, 생성 카드 강화 반영. |
| Stomp | `AfterCardEnteredCombat`와 `BeforeCardPlayed` 훅에서 조건을 계산한 뒤 `EnergyCost.AddThisTurn(-amount)`로 이번 턴 비용을 줄인다. | 손패 진입 직후 비용, 카드 사용 후 비용 변화. |
| StoneArmor | `PowerCmd.Apply<PlatingPower>`를 적용한다. | Plating 권능과 이후 피해 처리. |
| SwordBoomerang | `TargetType.RandomEnemy`이며 `DamageCmd.Attack(...WithHitCount(...)).TargetingRandomOpponents`를 실행한다. | 무작위 대상 분포와 다단 피해 결과. |
| Tank | `PowerCmd.Apply<TankPower>`를 적용한다. 업그레이드는 비용 감소다. | 권능 목록과 이후 비용/방어 효과. |
| Thrash | 2회 공격 후 손패 공격 카드 중 하나를 `Rng.CombatCardSelection.NextItem`으로 고르고 `CardCmd.Exhaust`한다. 소멸 카드 피해를 내부 변수에 반영하며 `AfterDowngraded`가 있다. | 무작위 소멸 카드, 내부 피해 누적, 소멸 더미 변화. |
| Unmovable | `PowerCmd.Apply<UnmovablePower>`를 적용한다. 업그레이드는 비용 감소다. | 이후 첫 방어 획득 배율. |
| Unrelenting | 공격 후 `PowerCmd.Apply<FreeAttackPower>`를 적용한다. | 다음 공격 카드 비용이 0이 되는지. |
| Vicious | `PowerCmd.Apply<ViciousPower>`를 적용한다. | 카드 수 관련 권능 효과. |
| Whirlwind | `HasEnergyCostX = true`이고, 소비 에너지 수를 타격 수로 삼아 `DamageCmd.Attack(...WithHitCount(num)...)`를 전체 적에게 실행한다. | 에너지 소비, 타격 수, 전체 피해. |

## 구현상 결론

1. 선택 브리지 최우선 대상은 `Headbutt`이다.
   - 이유: 원본이 `FromSimpleGrid`로 버림 더미를 보여준다.
   - 지금 `Armaments`에서 검증한 `FromHandForUpgrade`와 다른 선택 화면이다.

2. `FromHand` 선택 브리지는 `BurningPact`, `TrueGrit+`, `Brand`를 같이 처리해야 한다.
   - 후보 출처는 손패다.
   - 선택 목적은 대부분 소멸이다.
   - `Brand`는 선택 전 자해, 선택 후 힘 적용이 있어서 선택 대기 중 상태 표시가 더 중요하다.

3. 비용/자동 사용 카드는 수동 계산하지 않는다.
   - `Cascade`, `Havoc`, `HowlFromBeyond`, `Stomp`, `Whirlwind`는 원본 흐름이 정확한 순서를 가진다.
   - 어댑터는 legal action의 비용 표시와 결과 상태만 검증한다.

4. 권능 카드는 사용 직후만 보면 부족하다.
   - `Unrelenting`, `Rage`, `Juggernaut`, `Unmovable`, `Barricade`, `DemonForm`은 다음 카드나 다음 턴에서 의미가 드러난다.
   - 검증 시나리오는 “카드 사용 -> 상태 확인 -> 반응을 유발하는 다음 행동”까지 포함해야 한다.
