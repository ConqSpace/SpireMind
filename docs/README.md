# 문서 색인

이 폴더는 SpireMind의 설계, 실행 방법, 벤치마크 기준을 정리합니다. 처음 보는 사람은 먼저 [루트 README](../README.md)를 읽는 편이 좋습니다.

## 핵심 문서

- [benchmark_design.md](./benchmark_design.md): 단기/장기 벤치마크의 목적, 측정 지표, 통과 기준
- [benchmark_implementation_plan.md](./benchmark_implementation_plan.md): 벤치마크 실행기, 설정 파일, 종료 조건, 요약 파일 설계
- [autotest_commands.md](./autotest_commands.md): 게임 안에서 새 런 시작, 고정 시드 적용, 진행 중 런 포기 명령을 처리하는 방식
- [bridge_architecture.md](./bridge_architecture.md): STS2 모드, 로컬 HTTP 브리지, MCP 프록시의 역할
- [decision_loop.md](./decision_loop.md): 상태 관측, LLM 판단, 행동 제출, 결과 대기 흐름
- [codex_decider.md](./codex_decider.md): Codex 기반 판단기 연결 방식
- [run_memory_logging.md](./run_memory_logging.md): 런 로그, 판단 기록, 메모리 요약 설계
- [game_launch_guide.md](./game_launch_guide.md): STS2 실행과 로컬 점검 절차
- [session_handoff.md](./session_handoff.md): 현재 세션의 구현 상태와 다음 작업 인수인계

## 상태와 행동 스키마

- [state_schema.md](./state_schema.md): LLM에 전달하는 게임 상태 JSON 형식
- [action_schema.md](./action_schema.md): LLM이 선택할 수 있는 행동 목록과 응답 형식
- [action_batch_schema.md](./action_batch_schema.md): 여러 행동 후보를 묶어 다루는 형식
- [action_execution_design.md](./action_execution_design.md): 행동 검증과 실행 설계
- [failure_policy.md](./failure_policy.md): 잘못된 판단, 오래된 상태, 실행 실패를 처리하는 기준

## 어댑터와 런타임 설계

- [adapter_design_notes.md](./adapter_design_notes.md): STS2 런타임 상태를 안정적으로 읽기 위한 설계 메모
- [adapter_implementation_roadmap.md](./adapter_implementation_roadmap.md): 어댑터 구현 단계와 남은 과제
- [runtime_state_adapter_rebuild.md](./runtime_state_adapter_rebuild.md): 런타임 상태 어댑터 재구성 계획
- [card_selection_bridge_design.md](./card_selection_bridge_design.md): 카드 선택 화면 브리지 설계
- [persistent_agent_daemon.md](./persistent_agent_daemon.md): 장기 실행 에이전트 데몬 설계

## 카드, 포션, 전투 참고 문서

- [ironclad_card_effect_reference.md](./ironclad_card_effect_reference.md): 아이언클래드 카드 효과 참고
- [ironclad_card_execution_classification.md](./ironclad_card_execution_classification.md): 아이언클래드 카드 실행 방식 분류
- [ironclad_followup_reference_implementation.md](./ironclad_followup_reference_implementation.md): 후속 카드 실행 참고 구현
- [potion_reference_logic_review.md](./potion_reference_logic_review.md): 포션 처리 로직 검토

## 로드맵과 실험 계획

- [development_roadmap.md](./development_roadmap.md): 구현 단계와 검증 기준
- [strategic_roadmap.md](./strategic_roadmap.md): 장기 연구 방향과 전략 평가 기준
- [scenario_framework.md](./scenario_framework.md): SpireMind를 시나리오 평가 도구로 확장하는 구조
- [experiment_protocol.md](./experiment_protocol.md): 실험 조건, 반복 수, 비교 기준
- [ai_teammate_gap_roadmap.md](./ai_teammate_gap_roadmap.md): AI 동료형 플레이어로 확장할 때의 차이와 과제

## 정리 원칙

- 루트 README는 프로젝트의 현재 기준 문서입니다.
- 이 폴더의 문서는 세부 설계와 실행 절차를 담당합니다.
- 오래된 문서에 남은 인코딩 깨짐은 순차적으로 정리합니다.
- 실행 결과와 전략 해석은 분리해서 기록합니다.
