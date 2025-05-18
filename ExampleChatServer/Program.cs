namespace ExampleChatServer;


internal class Program
{
    public static async Task Main(string[] args)
    {
        Console.WriteLine("===== JobDispatcherNET을 활용한 채팅 서버 예제 =====");

        // 채팅 서버 생성 및 시작
        var chatServer = new ChatServer(workerCount: 4);
        await chatServer.StartAsync();

        // 테스트용 가상 클라이언트 생성
        var clients = new List<ChatNetworkClient>
        {
            new("user1", "철수"),
            new("user2", "영희"),
            new("user3", "민수"),
            new("user4", "지영")
        };

        // 클라이언트 연결
        foreach (var client in clients)
        {
            chatServer.HandleUserConnect(client);
        }

        // 잠시 대기
        await Task.Delay(500);

        // 채팅방 입장
        chatServer.HandleRoomJoin("user1", "general");
        chatServer.HandleRoomJoin("user2", "general");
        chatServer.HandleRoomJoin("user3", "game");
        chatServer.HandleRoomJoin("user4", "game");
        chatServer.HandleRoomJoin("user1", "dev");
        chatServer.HandleRoomJoin("user3", "dev");

        await Task.Delay(500);

        // 방 채팅 메시지 전송
        chatServer.HandleRoomChat("user1", "general", "안녕하세요, 모두들!");
        chatServer.HandleRoomChat("user2", "general", "반갑습니다, 철수님!");
        chatServer.HandleRoomChat("user3", "game", "게임 하실 분?");
        chatServer.HandleRoomChat("user4", "game", "저요!");
        chatServer.HandleRoomChat("user1", "dev", "개발 관련 질문 있습니다.");

        await Task.Delay(500);

        // 1:1 채팅 메시지
        chatServer.HandlePrivateChat("user1", "user2", "안녕하세요, 영희님. 개인적으로 말씀드리고 싶은 게 있어요.");
        chatServer.HandlePrivateChat("user2", "user1", "네, 말씀하세요.");

        // 쪽지 보내기
        chatServer.HandleInstantMessage("user3", "user4", "게임 서버 IP를 알려드립니다: 192.168.1.100");

        await Task.Delay(500);

        // 서버 상태 출력
        chatServer.PrintStatus();

        // 채팅방 퇴장
        chatServer.HandleRoomLeave("user1", "general");
        chatServer.HandleRoomLeave("user3", "game");

        await Task.Delay(500);

        // 사용자 접속 종료
        chatServer.HandleUserDisconnect("user4");

        await Task.Delay(1000);

        // 최종 서버 상태 출력
        chatServer.PrintStatus();

        Console.WriteLine("\n아무 키나 누르면 서버를 종료합니다...");
        Console.ReadKey();

        // 서버 종료
        await chatServer.StopAsync();
    }
}


/*
1. Room 객체의 독립성 강화:
   - Room 클래스가 AsyncExecutable을 상속받아 자체적으로 비동기 작업을 처리합니다.
   - 각 Room은 자신만의 작업 큐를 가지며, 자신의 상태를 안전하게 관리합니다.
   - 한 Room의 작업이 다른 Room에 영향을 주지 않아 확장성이 향상됩니다.

2. lock 사용 최소화:
   - Room 객체 내부에서는 AsyncExecutable의 메시지 패싱 메커니즘을 사용하여 lock 없이도 스레드 안전성을 확보합니다.
   - 모든 Room 작업은 해당 Room의 작업 큐를 통해 처리되어 동시성 이슈를 방지합니다.
   - ChatServer는 주로 _roomsLock만 사용하여 Room 컬렉션 접근을 동기화합니다.

3. 명확한 책임 분리:
   - Room은 자신의 멤버와 메시지 관리를 책임집니다.
   - ChatServer는 Room 생성/관리와 유저 관리를 담당합니다.
   - 이 분리는 코드를 더 모듈화하고 유지보수하기 쉽게 만듭니다.

4. 작업 처리 방식 변경:
   - Room과 관련된 모든 작업(입장, 퇴장, 채팅)은 Room의 DoAsync를 통해 처리됩니다.
   - 이는 Room 내부 상태가 항상 일관성을 유지하도록 보장합니다.
   - 여러 스레드에서 동시에 동일한 Room에 접근해도 항상 안전하게 작동합니다.

5. 성능 향상:
   - 각 Room은 독립적으로 실행되므로 한 Room의 작업량이 많아도 다른 Room에 영향을 주지 않습니다.
   - 서버 규모가 커져도 Room별로 분산 처리되어 확장성이 좋습니다.
   - lock 경합이 줄어들어 고성능 처리가 가능합니다.

이 구현은 JobDispatcherNET의 장점을 최대한 활용하여 각 Room이 독립적인 처리 단위로 동작하게 하면서, 서버 전체적으로는 일관된 처리가 가능하도록 설계되었습니다. 사용자가 증가하더라도 Room 별로 작업이 분산되어 처리되기 때문에 확장성이 뛰어납니다.
*/