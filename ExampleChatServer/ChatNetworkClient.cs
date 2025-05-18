using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

namespace ExampleChatServer;

/// <summary>
/// 채팅 클라이언트 네트워크 구현 (간략화된 버전)
/// </summary>
public class ChatNetworkClient : IChatClient
{
    private readonly bool _verbose;
    private bool _isConnected;
    private readonly List<ChatMessage> _messageHistory = [];

    // IChatClient 구현
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
    /// 메시지 수신 및 처리 (네트워크 구현은 생략)
    /// </summary>
    public ValueTask SendMessageAsync(ChatMessage message)
    {
        if (!_isConnected)
        {
            Console.WriteLine($"[오류] 연결이 끊긴 클라이언트에게 메시지 전송 시도: {Username}");
            return ValueTask.CompletedTask;
        }

        // 메시지 기록에 추가
        _messageHistory.Add(message);

        // 메시지 처리 로직 (실제로는 클라이언트에 전송)
        if (_verbose)
        {
            // 자세한 로그 출력
            Console.WriteLine($"[클라이언트 {Username} 수신] {message.Type}: " +
                            $"발신자={message.Sender}, " +
                            $"수신자={message.Recipient ?? "없음"}, " +
                            $"방={message.RoomId ?? "없음"}, " +
                            $"내용=\"{message.Content}\"");
        }
        else
        {
            // 간략한 로그 출력
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

        // 메시지 수신 시뮬레이션 - 실제 네트워크 지연 시뮬레이션
        // 실제 구현에서는 네트워크 전송 코드가 들어갈 위치
        Thread.Sleep(Random.Shared.Next(1, 5));

        return ValueTask.CompletedTask;
    }

    /// <summary>
    /// 서버에 메시지 전송 (클라이언트 -> 서버) 시뮬레이션
    /// </summary>
    public void SimulateSendToServer(string messageContent, MessageType type, string? roomId = null, string? recipientId = null)
    {
        if (!_isConnected)
        {
            Console.WriteLine($"[오류] 연결이 끊긴 클라이언트가 메시지 전송 시도: {Username}");
            return;
        }

        Console.WriteLine($"[클라이언트 {Username} 전송] {type}: \"{messageContent}\"");

        // 실제 구현에서는 여기서 서버로 메시지를 전송
    }

    /// <summary>
    /// 연결 종료 시뮬레이션
    /// </summary>
    public void Disconnect()
    {
        if (_isConnected)
        {
            _isConnected = false;
            Console.WriteLine($"클라이언트 연결 종료됨: {Username} ({UserId})");
        }
    }

    /// <summary>
    /// 재연결 시뮬레이션
    /// </summary>
    public void Reconnect()
    {
        if (!_isConnected)
        {
            _isConnected = true;
            Console.WriteLine($"클라이언트 재연결됨: {Username} ({UserId})");
        }
    }

    /// <summary>
    /// 메시지 히스토리 출력
    /// </summary>
    public void PrintMessageHistory()
    {
        Console.WriteLine($"\n=== {Username}의 메시지 히스토리 ({_messageHistory.Count}개) ===");

        foreach (var msg in _messageHistory)
        {
            Console.WriteLine($"[{msg.Timestamp:HH:mm:ss}] ({msg.Type}) {msg.Sender}: {msg.Content}");
        }

        Console.WriteLine("===================================\n");
    }

    /// <summary>
    /// 메시지 히스토리를 JSON으로 내보내기
    /// </summary>
    public string ExportMessageHistoryAsJson()
    {
        var options = new JsonSerializerOptions { WriteIndented = true };
        return JsonSerializer.Serialize(_messageHistory, options);
    }
}


/*
이 ChatNetworkClient 클래스는 실제 네트워크 구현 없이 채팅 클라이언트를 시뮬레이션합니다:

기본 정보:

UserId와 Username을 저장하여 사용자를 식별합니다.
연결 상태(_isConnected)를 관리하여 연결/연결 종료 상태를 시뮬레이션합니다.
메시지 처리:

SendMessageAsync: 서버에서 클라이언트로 메시지를 보내는 메소드를 구현합니다.
메시지 타입에 따라 다른 형식으로 콘솔에 출력합니다.
상세 모드(_verbose)에서는 메시지의 모든 필드를 출력합니다.
추가 기능:

SimulateSendToServer: 클라이언트에서 서버로 메시지를 보내는 것을 시뮬레이션합니다.
Disconnect/Reconnect: 연결 종료 및 재연결을 시뮬레이션합니다.
PrintMessageHistory: 수신한 모든 메시지의 기록을 출력합니다.
ExportMessageHistoryAsJson: 메시지 기록을 JSON 형식으로 내보냅니다.
네트워크 시뮬레이션:

실제 네트워크 지연을 시뮬레이션하기 위해 Thread.Sleep을 사용합니다.
연결이 끊긴 상태에서의 오류 처리를 포함합니다.
이 구현은 채팅 서버 예제에서 클라이언트를 시뮬레이션하는 데 사용되며, 실제 네트워크 구현을 추가하기 전에 로컬에서 테스트하기에 적합합니다. 실제 네트워크 코드는 SendMessageAsync 및 SimulateSendToServer 메서드 내에 추가될 수 있습니다.
 */ 