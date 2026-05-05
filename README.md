# SpireMind

SpireMind는 Slay the Spire 2를 LLM 에이전트가 플레이하고, 그 판단 과정을 반복 가능한 벤치마크로 기록하기 위한 실험 프로젝트입니다.

목표는 단순히 자동으로 게임을 클리어하는 봇을 만드는 것이 아닙니다. 이 프로젝트는 LLM이 전투, 보상, 지도, 휴식, 상점, 이벤트 선택을 어떻게 해석하는지 관찰하고, 실패를 다음 시도에서 전략으로 전환하는지 측정합니다.

## 현재 초점

지금 단계의 핵심 질문은 네 가지입니다.

- LLM이 현재 게임 상태에서 합법 행동만 안정적으로 선택하는가?
- 선택한 행동이 실제 STS2 상태 변화로 이어지는가?
- 장기 진행에서 체력, 카드 보상, 유물, 경로 선택을 연결해서 판단하는가?
- 이전 런의 실패를 handoff로 압축하고 다음 런에서 실제 행동 변화로 반영하는가?

## 구성

```text
src/SpireMindMod/       STS2 안에서 상태 추출과 행동 실행을 담당하는 Godot/C# 모드
bridge/                 게임 상태와 행동 요청을 주고받는 로컬 브리지
scripts/                빌드, 배포, 런타임 점검, 벤치마크 실행 스크립트
benchmarks/             벤치마크 설정과 실행 결과
docs/                   설계 문서, 실행 가이드, 벤치마크 설계
logs/                   수동 실행 로그와 장기 테스트 로그
```

## 동작 흐름

```text
STS2 + SpireMind 모드
-> combat_state.json 및 HTTP 브리지로 현재 상태 export
-> 에이전트 실행기가 legal_actions 중 하나를 LLM에 요청
-> LLM은 selected_action_id 하나만 선택
-> 브리지가 행동을 검증하고 게임 안에서 실행
-> 실행 결과, 상태 변화, 판단 근거를 로그로 기록
```

LLM은 게임 객체를 직접 조작하지 않습니다. 항상 `legal_actions`에 포함된 `action_id`만 선택합니다. 실행 가능 여부, 대상 유효성, 에너지 부족, 오래된 상태 판단은 모드와 실행기가 검증합니다.

## 빠른 시작

### 1. 로컬 경로 설정

`src/SpireMindMod/SpireMind.Local.props.example`을 같은 폴더의 `SpireMind.Local.props`로 복사한 뒤, 로컬 STS2 경로를 채웁니다.

```xml
<Project>
  <PropertyGroup>
    <Sts2AssemblyPath>I:\SteamLibrary\steamapps\common\Slay the Spire 2\SlayTheSpire2_Data\Managed\sts2.dll</Sts2AssemblyPath>
    <Sts2GameDataPath>I:\SteamLibrary\steamapps\common\Slay the Spire 2\data_sts2_windows_x86_64</Sts2GameDataPath>
    <Sts2ModsDir>I:\SteamLibrary\steamapps\common\Slay the Spire 2\mods</Sts2ModsDir>
  </PropertyGroup>
</Project>
```

`SpireMind.Local.props`는 개인 PC 경로를 담기 때문에 Git에 올리지 않습니다.

### 2. 빌드

```powershell
.\scripts\build_mod.ps1
```

또는 직접 빌드할 수 있습니다.

```powershell
dotnet build .\src\SpireMindMod\SpireMindMod.csproj
```

### 3. 배포

```powershell
.\scripts\deploy_mod.ps1 -ModsDir "I:\SteamLibrary\steamapps\common\Slay the Spire 2\mods"
```

`SpireMind.Local.props`에 `Sts2ModsDir`가 있으면 `-ModsDir`를 생략할 수 있습니다.

### 4. 런타임 점검

게임과 브리지를 띄운 뒤 상태 export와 브리지 연결을 확인합니다.

```powershell
.\scripts\runtime_smoke_check.ps1 -StartBridge -CheckSeconds 30 -PollSeconds 1
```

고정 시드로 새 런을 시작하고, 진행 중인 런이 있으면 포기한 뒤 다시 시작하려면 다음 명령을 사용합니다.

```powershell
.\scripts\runtime_smoke_check.ps1 `
  -StartBridge `
  -StartSeededRun `
  -ForceAbandonRun `
  -Seed "7MJCUHEB5Q" `
  -CommandWaitSeconds 300
