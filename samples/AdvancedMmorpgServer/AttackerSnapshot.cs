namespace AdvancedMmorpgServer;

/// <summary>
/// Actor 간 안전하게 전달되는 불변 공격 정보.
/// </summary>
public readonly record struct AttackerSnapshot(
    int AttackerId,
    string AttackerName,
    EntityKind AttackerKind,
    float X,
    float Y,
    int Attack);
