# 게임 실행 가이드

마지막 갱신: 2026-04-29

이 문서는 SpireMind 개발과 테스트 중 Slay the Spire 2를 실행하는 방법을 정리한다.

## 1. 기본 정보

현재 로컬 환경에서 확인된 STS2 정보는 다음과 같다.

- Steam 앱 ID: `2868840`
- Steam appmanifest: `I:\SteamLibrary\steamapps\appmanifest_2868840.acf`
- 게임 설치 폴더: `I:\SteamLibrary\steamapps\common\Slay the Spire 2`
- 실행 파일: `I:\SteamLibrary\steamapps\common\Slay the Spire 2\SlayTheSpire2.exe`
- 모드 폴더: `I:\SteamLibrary\steamapps\common\Slay the Spire 2\mods`
- SpireMind 모드 폴더: `I:\SteamLibrary\steamapps\common\Slay the Spire 2\mods\SpireMind`

상태 파일과 로그는 보통 아래 경로에서 확인한다.

- 상태 파일: `%APPDATA%\SlayTheSpire2\SpireMind\combat_state.json`
- 브리지 실행 기록: `%APPDATA%\SlayTheSpire2\SpireMind\bridge_runs`
- Godot 로그: `%APPDATA%\SlayTheSpire2\logs\godot.log`

## 2. 권장 실행 방법

가장 안전한 실행 방법은 Steam URI를 사용하는 것이다.

```powershell
Start-Process "steam://rungameid/2868840"
```

이 방법은 Steam이 게임 소유권, 런타임, 오버레이 상태를 정상적으로 준비한 뒤 게임을 실행한다. 자동 실행 중 “Steam에서 실행하라”는 오류가 뜨면 직접 exe 실행보다 이 방식을 우선 사용한다.

## 3. Steam 클라이언트에서 수동 실행

수동으로 확인할 때는 다음 순서로 진행한다.

1. Steam을 켠다.
2. 라이브러리에서 `Slay the Spire 2`를 선택한다.
3. 실행 버튼을 누른다.
4. 메인 메뉴가 뜨면 모드 초기화 오류가 없는지 확인한다.
5. `계속`을 눌러 저장된 런으로 들어간다.
6. 전투, 이벤트, 보상, 지도 중 테스트할 화면까지 이동한다.

현재 자동화 테스트에서는 메인 메뉴의 `계속` 버튼을 누르면 바로 이어서 진행할 수 있는 저장 상태를 자주 사용한다.

## 4. 런타임 스모크 스크립트로 실행

프로젝트에는 반자동 런타임 확인 스크립트가 있다.

```powershell
.\scripts\runtime_smoke_check.ps1 -LaunchGame
```

이 명령은 기본적으로 Steam URI인 `steam://rungameid/2868840`을 사용한다.

빌드, 배포, 실행, 상태 감시를 한 번에 수행하려면 다음 명령을 사용한다.

```powershell
.\scripts\runtime_smoke_check.ps1 `
  -Build `
  -Deploy `
  -LaunchGame `
  -ModsDir "I:\SteamLibrary\steamapps\common\Slay the Spire 2\mods"
```

브리지 실행, 게임 실행, 고정 시드 커스텀 런 시작까지 한 번에 처리하려면 다음 명령을 사용한다.

```powershell
.\scripts\runtime_smoke_check.ps1 `
  -Build `
  -Deploy `
  -StartBridge `
  -LaunchGame `
  -StartSeededRun `
  -Seed "7MJCUHEB5Q" `
  -ModsDir "I:\SteamLibrary\steamapps\common\Slay the Spire 2\mods"
