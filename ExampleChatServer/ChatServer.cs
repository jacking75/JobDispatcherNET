using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using JobDispatcherNET;

namespace ExampleChatServer;

// 채팅 서버 클래스
public class ChatServer : AsyncExecutable
{
    private readonly Dictionary<string, User> _users = [];
    private readonly Dictionary<string, Room> _rooms = [];
    private readonly ReaderWriterLockSlim _usersLock = new();
    private readonly object _roomsLock = new(); // 방 컬렉션에만 락 사용
    private readonly int _workerCount;
    private JobDispatcher<ChatWorker>? _dispatcher;

    public ChatServer(int workerCount = 4)
    {
        _workerCount = workerCount;
    }

    // 서버 시작
    public async Task StartAsync()
    {
        Console.WriteLine("채팅 서버를 시작합니다...");

        // 기본 채팅방 생성
        CreateDefaultRooms();

        // JobDispatcher 시작
        _dispatcher = new JobDispatcher<ChatWorker>(_workerCount);

        // 비동기로 워커 스레드 실행
        _ = Task.Run(async () => await _dispatcher.RunWorkerThreadsAsync());

        Console.WriteLine($"채팅 서버가 {_workerCount}개의 워커로 시작되었습니다.");
    }

    // 서버 종료
    public async Task StopAsync()
    {
        Console.WriteLine("채팅 서버를 종료합니다...");

        // 모든 방에서 사용자 강제 퇴장
        lock (_roomsLock)
        {
            foreach (var room in _rooms.Values)
            {
                room.ForceRemoveAllUsers();
            }
        }

        if (_dispatcher is not null)
        {
            await _dispatcher.DisposeAsync();
        }

        Console.WriteLine("채팅 서버가 종료되었습니다.");
    }

    // 기본 채팅방 생성
    private void CreateDefaultRooms()
    {
        lock (_roomsLock)
        {
            _rooms["general"] = new Room("일반 채팅");
            _rooms["game"] = new Room("게임 채팅");
            _rooms["dev"] = new Room("개발자 채팅");
        }
    }

    #region 사용자 관리

    // 사용자 접속 처리
    public void HandleUserConnect(IChatClient client)
    {
        DoAsync(() => ProcessUserConnect(client));
    }

    private void ProcessUserConnect(IChatClient client)
    {
        Console.WriteLine($"사용자 접속: {client.Username} ({client.UserId})");

        var user = new User(client);

        _usersLock.EnterWriteLock();
        try
        {
            _users[user.UserId] = user;
        }
        finally
        {
            _usersLock.ExitWriteLock();
        }

        // 모든 사용자에게 새 사용자 접속 알림
        BroadcastSystemMessage(
            MessageType.UserConnect,
            $"{user.Username}님이 접속하셨습니다.",
            null);
    }

    // 사용자 접속 종료 처리
    public void HandleUserDisconnect(string userId)
    {
        DoAsync(() => ProcessUserDisconnect(userId));
    }

    private void ProcessUserDisconnect(string userId)
    {
        User? user = null;

        _usersLock.EnterUpgradeableReadLock();
        try
        {
            if (_users.TryGetValue(userId, out user))
            {
                _usersLock.EnterWriteLock();
                try
                {
                    _users.Remove(userId);
                }
                finally
                {
                    _usersLock.ExitWriteLock();
                }
            }
        }
        finally
        {
            _usersLock.ExitUpgradeableReadLock();
        }

        if (user is not null)
        {
            Console.WriteLine($"사용자 접속 종료: {user.Username} ({user.UserId})");

            // 참여 중인 모든 방에서 퇴장 처리
            var roomIds = new List<string>(user.JoinedRoomIds);
            foreach (var roomId in roomIds)
            {
                // 방의 DoAsync를 사용하여 방 내부 스레드에서 안전하게 처리
                Room? room = GetRoom(roomId);
                if (room != null)
                {
                    room.RemoveUser(userId);
                }
            }

            // 모든 사용자에게 접속 종료 알림
            BroadcastSystemMessage(
                MessageType.UserDisconnect,
                $"{user.Username}님이 접속을 종료하셨습니다.",
                null);
        }
    }

    #endregion

    #region 채팅방 관리

    // 방 가져오기
    private Room? GetRoom(string roomId)
    {
        lock (_roomsLock)
        {
            return _rooms.TryGetValue(roomId, out var room) ? room : null;
        }
    }

    // 채팅방 입장 처리
    public void HandleRoomJoin(string userId, string roomId)
    {
        DoAsync(() => ProcessRoomJoin(userId, roomId));
    }

    private void ProcessRoomJoin(string userId, string roomId)
    {
        User? user = null;
        Room? room = GetRoom(roomId);

        _usersLock.EnterReadLock();
        try
        {
            _users.TryGetValue(userId, out user);
        }
        finally
        {
            _usersLock.ExitReadLock();
        }

        if (user is not null && room is not null)
        {
            // Room의 DoAsync를 사용하여 이 작업을 Room의 스레드에서 처리
            room.AddUser(user);

            // 사용자의 참여 방 목록 업데이트
            user.JoinedRoomIds.Add(roomId);
        }
    }

