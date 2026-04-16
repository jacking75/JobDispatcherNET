namespace ExampleMmorpgServer;

/// <summary>
/// 플레이어 상태 정보.
/// GameZone의 DoAsync 내에서만 접근되므로 lock이 필요 없다.
/// </summary>
public class Player
{
    public string PlayerId { get; }
    public string Name { get; }
    public float X { get; set; }
    public float Y { get; set; }
    public int Hp { get; set; }
    public int MaxHp { get; }
    public int Attack { get; }
    public int Defense { get; }
    public bool IsAlive => Hp > 0;

    /// <summary>
    /// 네트워크 전송용 콜백 (ClientSession에서 설정)
    /// </summary>
    public Action<string>? SendPacket { get; set; }

    public Player(string playerId, string name, int maxHp = 1000, int attack = 50, int defense = 20)
    {
        PlayerId = playerId;
        Name = name;
        MaxHp = maxHp;
        Hp = maxHp;
        Attack = attack;
        Defense = defense;
    }

    public int TakeDamage(int rawDamage)
    {
        int actualDamage = Math.Max(1, rawDamage - Defense);
        Hp = Math.Max(0, Hp - actualDamage);
        return actualDamage;
    }

    public float DistanceTo(Player other)
    {
        float dx = X - other.X;
        float dy = Y - other.Y;
        return MathF.Sqrt(dx * dx + dy * dy);
    }

    public float DistanceTo(float x, float y)
    {
        float dx = X - x;
        float dy = Y - y;
        return MathF.Sqrt(dx * dx + dy * dy);
    }
}
