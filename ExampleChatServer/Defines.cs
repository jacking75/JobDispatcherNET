using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace ExampleChatServer;

// 채팅 메시지 종류
public enum MessageType
{
    RoomChat,       // 방 채팅
    PrivateChat,    // 1:1 채팅
    InstantMessage, // 쪽지
    RoomJoin,       // 방 입장
    RoomLeave,      // 방 퇴장
    UserConnect,    // 유저 접속
    UserDisconnect  // 유저 접속 종료
}

// 채팅 메시지 클래스
public record ChatMessage(
    Guid Id,
    MessageType Type,
    string Sender,
    string? Recipient,
    string? RoomId,
    string Content,
    DateTimeOffset Timestamp);

// 클라이언트 인터페이스
public interface IChatClient
{
    string UserId { get; }
    string Username { get; }

    // 메시지 전송 (네트워크 구현은 생략)
    ValueTask SendMessageAsync(ChatMessage message);
}
