using System.Collections.Concurrent;
using System.Globalization;

namespace AdvancedMmorpgClient;

/// <summary>
/// 클라이언트 프로세스 전체에서 공유되는 월드 스냅샷.
/// 모든 봇 커넥션의 STATE 패킷을 받아 동일한 view를 갱신한다.
/// 렌더러가 이 컬렉션을 읽어 화면에 그린다.
/// </summary>
public sealed class WorldState
{
    public float WorldWidth { get; private set; } = 1000f;
    public float WorldHeight { get; private set; } = 1000f;

    public ConcurrentDictionary<int, EntityView> Entities { get; } = [];
    public HashSet<int> MyBotIds { get; } = [];
    private readonly object _myBotIdsLock = new();

    public void SetWorldSize(float w, float h)
    {
        WorldWidth = w;
        WorldHeight = h;
    }

    public void RegisterMyBot(int id)
    {
        lock (_myBotIdsLock) MyBotIds.Add(id);
    }

    public bool IsMyBot(int id)
    {
        lock (_myBotIdsLock) return MyBotIds.Contains(id);
    }

    public void HandlePacket(string packet)
    {
        var parts = packet.Split('|');
        if (parts.Length == 0) return;

        switch (parts[0])
        {
            case "WELCOME":
                if (parts.Length >= 6 &&
                    TryFloat(parts[4], out var ww) && TryFloat(parts[5], out var wh))
                    SetWorldSize(ww, wh);
                break;

            case "SPAWN":
                // SPAWN|id|kind|name|x|y|hp|maxHp|color
                if (parts.Length >= 8 &&
                    int.TryParse(parts[1], out var sid) &&
                    Enum.TryParse<EntityKindView>(parts[2], out var kind) &&
                    TryFloat(parts[4], out var sx) && TryFloat(parts[5], out var sy) &&
                    int.TryParse(parts[6], out var hp) && int.TryParse(parts[7], out var maxHp))
                {
                    var color = parts.Length >= 9 ? parts[8] : "";
                    Entities[sid] = new EntityView
                    {
                        Id = sid, Name = parts[3], Kind = kind, Color = color,
                        X = sx, Y = sy, Hp = hp, MaxHp = maxHp
                    };
                }
                break;

            case "DESPAWN":
                if (parts.Length >= 2 && int.TryParse(parts[1], out var did))
                    Entities.TryRemove(did, out _);
                break;

            case "STATE":
                // STATE|id,x,y,hp|id,x,y,hp|...
                for (int i = 1; i < parts.Length; i++)
                {
                    var seg = parts[i].Split(',');
                    if (seg.Length != 4) continue;
                    if (!int.TryParse(seg[0], out var eid)) continue;
                    if (!TryFloat(seg[1], out var ex)) continue;
                    if (!TryFloat(seg[2], out var ey)) continue;
                    if (!int.TryParse(seg[3], out var ehp)) continue;
                    if (Entities.TryGetValue(eid, out var ev))
                    {
                        ev.X = ex;
                        ev.Y = ey;
                        ev.Hp = ehp;
                    }
                }
                break;

            case "DEATH":
                if (parts.Length >= 2 && int.TryParse(parts[1], out var deadId)
                    && Entities.TryGetValue(deadId, out var dev))
                    dev.Hp = 0;
                break;

            case "RESPAWN":
                if (parts.Length >= 5 && int.TryParse(parts[1], out var rid)
                    && TryFloat(parts[2], out var rx) && TryFloat(parts[3], out var ry)
                    && int.TryParse(parts[4], out var rhp)
                    && Entities.TryGetValue(rid, out var rev))
                {
                    rev.X = rx; rev.Y = ry; rev.Hp = rhp;
                }
                break;

            case "ATTACK":
                // 시각 효과 등에 활용 가능 (현재는 무시)
                break;
        }
    }

    private static bool TryFloat(string s, out float v) =>
        float.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out v);
}
