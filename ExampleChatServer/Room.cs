using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using JobDispatcherNET;

namespace ExampleChatServer;

public class Room : AsyncExecutable
{
    private readonly Dictionary<string, User> _users = [];
    private readonly string _roomId;
    private readonly string _name;

    public Room(string name)
    {
        _roomId = Guid.NewGuid().ToString();
        _name = name;
        Console.WriteLine($"채팅방 생성: {_name} ({_roomId})");
    }

    public string RoomId => _roomId;
    public string Name => _name;

    // 사용자 수 반환
    public int GetUserCount()
    {
        int count = 0;

        // DoAsync를 사용하여 현재 Room의 스레드에서 안전하게 실행
        // count를 return하기 위해 TaskCompletionSource 사용
        var tcs = new TaskCompletionSource<int>();

        DoAsync(() => {
            count = _users.Count;
            tcs.SetResult(count);
        });

        return tcs.Task.Result;
    }

    // 사용자 목록 반환
    public List<string> GetUserIds()
    {
        var tcs = new TaskCompletionSource<List<string>>();

        DoAsync(() => {
            var userIds = _users.Keys.ToList();
            tcs.SetResult(userIds);
        });

        return tcs.Task.Result;
    }

    // 사용자 입장 처리
    public void AddUser(User user)
    {
        DoAsync(() => {
            if (!_users.ContainsKey(user.UserId))
            {
                _users[user.UserId] = user;
                Console.WriteLine($"방 입장: {user.Username} -> {_name}");

                // 입장 메시지 모든 사용자에게 전송
                BroadcastSystemMessage(
                    MessageType.RoomJoin,
                    $"{user.Username}님이 입장하셨습니다.");
            }
        });
    }

    // 사용자 퇴장 처리
    public void RemoveUser(string userId)
    {
        DoAsync(() => {
            if (_users.TryGetValue(userId, out var user))
            {
                _users.Remove(userId);
                Console.WriteLine($"방 퇴장: {user.Username} <- {_name}");

                // 퇴장 메시지 모든 사용자에게 전송
                BroadcastSystemMessage(
                    MessageType.RoomLeave,
                    $"{user.Username}님이 퇴장하셨습니다.");
            }
        });
    }

    // 채팅 메시지 처리
    public void ProcessChatMessage(string userId, string content)
    {
        DoAsync(() => {
            if (_users.TryGetValue(userId, out var sender))
            {
                Console.WriteLine($"방 채팅: {sender.Username} -> 방[{_roomId}]: {content}");

                // 메시지 생성
                var message = new ChatMessage(
                    Guid.NewGuid(),
                    MessageType.RoomChat,
                    sender.Username,
                    null,
                    _roomId,
                    content,
                    DateTimeOffset.UtcNow);

                // 모든 사용자에게 메시지 전송
                foreach (var user in _users.Values)
                {
                    _ = user.SendMessageAsync(message);
                }
            }
        });
    }

    // 시스템 메시지 브로드캐스트
    private void BroadcastSystemMessage(MessageType type, string content)
    {
        var message = new ChatMessage(
            Guid.NewGuid(),
            type,
            "시스템",
            null,
            _roomId,
            content,
            DateTimeOffset.UtcNow);

        foreach (var user in _users.Values)
        {
            _ = user.SendMessageAsync(message);
        }
    }

    // 사용자 강제 퇴장 (서버 종료 등에 사용)
    public void ForceRemoveAllUsers()
    {
        DoAsync(() => {
            foreach (var user in _users.Values)
            {
                user.JoinedRoomIds.Remove(_roomId);
            }

            _users.Clear();
            Console.WriteLine($"방 초기화: {_name} 모든 사용자 제거됨");
        });
    }

    // 방 상태 출력
    public void PrintStatus()
    {
        DoAsync(() => {
            Console.WriteLine($"- {_name} ({_roomId}), 참여자: {_users.Count}명");
            foreach (var user in _users.Values)
            {
                Console.WriteLine($"  * {user.Username} ({user.UserId})");
            }
        });
    }
}