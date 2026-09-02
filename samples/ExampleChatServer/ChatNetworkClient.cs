using System.Text.Json;

namespace ExampleChatServer;

/// <summary>
/// 채팅 클라이언트 시뮬레이션 (실제 네트워크 코드는 생략).
/// 동기식 — User actor 큐 안에서 호출되므로 다른 actor 는 막지 않는다.
/// </summary>
public class ChatNetworkClient : IChatClient
{
    private readonly bool _verbose;
    private bool _isConnected;
    private readonly List<ChatMessage> _messageHistory = [];

    public string UserId { get; }
    public string Username { get; }

    public ChatNetworkClient(string userId, string username, bool verbose = false)
    {
        UserId = userId;
        Username = username;
        _verbose = verbose;
        _isConnected = true;

        Console.WriteLine($"클라이언트 생성됨: {Username} ({UserId})");
    }

    /// <summary>
    /// 메시지 수신 처리 (서버 → 클라이언트). 동기식.
    /// User actor 큐 안에서 도므로 다른 actor(Room/Server) Flush 는 막지 않는다.
    /// </summary>
    public void SendMessage(ChatMessage message)
    {
        if (!_isConnected)
        {
            Console.WriteLine($"[오류] 연결이 끊긴 클라이언트에게 메시지 전송 시도: {Username}");
            return;
        }

        _messageHistory.Add(message);

        if (_verbose)
        {
            Console.WriteLine($"[클라이언트 {Username} 수신] {message.Type}: " +
                            $"발신자={message.Sender}, " +
                            $"수신자={message.Recipient ?? "없음"}, " +
                            $"방={message.RoomId ?? "없음"}, " +
                            $"내용=\"{message.Content}\"");
        }
        else
        {
            switch (message.Type)
            {
                case MessageType.RoomChat:
                    Console.WriteLine($"[{message.RoomId}] {message.Sender}: {message.Content}");
                    break;
                case MessageType.PrivateChat:
                    Console.WriteLine($"[1:1 채팅] {message.Sender} -> {message.Recipient}: {message.Content}");
                    break;
                case MessageType.InstantMessage:
                    Console.WriteLine($"[쪽지] {message.Sender} -> {message.Recipient}: {message.Content}");
                    break;
                case MessageType.RoomJoin:
                case MessageType.RoomLeave:
                case MessageType.UserConnect:
                case MessageType.UserDisconnect:
                    Console.WriteLine($"[알림] {message.Content}");
                    break;
            }
        }

        // 네트워크 지연 시뮬레이션 — User actor 큐 안에서 도므로 다른 actor 는 막지 않는다.
        Thread.Sleep(Random.Shared.Next(1, 3));
    }

    public void Disconnect()
    {
        if (_isConnected)
        {
            _isConnected = false;
            Console.WriteLine($"클라이언트 연결 종료됨: {Username} ({UserId})");
        }
    }

    public void Reconnect()
    {
        if (!_isConnected)
        {
            _isConnected = true;
            Console.WriteLine($"클라이언트 재연결됨: {Username} ({UserId})");
        }
    }

    public void PrintMessageHistory()
    {
        Console.WriteLine($"\n=== {Username}의 메시지 히스토리 ({_messageHistory.Count}개) ===");
        foreach (var msg in _messageHistory)
            Console.WriteLine($"[{msg.Timestamp:HH:mm:ss}] ({msg.Type}) {msg.Sender}: {msg.Content}");
        Console.WriteLine("===================================\n");
    }

    public string ExportMessageHistoryAsJson()
    {
        var options = new JsonSerializerOptions { WriteIndented = true };
        return JsonSerializer.Serialize(_messageHistory, options);
    }
}