```

이 명령은 게임을 실행한 뒤 기본 25초를 기다리고, `%APPDATA%\SlayTheSpire2\SpireMind\autotest_command.json`에 `start_new_run` 명령을 작성한다. 모드 쪽 실행기가 이 명령을 받아 `싱글플레이 -> 커스텀 -> 시드 입력 -> 캐릭터 선택 -> 시작` 순서로 이동한다.

기본 대기 시간이 짧으면 `-LaunchWaitSeconds 40`처럼 늘린다. 게임이 이미 메인 메뉴에 있으면 `-LaunchGame`을 빼고 `-StartSeededRun`만 사용할 수 있다.

`-StartSeededRun`을 쓰지 않는 경우에는 기존처럼 게임을 켠 뒤 전투에 들어가거나 카드를 쓰는 조작은 사람이 수행해야 한다.

## 5. 직접 실행 방법

직접 실행 파일을 호출할 수도 있다.

```powershell
Start-Process `
  -FilePath "I:\SteamLibrary\steamapps\common\Slay the Spire 2\SlayTheSpire2.exe" `
  -WorkingDirectory "I:\SteamLibrary\steamapps\common\Slay the Spire 2"
```

다만 직접 실행은 Steam 초기화가 제대로 되지 않아 오류가 날 수 있다. 테스트 자동화에서는 Steam URI 실행을 우선한다.

렌더러를 명시해야 할 때는 게임 폴더의 bat 파일을 사용할 수 있다.

```powershell
Start-Process `
  -FilePath "I:\SteamLibrary\steamapps\common\Slay the Spire 2\launch_d3d12.bat" `
  -WorkingDirectory "I:\SteamLibrary\steamapps\common\Slay the Spire 2"
```

다른 선택지는 다음과 같다.

- `launch_d3d12.bat`: `--rendering-driver d3d12`
- `launch_opengl.bat`: `--rendering-driver opengl3`
- `launch_vulkan.bat`: `--rendering-driver vulkan`

## 6. 실행 전 권장 순서

코드를 수정한 뒤 실제 게임에서 확인할 때는 아래 순서를 따른다.

1. 게임이 켜져 있으면 먼저 종료한다.
2. 모드를 빌드한다.
3. 모드 DLL을 배포한다.
4. 브리지 서버를 켠다.
5. Steam URI로 게임을 실행한다.
6. 메인 메뉴에서 모드 초기화 오류를 확인한다.
7. 테스트할 화면으로 이동한다.
8. `combat_state.json`과 브리지 상태를 확인한다.
9. 의사결정 루프를 실행한다.

기본 명령 예시는 다음과 같다.

```powershell
dotnet build .\src\SpireMindMod\SpireMindMod.csproj -c Release

.\scripts\deploy_mod.ps1 `
  -ModsDir "I:\SteamLibrary\steamapps\common\Slay the Spire 2\mods"

Start-Process "steam://rungameid/2868840"
```

## 7. 브리지 서버 실행

브리지가 꺼져 있으면 다음 명령으로 켠다.

```powershell
node .\bridge\spiremind_bridge.js --http-host 127.0.0.1 --http-port 17832
```

브리지를 백그라운드로 켜려면 다음 명령을 사용한다.

```powershell
Start-Process `
  -FilePath "node" `
  -ArgumentList @(".\bridge\spiremind_bridge.js", "--http-host", "127.0.0.1", "--http-port", "17832") `
  -WorkingDirectory "F:\Antigravity\STSAutoplay" `
  -WindowStyle Hidden
```

브리지 상태 확인:

```powershell
Invoke-RestMethod -Method Get -Uri "http://127.0.0.1:17832/health" -TimeoutSec 2
```

현재 게시된 상태 확인:

```powershell
Invoke-RestMethod -Method Get -Uri "http://127.0.0.1:17832/state/current" -TimeoutSec 2
```

## 8. 의사결정 루프 실행

휴리스틱으로 한 번만 판단하려면 다음 명령을 사용한다.

```powershell
node .\bridge\spiremind_decision_loop.js `
  --bridge-url "http://127.0.0.1:17832" `
  --mode heuristic `
  --once
```

실행 결과까지 기다리려면 다음 옵션을 붙인다.

```powershell
node .\bridge\spiremind_decision_loop.js `
  --bridge-url "http://127.0.0.1:17832" `
  --mode heuristic `
  --once `
  --wait-result
