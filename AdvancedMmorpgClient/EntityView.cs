namespace AdvancedMmorpgClient;

public enum EntityKindView
{
    Player,
    Slime,
    Goblin,
    Wolf,
    Skeleton,
    Boss,
}

public sealed class EntityView
{
    public int Id { get; init; }
    public string Name { get; init; } = "";
    public EntityKindView Kind { get; init; }
    public string Color { get; init; } = "";
    public float X;
    public float Y;
    public int Hp;
    public int MaxHp;
    public bool IsAlive => Hp > 0;
}
