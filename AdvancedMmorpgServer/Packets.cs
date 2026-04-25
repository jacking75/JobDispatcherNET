using System.Globalization;

namespace AdvancedMmorpgServer;

/// <summary>
/// 텍스트 기반 패킷 인코딩. 줄바꿈으로 종결, 필드는 '|' 구분.
/// 네트워크 자체는 본 샘플의 핵심이 아니므로 단순화.
///
/// Server → Client:
///   WELCOME|playerId|x|y|worldW|worldH
///   SPAWN|id|kind|name|x|y|hp|maxHp|color
///   DESPAWN|id
///   STATE|id,x,y,hp|id,x,y,hp|...
///   ATTACK|attackerId|targetId|damage
///   DEATH|id|killerId
///   RESPAWN|id|x|y|hp
///
/// Client → Server:
///   LOGIN|botName
///   MOVE|x|y
///   ATTACK|targetId
///   LEAVE
/// </summary>
public static class Packets
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    public static string Welcome(int pid, float x, float y, float w, float h) =>
        $"WELCOME|{pid}|{x.ToString("F1", Inv)}|{y.ToString("F1", Inv)}|{w.ToString("F1", Inv)}|{h.ToString("F1", Inv)}";

    public static string Spawn(Entity e) =>
        $"SPAWN|{e.Id}|{e.Kind}|{e.Name}|{e.X.ToString("F1", Inv)}|{e.Y.ToString("F1", Inv)}|{e.Hp}|{e.MaxHp}|{e.Color}";

    public static string Despawn(int id) => $"DESPAWN|{id}";
    public static string Attack(int aId, int tId, int dmg) => $"ATTACK|{aId}|{tId}|{dmg}";
    public static string Death(int id, int killerId) => $"DEATH|{id}|{killerId}";
    public static string Respawn(int id, float x, float y, int hp) =>
        $"RESPAWN|{id}|{x.ToString("F1", Inv)}|{y.ToString("F1", Inv)}|{hp}";
}

public static class PacketHandler
{
    public static void Handle(GameServer server, ClientSession session, string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return;
        var parts = raw.Split('|');
        if (parts.Length == 0) return;

        switch (parts[0].ToUpperInvariant())
        {
            case "LOGIN" when parts.Length >= 2:
                if (session.PlayerId != 0)
                    return; // 이미 로그인됨
                var name = parts[1];
                server.World.AddPlayer(name, session);
                break;

            case "MOVE" when parts.Length >= 3 && session.PlayerId != 0:
                if (TryFloat(parts[1], out var mx) && TryFloat(parts[2], out var my))
                    server.World.HandleClientMove(session.PlayerId, mx, my);
                break;

            case "ATTACK" when parts.Length >= 2 && session.PlayerId != 0:
                if (int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var tid))
                    server.World.HandleClientAttack(session.PlayerId, tid);
                break;

            case "LEAVE":
                session.Close();
                break;
        }
    }

    private static bool TryFloat(string s, out float v) =>
        float.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out v);
}
