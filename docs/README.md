# JobDispatcherNET 문서

## 아키텍처 가이드

[architecture.html](architecture.html)을 브라우저에서 열면 인터랙티브 애니메이션과 함께 라이브러리 전체 구조를 확인할 수 있습니다.

### 가이드 구성
1. 이 라이브러리는 무엇인가?
2. 어떤 문제를 해결하는가? (lock vs JobDispatcher 비교)
3. 핵심 클래스 6개 (SVG 관계도)
4. 작업 흐름 애니메이션 (5단계 인터랙티브)
5. DoTask 내부 동작 (3가지 시나리오별 단계 애니메이션)
6. 타이머 DoAsyncAfter (타임라인 애니메이션)
7. 실전 패턴 — MMORPG 서버 (이동/근접/AoE)
8. 한눈에 보는 요약

## 예제 프로젝트

| 프로젝트 | 설명 |
|---|---|
| `ExampleConsoleApp` | 기본 사용법, 워커 스레드, 데이터 처리 |
| `ExampleChatServer` | 멀티 채팅방 서버 (Room별 AsyncExecutable) |
| `ExampleMmorpgServer` | MMORPG 서버 — 플레이어 Actor 패턴, 단일 존 병렬 처리 |
