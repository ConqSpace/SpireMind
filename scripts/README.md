# scripts

이 폴더에는 SpireMind의 빌드, 배포, 런타임 검증 스크립트가 들어 있다.

## 빌드

```powershell
.\scripts\build_mod.ps1
```

기본값은 `src/SpireMindMod/SpireMindMod.csproj`와 `Release` 구성이다.

## 배포

```powershell
.\scripts\deploy_mod.ps1 -ModsDir "C:\path\to\Slay the Spire 2\mods"
```

`src/SpireMindMod/SpireMind.Local.props`에 `Sts2ModsDir`를 설정하면 `-ModsDir`를 생략할 수 있다.

`SpireMind.pck`가 필요한 모드 로더라면 Godot에서 만든 산출물을 `-PckPath`로 넘긴다. 이 저장소에는 STS2 원본 파일이나 로컬 pck 산출물을 커밋하지 않는다.

## R2/R3 런타임 반자동 검사

`runtime_smoke_check.ps1`는 전투 상태 exporter가 실제 런타임에서 `combat_state.json`을 생성하고 갱신하는지 확인한다.

스크립트는 UI 자동 클릭, 마우스 조작, 키 입력을 하지 않는다. 사용자가 직접 게임에서 전투에 들어가 카드 1장을 사용하고 턴 종료까지 진행해야 한다.

기본 감시 경로는 다음과 같다.

- `godot.log`: `%APPDATA%\SlayTheSpire2\logs\godot.log`
- `combat_state.json`: `%APPDATA%\SlayTheSpire2\SpireMind\combat_state.json`

검사만 실행:

```powershell
.\scripts\runtime_smoke_check.ps1 -CheckSeconds 120 -PollSeconds 5
```

빌드, 배포, 게임 실행까지 함께 수행:

```powershell
.\scripts\runtime_smoke_check.ps1 -Build -Deploy -LaunchGame -ModsDir "I:\SteamLibrary\steamapps\common\Slay the Spire 2\mods"
```

`-LaunchGame`의 기본 실행 방식은 Steam URL이다. 즉, 기본값은 `-LaunchMode Steam`이며 `steam://rungameid/2868840`을 실행한다. Steam을 거치지 않고 exe를 직접 실행해야 하는 확인에는 `-LaunchMode Exe`를 명시한다.

```powershell
.\scripts\runtime_smoke_check.ps1 -LaunchGame -LaunchMode Exe -Sts2ExePath "I:\SteamLibrary\steamapps\common\Slay the Spire 2\SlayTheSpire2.exe"
```

안전하게 실행 계획만 확인:

```powershell
.\scripts\runtime_smoke_check.ps1 -Build -Deploy -LaunchGame -WhatIf
.\scripts\runtime_smoke_check.ps1 -Help
```

## R4 브리지

상주 브리지 서버는 `bridge/spiremind_bridge.js`에 있다.

실행과 Codex MCP 등록 방법은 [bridge_architecture.md](../docs/bridge_architecture.md)를 따른다.