```

## 벤치마크 실행

### B0: 첫 전투 안정성

니오우 선택부터 첫 전투 종료까지 확인하는 짧은 테스트입니다. 관측, 행동 제출, 결과 대기, 로그 기록이 안정적인지 봅니다.

```powershell
node .\scripts\run_benchmark.js `
  --benchmark-dir .\benchmarks\B0_NEOW_FIRST_COMBAT `
  --decider llm_current `
  --seed seed_0001 `
  --repeat-index 1 `
  --bridge-url http://127.0.0.1:17832
```

### B2: 1막 장기 진행

같은 고정 시드에서 가능한 한 멀리 진행합니다. 종료 조건은 `game_over`, `run_finished`, 또는 최대 판단 횟수입니다.

```powershell
node .\scripts\run_benchmark.js `
  --benchmark-dir .\benchmarks\B2_ACT1_PUSH `
  --decider llm_current `
  --seed seed_0001 `
  --repeat-index 1 `
  --bridge-url http://127.0.0.1:17832 `
  --max-runtime-ms 3600000
```

최근 장기 실행에서는 같은 시드 `7MJCUHEB5Q`로 1막 후반 `r16` 전투까지 도달한 뒤 `game_over`가 기록되었습니다. 실행 안정성 측면에서는 `invalid_action`, `executor_failed`, `timeout`이 0으로 유지되었고, 전략 측면에서는 후반 대형 단일 적 전투에서 체력 관리가 핵심 실패 원인으로 드러났습니다.

## 벤치마크 설계 방향

SpireMind의 벤치마크는 승패만 보지 않습니다. 지금은 아래 계층으로 확장하고 있습니다.

| ID | 목적 | 핵심 질문 |
| --- | --- | --- |
| B0 | 첫 전투 안정성 | 상태 추출과 행동 실행이 반복 가능한가? |
| B1 | 단일 런 장기 진행 | 한 번의 런에서 어디까지 갈 수 있는가? |
| B2 | 같은 시드 반복 | 무기억 반복에서 결과가 어떻게 흔들리는가? |
| B3 | handoff 학습 | 이전 실패가 다음 런 행동 규칙으로 바뀌는가? |
| B4 | 엘리트 가치 판단 | 위험한 엘리트 노드를 조건부 투자로 이해하는가? |
| B5 | 모듈형 판단 비교 | 단일 프롬프트보다 상황별 프롬프트가 안정적인가? |

handoff는 자유 감상문이 아니라 다음 런에 쓸 수 있는 구조화된 전략 메모로 다룹니다.

```json
{
  "schema_version": "handoff.v1",
  "run_summary": {},
  "diagnosis": [],
  "next_run_rules": [],
  "experiment": {},
  "free_note": ""
}
```

특히 `next_run_rules`는 조건, 행동, 이유를 반드시 포함해야 합니다. 그래야 다음 런에서 규칙 이행률을 측정할 수 있습니다.

## 주요 문서

- [문서 색인](./docs/README.md)
- [벤치마크 설계](./docs/benchmark_design.md)
- [벤치마크 구현 계획](./docs/benchmark_implementation_plan.md)
- [자동 테스트 명령](./docs/autotest_commands.md)
- [브리지 구조](./docs/bridge_architecture.md)
- [의사결정 루프](./docs/decision_loop.md)
- [Codex 판단기](./docs/codex_decider.md)
- [런 메모리와 로그](./docs/run_memory_logging.md)
- [게임 실행 가이드](./docs/game_launch_guide.md)
- [세션 handoff](./docs/session_handoff.md)

## 개발 원칙

- 게임 상태는 가능한 한 구조화된 JSON으로 기록합니다.
- LLM 입력과 출력은 모두 재현 가능한 로그로 남깁니다.
- 실행 실패와 전략 실패를 분리해서 해석합니다.
- 같은 시드와 같은 설정에서는 결과 비교가 가능해야 합니다.
- 벤치마크는 “잘했는가”보다 먼저 “왜 그렇게 판단했는가”를 드러내야 합니다.

## 현재 리스크

- 일부 문서와 로그에 과거 인코딩 깨짐이 남아 있습니다.
- 장기 테스트는 아직 handoff 자동 생성과 규칙 이행률 채점이 완성되지 않았습니다.
- `stale_state`는 장기 실행에서 누적될 수 있어, 전략 실패와 구분해서 해석해야 합니다.
- 엘리트 가치 판단은 아직 별도 벤치마크로 분리되지 않았습니다.
