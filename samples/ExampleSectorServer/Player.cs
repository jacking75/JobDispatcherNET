namespace ExampleSectorServer;

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
    /// 섹터 이동 중 플래그.
    /// true인 동안 이 플레이어에 대한 공격/상호작용은 거부된다.
    /// 구 섹터에서 제거 → 신 섹터에 추가 사이의 원자성을 보장할 수 없으므로
    /// 이 플래그로 "이동 중" 상태를 명시한다.
    /// </summary>
    public volatile bool IsTransferring;

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
        int actual = Math.Max(1, rawDamage - Defense);
        Hp = Math.Max(0, Hp - actual);
        return actual;
    }

    public float DistanceTo(float x, float y)
    {
        float dx = X - x, dy = Y - y;
        return MathF.Sqrt(dx * dx + dy * dy);
    }
}

/// <summary>
/// 공격자 스냅샷 — 섹터 간 전투 시 불변 데이터로 전달.
/// 공격자 섹터에서 캡처 → 대상 섹터의 DoAsync로 전달.
/// </summary>
public readonly record struct AttackerSnapshot(
    string PlayerId, string Name,
    float X, float Y, int Attack);
