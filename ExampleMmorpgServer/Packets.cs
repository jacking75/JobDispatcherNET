namespace ExampleMmorpgServer;

/// <summary>
/// 패킷 타입 정의.
/// 실제 서버에서는 바이너리 직렬화를 사용하지만,
/// 예제에서는 텍스트 기반 프로토콜로 간략화한다.
/// </summary>
public enum PacketType
{
    EnterZone,      // 존 입장: ENTERZONE|zoneId|spawnX|spawnY
    Move,           // 이동:   MOVE|newX|newY
    MeleeAttack,    // 근접공격: MELEEATTACK|targetPlayerId
    AreaAttack,     // 범위공격: AREAATTACK|centerX|centerY|radius
    Disconnect,     // 접속종료: DISCONNECT
}

/// <summary>
/// 텍스트 패킷 파서
/// </summary>
public static class PacketParser
{
    public static (PacketType type, string[] args)? Parse(string raw)
    {
        var parts = raw.Split('|');
        if (parts.Length == 0)
            return null;

        if (!Enum.TryParse<PacketType>(parts[0], ignoreCase: true, out var type))
            return null;

        return (type, parts[1..]);
    }
}

/// <summary>
/// 패킷 핸들러 — 파싱된 패킷을 GameServer로 라우팅한다.
/// 네트워크 IO 스레드에서 호출되며, GameServer가 해당 PlayerActor의 DoAsync로 전달.
/// </summary>
public static class PacketHandler
{
    public static void Handle(GameServer server, Player player, string rawPacket)
    {
        var parsed = PacketParser.Parse(rawPacket);
        if (parsed is null)
        {
            Console.WriteLine($"[패킷오류] 알 수 없는 패킷: {rawPacket}");
            return;
        }

        var (type, args) = parsed.Value;

        switch (type)
        {
            case PacketType.EnterZone when args.Length >= 3:
                if (float.TryParse(args[1], out var sx) && float.TryParse(args[2], out var sy))
                    server.HandleEnterZone(player, sx, sy);
                break;

            case PacketType.Move when args.Length >= 2:
                if (float.TryParse(args[0], out var mx) && float.TryParse(args[1], out var my))
                    server.HandleMove(player.PlayerId, mx, my);
                break;

            case PacketType.MeleeAttack when args.Length >= 1:
                server.HandleMeleeAttack(player.PlayerId, args[0]);
                break;

            case PacketType.AreaAttack when args.Length >= 3:
                if (float.TryParse(args[0], out var cx) && float.TryParse(args[1], out var cy) &&
                    float.TryParse(args[2], out var r))
                    server.HandleAreaAttack(player.PlayerId, cx, cy, r);
                break;

            case PacketType.Disconnect:
                server.HandlePlayerDisconnect(player.PlayerId);
                break;

            default:
                Console.WriteLine($"[패킷오류] 잘못된 패킷 형식: {rawPacket}");
                break;
        }
    }
}
