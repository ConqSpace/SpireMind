# SpireMindMod

이 폴더는 R1 단계의 STS2 C# 모드 골격입니다. 지금 목표는 전투 자동화가 아니라, STS2 로더가 `SpireMind.json` manifest를 발견하고 `SpireMind.dll`을 로드하는지 확인하는 것입니다.

## 포함하는 것

- `SpireMindMod.csproj`: `SpireMind.dll`을 만드는 C# 프로젝트
- `SpireMind.json`: 기존 STS2 모드 manifest 형식을 기준으로 맞춘 최소 manifest 초안
- `SpireMindEntrypoint.cs`: 나중에 로더 진입점 규칙이 확정되면 연결할 수 있는 placeholder
- `SpireMind.Local.props.example`: 사용자 PC 경로 예시

## 포함하지 않는 것

- STS2 원본 파일
- `sts2.dll`
- 로컬 설치 경로가 들어간 실제 `SpireMind.Local.props`
- 실제 `SpireMind.pck` 산출물

## 로컬 설정

1. `SpireMind.Local.props.example`을 `SpireMind.Local.props`로 복사합니다.
2. `Sts2AssemblyPath`와 `Sts2ModsDir`를 자신의 STS2 설치 경로에 맞게 수정합니다.
3. `SpireMind.Local.props`는 커밋하지 않습니다.

`Sts2AssemblyPath`가 있으면 프로젝트가 `sts2.dll`을 참조합니다. 없으면 참조 없이 빌드 가능한 읽기 전용 골격으로 동작합니다.

STS2 런타임은 .NET 9 계열 어셈블리를 포함할 수 있습니다. 그러나 R1 프로젝트는 현재 저장소 검증 가능성을 우선해 `net8.0`으로 빌드합니다.

## BaseLib/Harmony 결정

R1에서는 BaseLib와 Harmony를 사용하지 않습니다. 지금 목표는 모드가 로드되는지 확인하는 것입니다. 런타임 패치나 게임 내부 메서드 가로채기가 필요해지는지는 R2에서 전투 상태 접근 지점을 확인한 뒤 다시 판단합니다.

이 결정은 두 가지 위험을 줄입니다.

- STS2 앞서 해보기 업데이트와 외부 라이브러리 버전 불일치가 동시에 터지는 상황을 피합니다.
- 첫 모드가 게임 상태를 바꾸지 않는 읽기 전용 골격이라는 범위를 지킵니다.

STS2 core의 `ModInitializerAttribute`를 쓰는 `ModEntry`는 R1에 포함하지 않습니다. 현재 로컬 환경에는 .NET 9 SDK가 없고, STS2 v0.103.2의 `sts2.dll`이 `System.Runtime 9.0`을 참조해 `net8.0` 빌드와 충돌하기 때문입니다. 모드 자체 initializer 로그는 .NET 9 SDK 도입 또는 대상 프레임워크 조정 결정을 한 뒤 별도 작업으로 처리합니다.

## 현재 검증된 로드 사실

- STS2 로더 로그(`C:\Users\jye00\AppData\Roaming\SlayTheSpire2\logs\godot.log`)에 SpireMind manifest 발견, DLL 로드, 초기화 완료가 남습니다.
- `SpireMindEntrypoint`는 R1 placeholder입니다. 현재 STS2 로더가 이 타입을 직접 호출한다고 검증되지 않았습니다.
- 모드 자체 `ModEntry` 로그는 .NET 9 SDK 도입 또는 대상 프레임워크 조정 결정 전까지 보류합니다.

## Manifest 형식

`SpireMind.json`은 로컬 기존 STS2 모드에서 쓰는 것으로 보고된 `author`, `has_pck`, `has_dll`, `dependencies`, `affects_gameplay` 필드를 기준으로 작성했습니다. 아직 로더의 진입점 규칙을 확인하지 못했기 때문에 `entrypoint` 같은 임의 확장 필드는 넣지 않습니다.

## 앞서 해보기 위험

STS2는 앞서 해보기 상태라서 모드 로더 구조, manifest 형식, `sts2.dll`의 공개 API가 바뀔 수 있습니다. 그래서 R1 모드는 게임 상태를 바꾸지 않습니다. 먼저 로더 수준의 manifest 인식과 DLL 로드 여부를 확인하고, R2에서 실제 전투 상태 접근 지점을 별도로 검증합니다.
