# 전투 상태 JSON 형식

이 문서는 LLM에게 전달할 STS2 전투 상태 JSON의 1차 형식을 정의한다.

핵심 원칙은 간단하다. 화면 묘사가 아니라, 판단과 실행에 필요한 게임 상태를 구조화해서 보낸다. LLM이 볼 수 있는 정보 범위가 명확해야 실패 원인을 해석할 수 있다.

## 최상위 구조

```json
{
  "schema_version": "combat_state.v1",
  "phase": "combat_turn",
  "state_id": "run_ABC123_floor_06_combat_02_turn_03_step_01",
  "exported_at_ms": 1710000000000,
  "run": {},
  "player": {},
  "relics": [],
  "piles": {},
  "enemies": [],
  "legal_actions": []
}
```

## 필드 규칙

- `schema_version`: 상태 JSON 형식 버전이다.
- `phase`: 현재 결정 종류다. 1차는 `combat_turn`만 사용한다.
- `state_id`: 같은 상태인지 판별하기 위한 식별자다. LLM 응답 검증에 사용한다.
- `exported_at_ms`: 로그 정렬용 시간이다.
- `run`: 판 전체의 고정 정보다.
- `player`: 현재 플레이어 상태다.
- `relics`: 현재 보유 유물이다.
- `piles`: 전투 중 카드 위치다.
- `enemies`: 살아 있는 적 목록이다.
- `legal_actions`: 모드가 만든 실행 가능한 행동 목록이다.

## run

```json
{
  "game": "Slay the Spire 2",
  "character": "Ironclad",
  "act": 1,
  "floor": 6,
  "ascension": 0,
  "seed": "ABC123",
  "mode": "single_player"
}
```

## player

```json
{
  "id": "player_0",
  "hp": 58,
  "max_hp": 80,
  "block": 5,
  "energy": 3,
  "max_energy": 3,
  "gold": 99,
  "buffs": [],
  "debuffs": []
}
```

버프와 디버프는 같은 구조를 쓴다.

```json
{
  "id": "strength",
  "name": "Strength",
  "amount": 2,
  "description": "Attack damage is increased by 2.",
  "source_type": "power"
}
```

### 버프/디버프 분류 규칙

- 게임 내부 Power가 긍정 효과인지 부정 효과인지 확실하면 `buffs` 또는 `debuffs`로 나눈다.
- 분류가 애매하면 `buffs`에 억지로 넣지 않는다. `powers_unknown` 확장 필드에 둔다.
- LLM 판단에 중요한 효과는 `description`을 넣는다.
- 현재 구현은 `Power`, `StatusEffect`, `Buff`, `Debuff`, `Effect` 계열 런타임 객체를 수집한다.
- `isBuff`, `isDebuff`, `powerType`, `category`, `id`, `name`, 타입 이름을 근거로 분류한다.
- 분류 근거가 부족하면 전투 판단을 오염시키지 않기 위해 `powers_unknown`에 남긴다.

## relics

```json
[
  {
    "id": "BurningBloodRelic",
    "name": "Burning Blood",
    "description": "Heal 6 HP at the end of combat.",
    "rarity": "starter",
    "counter": null
  }
]
```

`counter`는 충전 횟수, 남은 발동 횟수, 누적 수치가 있는 유물에서 사용한다. 없으면 `null`이다.

## piles

1차 실험은 완전 정보 모드를 기본값으로 한다. 즉 뽑을 카드 더미의 카드 목록도 LLM에 공개한다.

```json
{
  "hand": [],
  "draw_pile": [],
  "discard_pile": [],
  "exhaust_pile": []
}
```

카드는 다음 구조를 사용한다.

```json
{
  "instance_id": "draw_03",
  "card_id": "StrikeCard",
  "name": "Strike",
  "type": "attack",
  "cost": 1,
  "base_cost": 1,
  "upgraded": false,
  "playable": true,
  "target_type": "enemy",
  "damage": 6,
  "block": 0,
  "hits": 1,
  "description": "Deal 6 damage."
}
```

### 카드 더미 규칙

- `instance_id`는 현재 전투 안에서 카드 한 장을 구분한다.
- `card_id`는 카드 종류를 나타낸다.
- 같은 이름의 카드가 여러 장 있어도 `instance_id`는 달라야 한다.
- `cost`는 현재 비용이다. 비용 변경 효과가 있으면 변경된 값을 넣는다.
- `base_cost`는 기본 비용이다. 알 수 없으면 `null`을 넣는다.
- `playable`은 현재 손패에서만 의미가 있다. 다른 더미에서는 `false` 또는 생략할 수 있다.

## enemies

```json
[
  {
    "id": "enemy_0",
    "name": "Cultist",
    "hp": 34,
    "max_hp": 48,
    "block": 0,
    "buffs": [],
    "debuffs": [],
    "intent": {}
  }
]
```

## intent

적 의도는 문자열 하나로 끝내지 않는다. 피해량, 횟수, 부가 효과를 나눠서 보낸다.

```json
{
  "type": "attack_debuff",
  "raw_intent": "AttackDebuffIntent",
  "damage": 8,
  "hits": 1,
  "total_damage": 8,
  "block": 0,
  "applied_powers": [
    {
      "target": "player",
      "id": "frail",
      "name": "Frail",
      "amount": 2
    }
  ],
  "description": "Attack for 8 and apply 2 Frail."
}
```

### 의도 규칙

- `damage`는 1회 피해량이다.
- `hits`는 반복 횟수다.
- `total_damage`는 `damage * hits`다.
- 약화, 힘 감소 같은 효과가 이미 반영된 피해량인지 `damage_is_adjusted` 확장 필드로 표시할 수 있다.
- 피해가 없는 의도는 `damage`, `hits`, `total_damage`를 0으로 둔다.

## 압축 모드

비용 문제가 생기면 카드 더미 전체 목록 대신 요약을 보낼 수 있다. 단, 1차 실험에서는 사용하지 않는다.

```json
{
  "draw_pile_summary": {
    "count": 8,
    "attack_count": 4,
    "skill_count": 3,
    "power_count": 1,
    "block_cards": 3,
    "damage_cards": 4
  }
}
```

## 1차 완료 기준

- 전투 시작 후 매 턴 상태 JSON이 생성된다.
- 카드 사용, 드로우, 버림, 소멸 후 더미가 갱신된다.
- 적 사망, 소환, 턴 변경 후 적 목록과 의도가 갱신된다.
- 버프/디버프 수치 변화가 다음 상태 JSON에 반영된다.
- 같은 상태에서 생성한 `legal_actions`가 실행 검증을 통과한다.

## R2 구현 메모

- R2 exporter는 읽기 전용 `combat_state.v1` 생성만 담당한다.
- R2 산출물에는 `legal_actions`를 넣지 않는다. 행동 후보 생성과 실행 검증은 R3에서 다룬다.
- 현재 구현은 STS2 타입을 직접 참조하지 않고 리플렉션으로 가능한 필드만 채운다. 런타임에서 값이 확인되지 않는 필드는 `null` 또는 빈 목록으로 남을 수 있다.