    // 채팅방 퇴장 처리
    public void HandleRoomLeave(string userId, string roomId)
    {
        DoAsync(() => ProcessRoomLeave(userId, roomId));
    }

    private void ProcessRoomLeave(string userId, string roomId)
    {
        User? user = null;
        Room? room = GetRoom(roomId);

        _usersLock.EnterReadLock();
        try
        {
            _users.TryGetValue(userId, out user);
        }
        finally
        {
            _usersLock.ExitReadLock();
        }

        if (user is not null && room is not null)
        {
            // Room의 DoAsync를 사용하여 이 작업을 Room의 스레드에서 처리
            room.RemoveUser(userId);

            // 사용자의 참여 방 목록 업데이트
            user.JoinedRoomIds.Remove(roomId);
        }
    }

    #endregion

    #region 메시지 처리

    // 방 채팅 메시지 처리
    public void HandleRoomChat(string userId, string roomId, string content)
    {
        Room? room = GetRoom(roomId);

        if (room != null)
        {
            // Room의 DoAsync를 사용하여 이 작업을 Room의 스레드에서 처리
            room.ProcessChatMessage(userId, content);
        }
    }

    // 1:1 채팅 메시지 처리
    public void HandlePrivateChat(string senderId, string recipientId, string content)
    {
        DoAsync(() => ProcessPrivateChat(senderId, recipientId, content));
    }

    private void ProcessPrivateChat(string senderId, string recipientId, string content)
    {
        User? sender = null;
        User? recipient = null;

        _usersLock.EnterReadLock();
        try
        {
            _users.TryGetValue(senderId, out sender);
            _users.TryGetValue(recipientId, out recipient);
        }
        finally
        {
            _usersLock.ExitReadLock();
        }

        if (sender is not null && recipient is not null)
        {
            Console.WriteLine($"1:1 채팅: {sender.Username} -> {recipient.Username}: {content}");

            // 메시지 생성
            var message = new ChatMessage(
                Guid.NewGuid(),
                MessageType.PrivateChat,
                sender.Username,
                recipient.Username,
                null,
                content,
                DateTimeOffset.UtcNow);

            // 발신자와 수신자에게 전송
            _ = sender.SendMessageAsync(message);
            _ = recipient.SendMessageAsync(message);
        }
    }

    // 쪽지 보내기 처리
    public void HandleInstantMessage(string senderId, string recipientId, string content)
    {
        DoAsync(() => ProcessInstantMessage(senderId, recipientId, content));
    }

    private void ProcessInstantMessage(string senderId, string recipientId, string content)
    {
        User? sender = null;
        User? recipient = null;

        _usersLock.EnterReadLock();
        try
        {
            _users.TryGetValue(senderId, out sender);
            _users.TryGetValue(recipientId, out recipient);
        }
        finally
        {
            _usersLock.ExitReadLock();
        }

        if (sender is not null && recipient is not null)
        {
            Console.WriteLine($"쪽지: {sender.Username} -> {recipient.Username}: {content}");

            // 메시지 생성
            var message = new ChatMessage(
                Guid.NewGuid(),
                MessageType.InstantMessage,
                sender.Username,
                recipient.Username,
                null,
                content,
                DateTimeOffset.UtcNow);

            // 발신자와 수신자에게 전송
            _ = sender.SendMessageAsync(message);
            _ = recipient.SendMessageAsync(message);
        }
    }

    #endregion

    #region 시스템 메시지

    // 모든 사용자에게 시스템 메시지 전송
    private void BroadcastSystemMessage(MessageType type, string content, string? roomId)
    {
        List<User> allUsers = [];

        _usersLock.EnterReadLock();
        try
        {
            allUsers = _users.Values.ToList();
        }
        finally
        {
            _usersLock.ExitReadLock();
        }

        var message = new ChatMessage(
            Guid.NewGuid(),
            type,
            "시스템",
            null,
            roomId,
            content,
            DateTimeOffset.UtcNow);

        foreach (var user in allUsers)
        {
            _ = user.SendMessageAsync(message);
        }
    }

    #endregion

    // 서버 상태 출력
    public void PrintStatus()
    {
        DoAsync(() => {
            Console.WriteLine("\n==== 채팅 서버 상태 ====");

            _usersLock.EnterReadLock();
            try
            {
                Console.WriteLine($"접속 중인 사용자: {_users.Count}명");
                foreach (var user in _users.Values)
                {
                    Console.WriteLine($"- {user.Username} ({user.UserId}), 참여 중인 방: {user.JoinedRoomIds.Count}개");
                }
            }
            finally
            {
                _usersLock.ExitReadLock();
            }

            Console.WriteLine($"\n채팅방 목록: {_rooms.Count}개");
            lock (_roomsLock)
            {
                foreach (var room in _rooms.Values)
                {
                    room.PrintStatus();
                }
            }

            Console.WriteLine("========================\n");
        });
    }
}
