# 아이언클래드 카드 효과 레퍼런스

## 목적

이 문서는 디코드된 STS2 원본 소스를 기준으로 아이언클래드 카드와 어댑터 구현 리스크를 정리한다.

핵심 기준은 단순하다.

- 카드 효과는 어댑터가 다시 구현하지 않는다.
- 원본 `OnPlay`, `OnUpgrade`, 전투 훅이 실행되게 둔다.
- LLM이 선택해야 하는 `CardSelectCmd` 계열 선택만 어댑터 legal action으로 노출한다.
- 대상 지정, X 비용, 무작위, 더미 이동, 권능 효과는 원본 명령을 통과시킨 뒤 상태 관찰로 검증한다.

## 원본 기준 파일

- 카드 풀: `artifacts/decompiled/sts2/MegaCrit.Sts2.Core.Models.CardPools/IroncladCardPool.cs`
- 카드 구현: `artifacts/decompiled/sts2/MegaCrit.Sts2.Core.Models.Cards/*.cs`
- 시작 덱: `artifacts/decompiled/sts2/MegaCrit.Sts2.Core.Models.Characters/Ironclad.cs`

아이언클래드 카드 풀은 `GenerateAllCards()` 기준 87장이다.

## 구현 판단 기준

| 분류 | 의미 | 어댑터 처리 |
|---|---|---|
| 원본 실행 | `DamageCmd`, `CreatureCmd`, `PowerCmd`, `CardPileCmd`, `CardCmd`로 끝나는 효과 | 직접 재구현하지 않는다. 원본 카드 사용 흐름을 실행한다. |
| 대상 지정 | `AnyEnemy`, `AllEnemies`, `RandomEnemy`, `AnyAlly`, `Self` | 현재 legal action 생성 로직으로 대상 선택을 노출한다. |
| 카드 선택 | `CardSelectCmd` 호출 | 선택 후보를 adapter selection phase로 변환한다. |
| 비용 변화 | X 비용, 이번 턴 비용 조정, 내부 비용 상태 | 원본 비용 계산을 유지하고 상태 출력에서 실제 비용을 검증한다. |
| 전투 훅 | `BeforeCardPlayed`, `AfterCardEnteredCombat`, `BeforeHandDraw` 등 | 훅을 직접 흉내 내지 않는다. 원본 훅이 호출되는 경로를 보장한다. |
| 권능 | 지속 효과, 반응 효과 | `PowerCmd.Apply` 이후 상태와 다음 행동 결과로 검증한다. |

## 선택 연결 우선순위

| 우선순위 | 카드 | 원본 선택 호출 | 필요한 어댑터 연결 |
|---|---|---|---|
| 1 | Armaments | `CardSelectCmd.FromHandForUpgrade` | 손패 강화 선택. 현재 구현 및 실전 검증 완료. |
| 1 | Headbutt | `CardSelectCmd.FromSimpleGrid` | 버림 더미 카드 1장을 뽑을 더미 맨 위로 이동하는 선택. 다음 구현 후보. |
| 1 | BurningPact | `CardSelectCmd` | 손패 1장 소멸 선택 후 드로우. |
| 1 | TrueGrit+ | `CardSelectCmd` | 강화 상태에서 손패 1장 소멸 선택. 기본 상태는 무작위 소멸. |
| 2 | Brand | `CardSelectCmd` | 손패 선택, 자해, 소멸, 힘 증가가 함께 일어난다. |

## 검증 우선순위

| 우선순위 | 카드 | 검증 이유 |
|---|---|---|
| 1 | Headbutt | 선택형 카드인데 아직 `FromSimpleGrid` 연결이 필요하다. |
| 1 | TrueGrit+ | 선택형 소멸 카드다. 손패 후보 노출과 확인 흐름을 검증해야 한다. |
| 1 | BurningPact | 선택 후 소멸과 드로우가 이어진다. 선택 브리지와 더미 변화를 같이 확인해야 한다. |
| 2 | Stomp | `AfterCardEnteredCombat`, `BeforeCardPlayed`로 비용이 변한다. 수동 비용 계산을 하면 틀릴 가능성이 높다. |
| 2 | Whirlwind, Cascade | X 비용 카드다. 사용 가능 여부와 에너지 소비를 원본 기준으로 검증해야 한다. |
| 2 | Havoc, HowlFromBeyond | 뽑을 더미 자동 사용, 드로우 전 자동 사용 등 플레이어가 직접 고르지 않는 흐름이 있다. |
| 2 | FiendFire, Stoke, SecondWind | 손패 대량 소멸과 생성, 방어 계산이 묶여 있다. |
| 3 | Rampage, Thrash | 카드 내부 상태가 전투 중 바뀐다. 같은 카드 인스턴스의 변화가 상태에 드러나야 한다. |
| 3 | Unrelenting, Rage, Juggernaut, Unmovable | 권능이 다음 카드 사용이나 방어 획득에 반응한다. |
| 3 | Feed | 처치 여부에 따라 최대 체력이 증가한다. 킬 판정 후처리 검증이 필요하다. |

