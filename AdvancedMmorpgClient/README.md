# AdvancedMmorpgClient

`AdvancedMmorpgServer`에 부하를 거는 MonoGame 기반 더미 봇 클라이언트.  
한 프로세스에서 N개의 봇을 동시에 띄우며, 모든 엔티티를 부감 시점으로 한 화면에 표시합니다.

전체 사용법(설정·실행 절차·패킷 프로토콜·부하 시나리오)은 서버 측 통합 문서를 참고하세요.

→ [`../AdvancedMmorpgServer/README.md`](../AdvancedMmorpgServer/README.md)

## 빠른 실행

```bash
# 1) 서버 먼저
cd AdvancedMmorpgServer && dotnet run

# 2) 클라이언트 (별도 터미널)
cd AdvancedMmorpgClient && dotnet run
```

`clientconfig.json`에서 봇 수(`bots.count`), tick 주기(`bots.tickIntervalMs`), 화면 크기 등을 조정합니다.  
ESC 키로 종료. 자기 봇은 노란 외곽선으로 표시됩니다.
