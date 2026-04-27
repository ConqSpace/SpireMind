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

Codex가 붙는 MCP 서버는 `bridge/spiremind_mcp_proxy.js`다. 이 프록시는 자체 HTTP 서버를 열지 않고, `bridge/spiremind_bridge.js`의 HTTP 서버로만 요청을 전달한다.

실행과 Codex MCP 등록 방법은 [bridge_architecture.md](../docs/bridge_architecture.md)를 따른다.

## 브리지 전송

`runtime_smoke_check.ps1`는 여전히 `combat_state.json`이 생성되고 갱신되는지 확인합니다. 이제 exporter는 같은 JSON을 로컬 브리지에도 보낼 수 있습니다.

- 기본 브리지 주소는 `http://127.0.0.1:17832`입니다.
- 전송 설정 파일은 `%APPDATA%\SlayTheSpire2\SpireMind\bridge_config.json`입니다.
- 전송을 끄려면 설정 파일의 `enabled`를 `false`로 두거나 `SPIREMIND_BRIDGE_ENABLED=false` 환경 변수를 사용합니다.
- 브리지 주소를 바꾸려면 설정 파일의 `state_url` 또는 `SPIREMIND_BRIDGE_STATE_URL` 환경 변수를 사용합니다.
- 브리지 전송 실패는 게임 실행을 멈추게 하지 않습니다.

## Codex 등록 예시

브리지와 분리된 MCP 프록시를 Codex에 등록한다.

```powershell
codex mcp add spiremind-bridge -- node F:\Antigravity\STSAutoplay\bridge\spiremind_mcp_proxy.js --bridge-url http://127.0.0.1:17832
```

환경 변수로 기본 주소를 바꿀 수도 있다.

```powershell
$env:SPIREMIND_BRIDGE_URL = "http://127.0.0.1:17832"
codex mcp add spiremind-bridge -- node F:\Antigravity\STSAutoplay\bridge\spiremind_mcp_proxy.js
```

## 브리지 프록시 검사

HTTP 브리지와 MCP 프록시 왕복을 한 번에 확인한다.

```powershell
.\scripts\bridge_proxy_smoke_check.ps1
```

이미 떠 있는 브리지만 사용하고 싶을 때:

```powershell
.\scripts\bridge_proxy_smoke_check.ps1 -UseExistingBridge
```
