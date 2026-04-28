# STS2 자동 테스트 명령 채널

SpireMind 모드는 전투 상태 내보내기와 행동 처리 루프에서 자동 테스트 명령 파일을 주기적으로 확인한다. 외부 테스트 도구는 이 파일에 명령을 쓰고, 결과 파일을 읽어 적용 여부를 확인한다.

## 파일 경로

- 명령 파일: `%APPDATA%\SlayTheSpire2\SpireMind\autotest_command.json`
- 결과 파일: `%APPDATA%\SlayTheSpire2\SpireMind\autotest_result.json`
- 보조 로그: `%APPDATA%\SlayTheSpire2\SpireMind\spiremind.log`

같은 `id`를 가진 명령은 한 번만 실행된다. 같은 동작을 다시 실행하려면 새 `id`를 사용한다.

## 권장 사용 순서

1. 게임을 실행하고 메인 메뉴까지 진입한다.
2. 저장된 런이 있으면 `continue_run`으로 메인 메뉴의 "계속" 흐름을 내부 API로 실행한다.
3. `combat_state.json`이 갱신될 때까지 대기한다.
4. 전투 상태가 필요하지만 저장 런으로 전투에 들어가지 못한 경우에만 `enter_combat_debug`를 사용한다.

## continue_run

`continue_run`은 Godot UI 클릭이나 좌표 입력을 사용하지 않는다. 현재 메인 메뉴 객체를 찾은 뒤 `NMainMenu.OnContinueButtonPressedAsync()`를 reflection으로 호출한다.

예시:

```json
{
  "id": "cmd-continue-001",
  "action": "continue_run",
  "params": {
    "timeout_ms": 15000
  }
}
```

필드 동작:

- `timeout_ms`: 비동기 이어하기 흐름을 기다릴 최대 시간이다. 없으면 완료될 때까지 관찰한다.

안전 조건:

- `NGame.Instance`가 없으면 실패한다.
- 현재 화면에서 메인 메뉴 객체를 찾지 못하면 실패한다.
- 저장된 런이 없거나 Continue 버튼이 비활성 상태이면 실패한다.
- 이미 런이 진행 중이면 `already_in_run` 메시지로 성공 처리하고 다시 호출하지 않는다.
- 전투 중이면 중복 이어하기를 막기 위해 거절한다.

성공 예시:

```json
{
  "id": "cmd-continue-001",
  "action": "continue_run",
  "status": "applied",
  "message": "continue_run 호출이 완료되었습니다.",
  "timestamp": "2026-04-28T00:00:00.0000000+00:00",
  "diagnostics": {
    "n_game_found": true,
    "main_menu_found": true,
    "run_manager_found": true,
    "is_in_progress": true,
    "combat_in_progress": false
  }
}
```

## enter_combat_debug

`enter_combat_debug`는 빠른 전투 진입용 명령이다. Godot UI 노드 클릭 없이 `RunManager.Instance.EnterRoomDebug(...)`를 reflection으로 호출한다.

예시:

```json
{
  "id": "cmd-001",
  "action": "enter_combat_debug",
  "params": {
    "room_type": "Monster",
    "map_point_type": "Monster",
    "encounter_id": null,
    "show_transition": false
  }
}
```

필드 동작:

- `room_type`: 실패하거나 없으면 `Monster`를 사용한다.
- `map_point_type`: 실패하거나 없으면 `Monster`를 사용한다.
- `encounter_id`: 현재는 `null`만 지원한다. 값을 지정하면 명령을 거절한다.
- `show_transition`: 실패하거나 없으면 `false`를 사용한다.

## 결과 예시

성공:

```json
{
  "id": "cmd-001",
  "action": "enter_combat_debug",
  "status": "applied",
  "message": "debug combat entry requested",
  "timestamp": "2026-04-28T00:00:00.0000000+00:00"
}
```

실패 또는 거절:

```json
{
  "id": "cmd-001",
  "action": "enter_combat_debug",
  "status": "rejected",
  "message": "이미 전투 중이므로 enter_combat_debug를 실행하지 않습니다.",
  "timestamp": "2026-04-28T00:00:00.0000000+00:00"
}
```

## 위기 상황과 제한

- 저장 런이 없으면 `continue_run`은 실행되지 않는다.
- 런이 시작되지 않아 `RunState`가 없으면 `enter_combat_debug`는 실행되지 않는다.
- 전투 중이면 중복 진입을 막기 위해 명령을 실행하지 않는다.
- 명령 파일 JSON이 깨져도 게임 진행은 멈추지 않는다. 실패 결과와 로그만 남긴다.
- 이 명령 채널은 자동 테스트 속도를 위한 우회 경로다. 일반 이동 흐름 검증에는 사용하지 않는다.