```

게임이 실제로 켜져 있고 모드가 상태를 게시한 뒤에 실행해야 한다. 오래된 상태가 브리지에 남아 있으면 엉뚱한 행동이 제출될 수 있다.

## 9. 게임 실행 확인

게임 프로세스가 떠 있는지 확인한다.

```powershell
Get-Process | Where-Object {
  $_.ProcessName -like "*Slay*" -or $_.ProcessName -like "*Spire*"
} | Select-Object Id, ProcessName, Path
```

상태 파일이 갱신되는지 확인한다.

```powershell
$statePath = Join-Path $env:APPDATA "SlayTheSpire2\SpireMind\combat_state.json"
Get-Item $statePath | Select-Object FullName, LastWriteTime, Length
Get-Content $statePath -Raw -Encoding UTF8
```

Godot 로그에서 SpireMind 로그를 확인한다.

```powershell
$logPath = Join-Path $env:APPDATA "SlayTheSpire2\logs\godot.log"
Get-Content $logPath -Tail 120 -Encoding UTF8 | Select-String "SpireMind"
```

## 10. 자주 생기는 문제

### Steam에서 실행하라는 오류가 뜬다

직접 exe 실행을 중단하고 Steam URI를 사용한다.

```powershell
Start-Process "steam://rungameid/2868840"
```

### 모드 초기화 오류가 뜬다

1. 게임을 종료한다.
2. `dotnet build -c Release`가 통과하는지 확인한다.
3. `SpireMind.dll`을 모드 폴더에 다시 배포한다.
4. 게임을 Steam URI로 다시 실행한다.
5. `godot.log`에서 `SpireMind` 오류를 확인한다.

### 브리지 상태가 오래됐다

게임이 꺼진 상태에서 합성 상태를 올렸거나 이전 상태가 남아 있을 수 있다. 게임을 다시 켜고 테스트 화면에 들어가서 새 상태가 게시되는지 확인한다.

```powershell
Invoke-RestMethod -Method Get -Uri "http://127.0.0.1:17832/health" -TimeoutSec 2
```

`current_state_id`와 `state_version`이 게임 진행에 맞게 바뀌어야 한다.

### 실행은 됐는데 자동 행동이 적용되지 않는다

다음을 순서대로 확인한다.

1. 브리지가 켜져 있는가
2. `combat_state.json`이 갱신되는가
3. 브리지 `/state/current`가 최신 상태인가
4. `legal_actions`에 실행할 행동이 있는가
5. 의사결정 루프가 행동을 제출했는가
6. 게임 쪽 실행기가 claim하고 결과를 보고했는가

최근 행동 확인:

```powershell
Invoke-RestMethod -Method Get -Uri "http://127.0.0.1:17832/action/latest" -TimeoutSec 2
```
## 방 단위 어댑터 검증

긴 런을 한 번에 돌리면 실패 원인이 출력에 묻히기 쉽다. 어댑터 검증은 먼저 방 단위 러너로 끊어서 본다.

```powershell
node .\bridge\spiremind_room_runner.js `
  --max-rooms 1 `
  --max-steps 8 `
  --label smoke
```

러너는 각 단계의 전후 상태, 마지막 행동, 실패 사유를 `logs\room_runner\<timestamp>\events.jsonl`과 `summary.json`에 남긴다.

중요한 해석 기준은 다음과 같다.

- `action_failed`: `legal_actions`에 있던 행동을 실행자가 처리하지 못했다. 어댑터 결함 후보로 본다.
- `action_stale`: 상태가 바뀌어 같은 행동을 더 이상 적용할 수 없다. 반복되면 전환 대기나 최신 상태 매칭을 점검한다.
- `terminal_transition`: 행동 결과로 `game_over` 같은 종료 상태에 도달했다. 실패가 아니라 종료 전환이다.
- `decision_timeout`: 판단 루프 또는 게임 실행 대기가 제한 시간을 넘겼다.
