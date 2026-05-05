# Codex 판단기 연결

이 문서는 `scripts/codex_decider.js`를 사용해 의사결정 루프에 Codex CLI 판단기를 붙이는 방법을 정리한다.

목표는 Codex가 게임을 직접 조작하지 않게 하는 것이다. Codex는 현재 전투 상태를 읽고 JSON 행동만 반환한다. 브리지는 그 행동을 검증하고, STS2 모드는 검증된 행동만 실행한다.

```text
decision_loop --mode command
-> scripts/codex_decider.js
-> codex exec
-> JSON 행동 응답
-> 브리지 검증
-> STS2 모드 실행
```

## 실행 예시

```powershell
node .\bridge\spiremind_decision_loop.js `
  --mode command `
  --command node `
  --command-arg .\scripts\codex_decider.js `
  --once `
  --wait-result `
  --scenario-id "manual_codex_check" `
  --play-session-id "session_codex_001" `
  --run-log-dir "$env:APPDATA\SlayTheSpire2\SpireMind\runs\manual_codex_check"
```

기본 모델은 `gpt-5.4-mini`다. 바꾸려면 환경 변수를 쓴다.

```powershell
$env:SPIREMIND_CODEX_MODEL = "gpt-5.4-mini"
```

Codex 실행 시간이 길어질 수 있으므로, 처음에는 한 전투 한 턴만 확인한다.

한 전투를 끝까지 맡길 때는 `decision_loop`의 반복 전투 모드를 사용한다.

```powershell
node .\bridge\spiremind_decision_loop.js `
  --mode command `
  --command node `
  --command-arg .\scripts\codex_decider.js `
  --until-combat-end `
  --wait-result `
  --max-decisions 20 `
  --run-log-dir "$env:APPDATA\SlayTheSpire2\SpireMind\runs\codex_combat_001"
```

이 경로는 전투 턴에만 Codex를 호출한다. 전투가 끝났거나 보상 화면 같은 비전투 상태로 넘어가면 새 판단을 요청하지 않고 멈춘다.

## 입력과 출력

`decision_loop`는 Codex 판단기에 현재 상태 JSON을 stdin으로 보낸다.

Codex 판단기는 내부적으로 상태를 압축한다. 외부 판단기에 보내는 정보는 다음으로 제한한다.

- `play_session_id`
- 플레이어 체력, 방어도, 에너지
- 손패 카드 요약
- 적 체력, 방어도, 의도
- 유물 요약
- `legal_actions`
- 같은 실행 기록 안의 최근 판단 결과와 전투 변화량

외부 판단기에 보내지 않는 정보:

- 내부 `scenario_id`
- 비교나 점수 산정에 관한 설명
- 반복 실행 횟수
- 지표 계산 방식

`--run-log-dir`를 함께 쓰면 `decision_loop`가 최근 기록을 `recent_history`로 함께 보낸다. 여기에는 `memory_summary.json`의 압축 기억, 이전 판단, 행동 결과, 체력 변화, 적 체력 변화가 들어간다. Codex는 이 정보를 참고할 수 있지만, 실제 선택은 항상 현재 `legal_actions` 안에서 해야 한다.

Codex 판단기의 stdout은 JSON 객체 하나여야 한다.

```json
{
  "actions": [
    {
      "type": "play_card",
      "combat_card_id": 0,
      "target_combat_id": 1
    },
    {
      "type": "end_turn"
    }
  ],
  "reason": "피해를 먼저 준 뒤 턴을 종료합니다."
}
```

단일 행동도 가능하다.

```json
{
  "selected_action_id": "end_turn",
  "reason": "남은 에너지로 의미 있는 행동이 없습니다."
}
```

## 테스트용 고정 응답

실제 Codex 호출 없이 연결 경로만 확인할 때는 `SPIREMIND_CODEX_FAKE_DECISION`을 쓴다.

```powershell
$env:SPIREMIND_CODEX_FAKE_DECISION = '{"selected_action_id":"end_turn","reason":"smoke"}'
node .\bridge\spiremind_decision_loop.js `
  --mode command `
  --command node `
  --command-arg .\scripts\codex_decider.js `
  --once `
  --dry-run
Remove-Item Env:\SPIREMIND_CODEX_FAKE_DECISION
```

이 경로는 `decision_loop_smoke_check.ps1`에서도 검증한다.

## 현재 한계

- `codex exec`는 호출마다 새 프로세스를 시작한다.
- 장기 상주 세션은 아직 구현하지 않았다.
- 응답이 느리면 `--timeout-ms`와 `SPIREMIND_CODEX_TIMEOUT_MS`를 함께 늘려야 한다.
- 전투 한 턴 판단부터 확인한 뒤, 여러 턴 연속 실행으로 넓힌다.
