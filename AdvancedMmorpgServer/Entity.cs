namespace AdvancedMmorpgServer;

/// <summary>
/// 엔티티 종류. 클라이언트는 이 값으로 색/모양을 결정한다.
/// </summary>
public enum EntityKind
{
    Player,
    Slime,
    Goblin,
    Wolf,
    Skeleton,
    Boss,
}

/// <summary>
/// 모든 엔티티의 베이스 데이터. PlayerActor / NpcActor 안에서만 변경된다.
/// 다른 스레드에서의 읽기는 브로드캐스트 시점의 약한 일관성(스냅샷용) 만 보장.
/// </summary>
public abstract class Entity
{
    public int Id { get; }
    public string Name { get; }
    public EntityKind Kind { get; protected set; }

    /// <summary>
    /// 클라이언트 렌더링용 색상 (#RRGGBB). Player는 고정 기본값,
    /// Npc는 ServerConfig.NpcTypeConfig.Color에서 주입.
    /// </summary>
    public string Color { get; protected set; } = "#FFFFFF";

    public float X;
    public float Y;
    public int Hp;
    public int MaxHp;
    public int Attack;
    public int Defense;
    public float MoveSpeed;

    public bool IsAlive => Volatile.Read(ref Hp) > 0;

    protected Entity(int id, string name, EntityKind kind)
    {
        Id = id;
        Name = name;
        Kind = kind;
    }

    public int TakeDamage(int rawDamage)
    {
        int actual = Math.Max(1, rawDamage - Defense);
        int newHp = Math.Max(0, Hp - actual);
        Hp = newHp;
        return actual;
    }

    public float DistanceTo(float x, float y)
    {
        float dx = X - x;
        float dy = Y - y;
        return MathF.Sqrt(dx * dx + dy * dy);
    }

    public float DistanceSqTo(Entity other)
    {
        float dx = X - other.X;
        float dy = Y - other.Y;
        return dx * dx + dy * dy;
    }
}

public sealed class Player : Entity
{
    public Action<string>? SendPacket { get; set; }

    public Player(int id, string name) : base(id, name, EntityKind.Player)
    {
        MaxHp = 1000;
        Hp = MaxHp;
        Attack = 50;
        Defense = 15;
        MoveSpeed = 4f;
        Color = "#78B4FF";
    }
}

public sealed class Npc : Entity
{
    public ServerConfig.NpcTypeConfig Config { get; }
    public float AggroRange => Config.AggroRange;
    public float AttackRange => Config.AttackRange;
    public float FleeHpRatio => Config.FleeHpRatio;

    /// <summary>스폰 위치 — Idle 패트롤 중심점</summary>
    public float SpawnX { get; }
    public float SpawnY { get; }

    public Npc(int id, string name, ServerConfig.NpcTypeConfig cfg, float x, float y)
        : base(id, name, ParseKind(cfg.Kind))
    {
        Config = cfg;
        MaxHp = cfg.MaxHp;
        Hp = cfg.MaxHp;
        Attack = cfg.Attack;
        Defense = cfg.Defense;
        MoveSpeed = cfg.MoveSpeed;
        X = x;
        Y = y;
        SpawnX = x;
        SpawnY = y;
        Color = string.IsNullOrEmpty(cfg.Color) ? "#FFFFFF" : cfg.Color;
    }

    private static EntityKind ParseKind(string s) =>
        Enum.TryParse<EntityKind>(s, ignoreCase: true, out var k) ? k : EntityKind.Slime;
}
