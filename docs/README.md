# STS2 AI Runner 기획 문서

이 문서는 Slay the Spire 2를 LLM에게 플레이시키는 실험 모드의 1차 기획을 정리한다.

목표는 플레이어 보조가 아니다. 목표는 AI가 STS2의 전투와 장기 선택을 얼마나 잘 처리하는지 측정하는 것이다. 그래서 UI보다 상태 추출, 행동 제한, 로그, 반복 가능한 실험 조건을 먼저 잠근다.

## 1차 목표

- 전투 상태를 JSON으로 안정적으로 추출한다.
- 모드가 실행 가능한 행동 목록을 만든다.
- LLM은 행동 목록 중 하나의 `action_id`만 선택한다.
- 모드는 응답을 검증한 뒤 실행한다.
- 모든 입력, 응답, 결과를 로그로 남긴다.

## 문서 목록

- [state_schema.md](./state_schema.md): STS2에서 추출해 LLM에 전달할 상태 JSON 형식
- [action_schema.md](./action_schema.md): LLM이 선택할 수 있는 행동 목록과 응답 형식
- [experiment_protocol.md](./experiment_protocol.md): 성능 측정 조건, 지표, 비교 기준
- [scenario_framework.md](./scenario_framework.md): SpireMind를 STS2 AI 시나리오 평가 도구로 다루기 위한 상위 설계
- [failure_policy.md](./failure_policy.md): 잘못된 응답, 지연, 상태 변경, 예외 처리 규칙
- [development_roadmap.md](./development_roadmap.md): 구현 단계, 산출물, 검증 기준, 주요 위험
- [strategic_roadmap.md](./strategic_roadmap.md): 연구 방향, 장기 단계, 의사결정 지점
- [decision_loop.md](./decision_loop.md): 브리지 상태를 읽고 행동 묶음을 제출하는 의사결정 루프
- [run_memory_logging.md](./run_memory_logging.md): 전투 로그, 런 로그, 판단 기록, LLM 기억 요약 설계

## 현재 잠글 범위

- 단일 플레이 전투
- 현재 플레이어 1명 기준
- 손패, 뽑을 카드 더미, 버려진 카드 더미, 소멸된 카드 더미
- 플레이어 체력, 방어도, 에너지, 버프, 디버프
- 적 체력, 방어도, 버프, 디버프, 의도
- 유물
- 합법 행동 목록
- 턴별 로그

## 나중에 열어둘 범위

- 보상 선택
- 지도 경로 선택
- 상점 구매
- 휴식/강화 선택
- 이벤트 선택
- 협동 모드
- 장기 기억
- 강화학습 정책과의 비교

## 구현 원칙

- LLM은 게임 객체를 직접 조작하지 않는다.
- LLM은 항상 `legal_actions` 안의 `action_id`만 고른다.
- 카드 사용 가능 여부, 대상 가능 여부, 에너지 부족 여부는 모드가 판단한다.
- 같은 입력 JSON과 같은 모델 설정이면 같은 실험을 다시 실행할 수 있어야 한다.
- 실패한 응답도 버리지 않는다. 실패 자체가 실험 결과다.

## 로컬 빌드 설정

- `src/SpireMindMod/SpireMind.Local.props.example`을 같은 폴더의 `SpireMind.Local.props`로 복사한다.
- `Sts2AssemblyPath`는 로컬 STS2 `sts2.dll` 경로로 설정한다.
- `Sts2GameDataPath`는 로컬 STS2 `data_sts2_windows_x86_64` 폴더로 설정한다. 이 값으로 `GodotSharp.dll` 참조를 찾는다.
- `SpireMind.Local.props`는 개인 PC 경로를 담기 때문에 Git에 커밋하지 않는다.
