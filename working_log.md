# Working Log

## 2026-09-02 10:41 KST - 예제 프로젝트를 samples 디렉토리로 이동

- ExampleSectorServer, ExampleMmorpgServer, ExampleConsoleApp, ExampleChatServer, AdvancedMmorpgTests, AdvancedMmorpgServer, AdvancedMmorpgClient 7개 프로젝트를 `samples/` 밑으로 이동 (`git mv`).
- 이동한 프로젝트들의 `.csproj`/`.sln`에서 `JobDispatcherNET.csproj`를 가리키는 `ProjectReference` 상대 경로를 `..\` 한 단계 더 추가해 수정.
- `All.sln`의 프로젝트 경로를 `samples\...`로 갱신하고, 기존 samples 솔루션 폴더(PipelinesServer 등과 동일하게) 밑에 nest 되도록 `NestedProjects` 항목 추가.
- `.editorconfig`의 `AdvancedMmorpgTests` glob 경로, `README.md`/`README.ko.md`의 샘플 표와 `dotnet run` 예시, `AdvancedMmorpgServer`/`AdvancedMmorpgClient`의 `README.md` 내 `cd` 경로를 새 위치에 맞게 수정.
- `dotnet build All.sln` 로 전체 빌드 확인 (경고/오류 0개).