## 카드 목록

| 카드 ID | 클래스 | 타입 | 대상 | 비용 | 희귀도 | 어댑터 메모 |
|---|---|---|---|---:|---|---|
| CARD.AGGRESSION | Aggression | Power | Self | 1 | Rare | 권능. 업그레이드 시 선천성. |
| CARD.ANGER | Anger | Attack | AnyEnemy | 0 | Common | 공격 후 생성 카드가 더미에 추가된다. |
| CARD.ARMAMENTS | Armaments | Skill | Self | 1 | Common | 비강화는 손패 1장 강화 선택, 강화는 손패 전체 강화. |
| CARD.ASHEN_STRIKE | AshenStrike | Attack | AnyEnemy | 1 | Uncommon | 소멸 더미 수 기반 피해. |
| CARD.BARRICADE | Barricade | Power | Self | 3 | Rare | 방어도 유지 권능. |
| CARD.BASH | Bash | Attack | AnyEnemy | 2 | Basic | 피해와 취약. |
| CARD.BATTLE_TRANCE | BattleTrance | Skill | Self | 0 | Uncommon | 드로우와 드로우 금지 권능. |
| CARD.BLOOD_WALL | BloodWall | Skill | Self | 2 | Common | 자해 후 방어. |
| CARD.BLOODLETTING | Bloodletting | Skill | Self | 0 | Common | 자해 후 에너지 획득. |
| CARD.BLUDGEON | Bludgeon | Attack | AnyEnemy | 3 | Uncommon | 단일 대상 큰 피해. |
| CARD.BODY_SLAM | BodySlam | Attack | AnyEnemy | 1 | Common | 현재 방어도 기반 피해. |
| CARD.BRAND | Brand | Skill | Self | 0 | Rare | 선택, 자해, 소멸, 힘 증가가 결합됨. |
| CARD.BREAK | Break | Attack | AnyEnemy | 1 | Ancient | 피해와 취약. |
| CARD.BREAKTHROUGH | Breakthrough | Attack | AllEnemies | 1 | Common | 자해 후 전체 피해. |
| CARD.BULLY | Bully | Attack | AnyEnemy | 0 | Uncommon | 적 상태에 따른 조건 피해. |
| CARD.BURNING_PACT | BurningPact | Skill | Self | 1 | Uncommon | 카드 선택 소멸 후 드로우. |
| CARD.CASCADE | Cascade | Skill | Self | X | Rare | X 비용, 뽑을 더미 카드 자동 사용. |
| CARD.CINDER | Cinder | Attack | AnyEnemy | 2 | Common | 공격 후 무작위 손패 소멸. |
| CARD.COLOSSUS | Colossus | Skill | Self | 1 | Uncommon | 방어와 권능. |
| CARD.CONFLAGRATION | Conflagration | Attack | AllEnemies | 1 | Rare | 전체 피해, 계산 피해. |
| CARD.CORRUPTION | Corruption | Power | Self | 3 | Ancient | 스킬 비용 및 소멸 상호작용 권능. |
| CARD.CRIMSON_MANTLE | CrimsonMantle | Power | Self | 1 | Rare | 자해 증가 계열 권능. |
| CARD.CRUELTY | Cruelty | Power | Self | 1 | Rare | 권능 수치가 비율 성격. |
| CARD.DARK_EMBRACE | DarkEmbrace | Power | Self | 2 | Rare | 소멸 반응 드로우 권능. |
| CARD.DEFEND_IRONCLAD | DefendIronclad | Skill | Self | 1 | Basic | 방어 단순형. |
| CARD.DEMON_FORM | DemonForm | Power | Self | 3 | Rare | 턴마다 힘 증가. |
| CARD.DEMONIC_SHIELD | DemonicShield | Skill | AnyAlly | 0 | Uncommon | 자해와 대상 방어. |
| CARD.DISMANTLE | Dismantle | Attack | AnyEnemy | 1 | Uncommon | 방어도 조건 다단 피해. |
| CARD.DOMINATE | Dominate | Skill | AnyEnemy | 1 | Uncommon | 대상 상태 기반 힘 획득. |
| CARD.DRUM_OF_BATTLE | DrumOfBattle | Power | Self | 0 | Uncommon | 드로우와 권능. |
| CARD.EVIL_EYE | EvilEye | Skill | Self | 1 | Uncommon | 이번 턴 소멸 이력 기반 방어. |
| CARD.EXPECT_A_FIGHT | ExpectAFight | Skill | Self | 2 | Uncommon | 손패 공격 수 기반 에너지. |
| CARD.FEED | Feed | Attack | AnyEnemy | 1 | Rare | 처치 시 최대 체력 증가. |
| CARD.FEEL_NO_PAIN | FeelNoPain | Power | Self | 1 | Uncommon | 소멸 반응 방어 권능. |
| CARD.FIEND_FIRE | FiendFire | Attack | AnyEnemy | 2 | Rare | 손패 전부 소멸, 소멸 수만큼 타격. |
| CARD.FIGHT_ME | FightMe | Attack | AnyEnemy | 2 | Uncommon | 양쪽 힘 증가. |
| CARD.FLAME_BARRIER | FlameBarrier | Skill | Self | 2 | Uncommon | 방어와 반사 피해 권능. |
| CARD.FORGOTTEN_RITUAL | ForgottenRitual | Skill | Self | 1 | Uncommon | 이번 턴 소멸 조건 에너지. |
| CARD.HAVOC | Havoc | Skill | Self | 1 | Common | 뽑을 더미 맨 위 카드 자동 사용. |
| CARD.HEADBUTT | Headbutt | Attack | AnyEnemy | 1 | Common | 피해 후 버림 더미 선택. |
| CARD.HELLRAISER | Hellraiser | Power | Self | 2 | Rare | 권능과 전용 연출 훅. |
| CARD.HEMOKINESIS | Hemokinesis | Attack | AnyEnemy | 1 | Uncommon | 자해 후 피해. |
| CARD.HOWL_FROM_BEYOND | HowlFromBeyond | Attack | AllEnemies | 3 | Uncommon | 드로우 전 자동 사용 훅. |
| CARD.IMPERVIOUS | Impervious | Skill | Self | 2 | Rare | 큰 방어, 소멸. |
| CARD.INFERNAL_BLADE | InfernalBlade | Skill | Self | 1 | Uncommon | 무작위 공격 생성, 이번 턴 무료. |
| CARD.INFERNO | Inferno | Power | Self | 1 | Uncommon | 자해 증가 계열 권능. |
| CARD.INFLAME | Inflame | Power | Self | 1 | Uncommon | 힘 증가 권능. |
| CARD.IRON_WAVE | IronWave | Attack | AnyEnemy | 1 | Common | 방어 후 피해. |
| CARD.JUGGERNAUT | Juggernaut | Power | Self | 2 | Rare | 방어 획득 반응 피해 권능. |
| CARD.JUGGLING | Juggling | Power | Self | 1 | Uncommon | 권능, 업그레이드 시 선천성. |
| CARD.MANGLE | Mangle | Attack | AnyEnemy | 3 | Rare | 대상 피해와 힘 감소류 권능. |
| CARD.MOLTEN_FIST | MoltenFist | Attack | AnyEnemy | 1 | Common | 피해 후 조건부 취약 증가. |
| CARD.NOT_YET | NotYet | Skill | Self | 2 | Rare | 회복, 소멸. |
| CARD.OFFERING | Offering | Skill | Self | 0 | Rare | 자해, 에너지, 드로우. |
| CARD.ONE_TWO_PUNCH | OneTwoPunch | Skill | Self | 1 | Rare | 다음 공격 관련 권능. |
| CARD.PACTS_END | PactsEnd | Attack | AllEnemies | 0 | Rare | 소멸 더미 카드 수 조건 전체 피해. |
| CARD.PERFECTED_STRIKE | PerfectedStrike | Attack | AnyEnemy | 2 | Common | Strike 태그 수 기반 피해. |
| CARD.PILLAGE | Pillage | Attack | AnyEnemy | 1 | Uncommon | 처치 시 드로우 반복. |
| CARD.POMMEL_STRIKE | PommelStrike | Attack | AnyEnemy | 1 | Common | 피해와 드로우. |
| CARD.PRIMAL_FORCE | PrimalForce | Skill | Self | 0 | Rare | 손패를 GiantRock으로 변환. |
| CARD.PYRE | Pyre | Power | Self | 2 | Rare | 에너지 관련 권능. |
| CARD.RAGE | Rage | Skill | Self | 0 | Uncommon | 공격 사용 반응 방어 권능. |
| CARD.RAMPAGE | Rampage | Attack | AnyEnemy | 1 | Uncommon | 사용 후 카드 내부 피해 증가. |
| CARD.RUPTURE | Rupture | Power | Self | 1 | Uncommon | 체력 손실 반응 힘 권능. |
| CARD.SECOND_WIND | SecondWind | Skill | Self | 1 | Uncommon | 공격 제외 손패 소멸, 소멸 수 방어. |
| CARD.SETUP_STRIKE | SetupStrike | Attack | AnyEnemy | 1 | Common | 다음 조건부 힘 권능. |
| CARD.SHRUG_IT_OFF | ShrugItOff | Skill | Self | 1 | Common | 방어와 드로우. |
| CARD.SPITE | Spite | Attack | AnyEnemy | 0 | Uncommon | 체력 손실 이력 기반 다단 피해. |
| CARD.STAMPEDE | Stampede | Power | Self | 2 | Uncommon | 비용 감소 권능. |
| CARD.STOKE | Stoke | Skill | Self | 1 | Rare | 손패 전부 소멸 후 무작위 카드 생성. |
| CARD.STOMP | Stomp | Attack | AllEnemies | 3 | Uncommon | 전투 진입/카드 사용 전 비용 조정 훅. |
| CARD.STONE_ARMOR | StoneArmor | Power | Self | 1 | Uncommon | Plating 권능. |
| CARD.STRIKE_IRONCLAD | StrikeIronclad | Attack | AnyEnemy | 1 | Basic | 대상 단순 피해. |
| CARD.SWORD_BOOMERANG | SwordBoomerang | Attack | RandomEnemy | 1 | Common | 무작위 적 다단 피해. |
| CARD.TANK | Tank | Power | Self | 1 | Rare | 권능, 비용 감소. |
| CARD.TAUNT | Taunt | Skill | AnyEnemy | 1 | Uncommon | 방어와 대상 취약. |
| CARD.TEAR_ASUNDER | TearAsunder | Attack | AnyEnemy | 2 | Rare | 조건에 따른 타격 횟수. |
| CARD.THRASH | Thrash | Attack | AnyEnemy | 1 | Rare | 공격 소멸, 소멸 카드 피해 누적. |
| CARD.THUNDERCLAP | Thunderclap | Attack | AllEnemies | 1 | Common | 전체 피해와 전체 취약. |
| CARD.TREMBLE | Tremble | Skill | AnyEnemy | 1 | Common | 대상 취약, 소멸. |
| CARD.TRUE_GRIT | TrueGrit | Skill | Self | 1 | Common | 기본은 무작위 소멸, 강화는 선택 소멸. |
| CARD.TWIN_STRIKE | TwinStrike | Attack | AnyEnemy | 1 | Common | 대상 2회 타격. |
| CARD.UNMOVABLE | Unmovable | Power | Self | 2 | Rare | 첫 방어 획득 배율 권능. |
| CARD.UNRELENTING | Unrelenting | Attack | AnyEnemy | 2 | Uncommon | 다음 공격 무료 권능. |
| CARD.UPPERCUT | Uppercut | Attack | AnyEnemy | 2 | Uncommon | 피해, 약화, 취약. |
| CARD.VICIOUS | Vicious | Power | Self | 1 | Uncommon | 카드 수 관련 권능. |
| CARD.WHIRLWIND | Whirlwind | Attack | AllEnemies | X | Uncommon | X 비용 전체 다단 피해. |

## 다음 구현 후보

1. `CardSelectCmd.FromSimpleGrid` 브리지 구현
   - `Headbutt`의 버림 더미 선택을 먼저 목표로 삼는다.
   - 이후 `BurningPact`, `TrueGrit+`, `Brand`에 같은 선택 구조를 확장한다.

2. 선택형 카드의 legal action 안정 ID 보강
   - 후보 카드의 `pile`, `model_id`, `index`, `upgraded`를 포함한다.
   - 같은 이름 카드가 여러 장 있을 때도 LLM이 안정적으로 선택할 수 있어야 한다.

3. 비용 변화 카드 검증
   - `Stomp`, `Whirlwind`, `Cascade`를 우선 검증한다.
   - 원본 비용 계산이 상태 출력과 legal action에 같은 값으로 드러나는지 확인한다.

4. 권능 반응 카드 검증
   - `Unrelenting`, `Rage`, `Juggernaut`, `Unmovable`을 우선 검증한다.
   - 사용 직후 상태뿐 아니라 다음 카드 사용 결과까지 확인한다.
