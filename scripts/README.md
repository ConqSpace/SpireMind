# scripts

R1 단계의 빌드와 배포 보조 스크립트입니다. STS2 설치 경로는 저장소에 고정하지 않습니다.

STS2 런타임은 .NET 9 계열 어셈블리를 포함할 수 있습니다. R1 프로젝트는 현재 저장소 검증 가능성을 우선해 `net8.0`으로 빌드합니다.

## 빌드

```powershell
.\scripts\build_mod.ps1
```

## 배포

```powershell
.\scripts\deploy_mod.ps1 -ModsDir "C:\path\to\Slay the Spire 2\mods"
```

`src/SpireMindMod/SpireMind.Local.props`에 `Sts2ModsDir`를 설정하면 `-ModsDir`를 생략할 수 있습니다.

`SpireMind.pck`가 필요한 모드 로더라면 Godot에서 만든 산출물을 `-PckPath`로 넘깁니다. 이 저장소에는 STS2 원본 파일이나 로컬 pck 산출물을 커밋하지 않습니다.

## R2 런타임 반자동 검사

`runtime_smoke_check.ps1`는 R2 전투 상태 exporter가 실제 런타임에서 `combat_state.json`을 생성하고 갱신하는지 확인합니다. 스크립트는 UI 자동 클릭, 마우스 조작, 키 입력을 하지 않습니다. 사용자가 직접 게임에서 전투에 들어가 카드 1장을 사용하고 턴 종료까지 진행해야 합니다.

기본 감시 경로는 다음과 같습니다.

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

`-LaunchGame`의 기본 실행 방식은 Steam URL입니다. 즉, 기본값은 `-LaunchMode Steam`이며 `steam://rungameid/2868840`을 실행합니다. Steam을 거치지 않고 exe를 직접 실행해야 하는 확인에는 `-LaunchMode Exe`를 명시합니다.

```powershell
.\scripts\runtime_smoke_check.ps1 -LaunchGame -LaunchMode Exe -Sts2ExePath "I:\SteamLibrary\steamapps\common\Slay the Spire 2\SlayTheSpire2.exe"
```

안전하게 실행 계획만 확인:

```powershell
.\scripts\runtime_smoke_check.ps1 -Build -Deploy -LaunchGame -WhatIf
.\scripts\runtime_smoke_check.ps1 -Help
```
