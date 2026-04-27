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
