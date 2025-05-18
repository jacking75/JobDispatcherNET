using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;


namespace ExampleChatServer;

// 사용자 클래스
public class User
{
    public string UserId { get; }
    public string Username { get; }
    public HashSet<string> JoinedRoomIds { get; } = [];
    public IChatClient Client { get; }

    public User(IChatClient client)
    {
        UserId = client.UserId;
        Username = client.Username;
        Client = client;
    }

    // 메시지 전송
    public async ValueTask SendMessageAsync(ChatMessage message)
    {
        await Client.SendMessageAsync(message);
    }
}
