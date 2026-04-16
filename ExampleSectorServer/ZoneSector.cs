using JobDispatcherNET;

namespace ExampleSectorServer;

/// <summary>
/// 존 내 하나의 섹터. AsyncExecutable을 상속하여
/// 같은 섹터 내 모든 작업은 lock 없이 직렬화된다.
///
/// ★ 위험 지점:
///   - 섹터 경계에서의 근접/범위 공격 → 스냅샷 패턴으로 해결
///   - 섹터 이동 중 플레이어 상태 → IsTransferring 플래그로 보호
///   - 원거리 귓속말 → 대상 섹터의 DoAsync로 전달
/// </summary>
public class ZoneSector : AsyncExecutable
{
    private readonly Dictionary<string, Player> _players = [];

    public int GridX { get; }
    public int GridY { get; }
    public float OriginX { get; }
    public float OriginY { get; }
    public float Width { get; }
    public float Height { get; }
    public string SectorId { get; }

    private const float MeleeRange = 5.0f;

    public ZoneSector(int gridX, int gridY, float originX, float originY, float width, float height)
    {
        GridX = gridX;
        GridY = gridY;
        OriginX = originX;
        OriginY = originY;
        Width = width;
        Height = height;
        SectorId = $"({gridX},{gridY})";
    }

    public bool ContainsPoint(float x, float y) =>
        x >= OriginX && x < OriginX + Width &&
        y >= OriginY && y < OriginY + Height;

    // ════════════════════════════════════════════════
    //  같은 섹터 내 작업 — lock 불필요, 완전 안전
    // ════════════════════════════════════════════════

    public void AddPlayer(Player player)
    {
        DoAsync(() =>
        {
            _players[player.PlayerId] = player;
            player.IsTransferring = false;
            Console.WriteLine($"  [섹터{SectorId}] {player.Name} 진입 " +
                              $"({player.X:F0},{player.Y:F0}) [스레드:{Environment.CurrentManagedThreadId}]");
        });
    }

    public void RemovePlayer(string playerId, string reason = "퇴장")
    {
        DoAsync(() =>
        {
            if (_players.Remove(playerId, out var p))
                Console.WriteLine($"  [섹터{SectorId}] {p.Name} {reason} [스레드:{Environment.CurrentManagedThreadId}]");
        });
    }

    /// <summary>
    /// 같은 섹터 내 이동. 섹터 경계를 넘으면 onCrossBoundary 콜백 호출.
    /// </summary>
    public void MovePlayer(string playerId, float newX, float newY,
        Action<Player, float, float>? onCrossBoundary = null)
    {
        DoAsync(() =>
        {
            if (!_players.TryGetValue(playerId, out var player) || !player.IsAlive)
                return;

            float oldX = player.X, oldY = player.Y;
            player.X = newX;
            player.Y = newY;

            // 섹터 경계를 넘었는지 확인
            if (!ContainsPoint(newX, newY))
            {
                Console.WriteLine($"  [섹터{SectorId}] ⚠ {player.Name} 섹터 경계 초과! " +
                                  $"({oldX:F0},{oldY:F0})→({newX:F0},{newY:F0}) [스레드:{Environment.CurrentManagedThreadId}]");
                onCrossBoundary?.Invoke(player, newX, newY);
                return;
            }

            Console.WriteLine($"  [섹터{SectorId}] {player.Name} 이동 " +
                              $"({oldX:F0},{oldY:F0})→({newX:F0},{newY:F0}) [스레드:{Environment.CurrentManagedThreadId}]");
        });
    }

    /// <summary>
    /// 같은 섹터 내 근접 공격 — 두 플레이어 모두 이 섹터에 있으므로 안전.
    /// </summary>
    public void MeleeAttackSameSector(string attackerId, string targetId)
    {
        DoAsync(() =>
        {
            if (!_players.TryGetValue(attackerId, out var atk) || !atk.IsAlive) return;
            if (!_players.TryGetValue(targetId, out var tgt) || !tgt.IsAlive) return;

            float dist = atk.DistanceTo(tgt.X, tgt.Y);
            if (dist > MeleeRange)
            {
                Console.WriteLine($"  [섹터{SectorId}] {atk.Name}→{tgt.Name} 거리초과 ({dist:F1})");
                return;
            }

            int dmg = tgt.TakeDamage(atk.Attack);
            Console.WriteLine($"  [섹터{SectorId}] 근접공격: {atk.Name}→{tgt.Name} " +
                              $"데미지:{dmg} HP:{tgt.Hp} [스레드:{Environment.CurrentManagedThreadId}]");

            if (!tgt.IsAlive)
                Console.WriteLine($"  [섹터{SectorId}] ★ {tgt.Name} 사망! (처치: {atk.Name})");
        });
    }

    /// <summary>
    /// 같은 섹터 내 범위 공격
    /// </summary>
    public void AreaAttackSameSector(string attackerId, float cx, float cy, float radius)
    {
        DoAsync(() =>
        {
            if (!_players.TryGetValue(attackerId, out var atk) || !atk.IsAlive) return;

            int hits = 0;
            foreach (var tgt in _players.Values)
            {
                if (tgt.PlayerId == attackerId || !tgt.IsAlive) continue;
                if (tgt.DistanceTo(cx, cy) <= radius)
                {
                    int dmg = tgt.TakeDamage((int)(atk.Attack * 0.7f));
                    hits++;
                    Console.WriteLine($"  [섹터{SectorId}] AoE피격: {atk.Name}→{tgt.Name} " +
                                      $"데미지:{dmg} HP:{tgt.Hp} [스레드:{Environment.CurrentManagedThreadId}]");
                }
            }
            if (hits > 0)
                Console.WriteLine($"  [섹터{SectorId}] AoE 결과: {hits}명 적중");
        });
    }

    // ════════════════════════════════════════════════
    //  섹터 간 작업 — ★ 주의 필요! ★
    //  반드시 대상 섹터의 DoAsync를 통해야 안전
    // ════════════════════════════════════════════════

    /// <summary>
    /// [1단계] 공격자 섹터에서 호출 — 스냅샷 캡처 후 대상 섹터로 전달.
    /// 공격자와 대상이 다른 섹터에 있을 때 사용.
    /// </summary>
    public void InitiateCrossSectorMelee(string attackerId, ZoneSector targetSector, string targetId)
    {
        DoAsync(() =>
        {
            if (!_players.TryGetValue(attackerId, out var atk) || !atk.IsAlive) return;

            // 스냅샷 캡처 — 이 시점의 공격자 상태를 불변 복사
            var snapshot = new AttackerSnapshot(atk.PlayerId, atk.Name, atk.X, atk.Y, atk.Attack);

            Console.WriteLine($"  [섹터{SectorId}] ⚡ 섹터간 공격 시작: {atk.Name} → 섹터{targetSector.SectorId} " +
                              $"[스레드:{Environment.CurrentManagedThreadId}]");

            // [2단계] 대상 섹터의 DoAsync로 전달 — 대상 섹터의 스레드에서 안전하게 처리됨
            targetSector.ReceiveCrossSectorMelee(snapshot, targetId);
        });
    }

    /// <summary>
    /// [2단계] 대상 섹터에서 호출됨 — 스냅샷 데이터로 데미지 적용.
    /// 이 메서드는 대상 섹터의 DoAsync 안에서 실행되므로 대상 데이터 접근이 안전.
    /// </summary>
    public void ReceiveCrossSectorMelee(AttackerSnapshot snapshot, string targetId)
    {
        DoAsync(() =>
        {
            if (!_players.TryGetValue(targetId, out var tgt))
            {
                Console.WriteLine($"  [섹터{SectorId}] ⚠ {targetId}가 이 섹터에 없음 (이동했을 수 있음)");
                return;
            }
            if (!tgt.IsAlive) return;
            if (tgt.IsTransferring)
            {
                Console.WriteLine($"  [섹터{SectorId}] ⚠ {tgt.Name} 섹터 이동 중 — 공격 무효");
                return;
            }

            float dist = tgt.DistanceTo(snapshot.X, snapshot.Y);
            if (dist > MeleeRange)
            {
                Console.WriteLine($"  [섹터{SectorId}] {snapshot.Name}→{tgt.Name} 거리초과 ({dist:F1})");
                return;
            }

            int dmg = tgt.TakeDamage(snapshot.Attack);
            Console.WriteLine($"  [섹터{SectorId}] ⚡ 섹터간 피격: {snapshot.Name}→{tgt.Name} " +
                              $"데미지:{dmg} HP:{tgt.Hp} [스레드:{Environment.CurrentManagedThreadId}]");

            if (!tgt.IsAlive)
                Console.WriteLine($"  [섹터{SectorId}] ★ {tgt.Name} 사망! (처치: {snapshot.Name})");
        });
    }

    /// <summary>
    /// 다른 섹터에서 날아온 범위 공격 데미지 적용.
    /// 공격자 섹터에서 스냅샷을 캡처한 후, 영향 범위에 걸치는 모든 섹터에 이 메서드 호출.
    /// </summary>
    public void ReceiveCrossSectorAoE(AttackerSnapshot snapshot, float cx, float cy, float radius)
    {
        DoAsync(() =>
        {
            int hits = 0;
            foreach (var tgt in _players.Values)
            {
                if (tgt.PlayerId == snapshot.PlayerId || !tgt.IsAlive || tgt.IsTransferring) continue;
                if (tgt.DistanceTo(cx, cy) <= radius)
                {
                    int dmg = tgt.TakeDamage((int)(snapshot.Attack * 0.7f));
                    hits++;
                    Console.WriteLine($"  [섹터{SectorId}] ⚡ 섹터간 AoE피격: {snapshot.Name}→{tgt.Name} " +
                                      $"데미지:{dmg} HP:{tgt.Hp} [스레드:{Environment.CurrentManagedThreadId}]");
                }
            }
            if (hits > 0)
                Console.WriteLine($"  [섹터{SectorId}] 섹터간 AoE 결과: {hits}명 적중");
        });
    }

    /// <summary>
    /// 범위 공격 시작 — 공격자 섹터에서 호출.
    /// 1. 같은 섹터 내 AoE 처리
    /// 2. 스냅샷 캡처 후 다른 영향 섹터로 팬아웃
    /// 한 번의 DoAsync로 통합하여 불필요한 이중 큐잉을 방지한다.
    /// </summary>
    public void InitiateAreaAttack(string attackerId, float cx, float cy, float radius,
        List<ZoneSector> affectedSectors)
    {
        DoAsync(() =>
        {
            if (!_players.TryGetValue(attackerId, out var atk) || !atk.IsAlive) return;

            // 스냅샷 캡처 (실제 Attack 값 사용)
            var snapshot = new AttackerSnapshot(atk.PlayerId, atk.Name, atk.X, atk.Y, atk.Attack);

            Console.WriteLine($"  [섹터{SectorId}] AoE 시전: {atk.Name} 중심({cx:F0},{cy:F0}) 반경{radius:F0} " +
                              $"영향 섹터:{affectedSectors.Count}개 [스레드:{Environment.CurrentManagedThreadId}]");

            // 같은 섹터 내 AoE (직접 처리 — 이미 DoAsync 안이므로 안전)
            int hits = 0;
            foreach (var tgt in _players.Values)
            {
                if (tgt.PlayerId == attackerId || !tgt.IsAlive) continue;
                if (tgt.DistanceTo(cx, cy) <= radius)
                {
                    int dmg = tgt.TakeDamage((int)(atk.Attack * 0.7f));
                    hits++;
                    Console.WriteLine($"  [섹터{SectorId}] AoE피격: {atk.Name}→{tgt.Name} " +
                                      $"데미지:{dmg} HP:{tgt.Hp} [스레드:{Environment.CurrentManagedThreadId}]");
                }
            }
            if (hits > 0)
                Console.WriteLine($"  [섹터{SectorId}] AoE 결과: {hits}명 적중");

            // 다른 섹터로 팬아웃
            foreach (var target in affectedSectors)
            {
                if (target == this) continue; // 같은 섹터는 위에서 이미 처리
                target.ReceiveCrossSectorAoE(snapshot, cx, cy, radius);
            }
        });
    }

    /// <summary>
    /// 귓속말 발신 — 발신자 섹터에서 호출.
    /// 발신자 이름을 안전하게 조회한 후 대상 섹터의 DoAsync로 전달.
    /// </summary>
    public void SendWhisper(string senderId, ZoneSector targetSector, string targetId,
        string message, bool sameSector)
    {
        DoAsync(() =>
        {
            if (!_players.TryGetValue(senderId, out var sender)) return;

            Console.WriteLine($"  [섹터{SectorId}] 💬 귓속말 발신: " +
                              $"{sender.Name}→{targetId}: \"{message}\" " +
                              $"{(sameSector ? "(같은 섹터)" : $"→ 섹터{targetSector.SectorId}")} " +
                              $"[스레드:{Environment.CurrentManagedThreadId}]");

            targetSector.ReceiveWhisper(targetId, sender.Name, message);
        });
    }

    /// <summary>
    /// 귓속말 수신 — 다른 섹터의 플레이어가 보낸 메시지를 이 섹터의 플레이어에게 전달.
    /// ★ 귓속말도 반드시 대상 섹터의 DoAsync를 통해야 한다!
    ///   직접 Player 객체에 접근하면 다른 스레드에서 동시 접근 위험.
    /// </summary>
    public void ReceiveWhisper(string targetId, string senderName, string message)
    {
        DoAsync(() =>
        {
            if (!_players.TryGetValue(targetId, out var target))
            {
                Console.WriteLine($"  [섹터{SectorId}] ⚠ 귓속말 대상 {targetId} 없음");
                return;
            }
            Console.WriteLine($"  [섹터{SectorId}] 💬 귓속말 수신: {senderName}→{target.Name}: \"{message}\" " +
                              $"[스레드:{Environment.CurrentManagedThreadId}]");
        });
    }

    /// <summary>
    /// 섹터 이동 [1단계] — 구 섹터에서 플레이어 제거, 이동 중 플래그 설정.
    /// 이동 중(IsTransferring=true)인 플레이어는 공격 대상에서 제외된다.
    /// </summary>
    public void BeginTransferOut(string playerId, ZoneSector newSector,
        Action<Player> onRemoved)
    {
        DoAsync(() =>
        {
            if (!_players.Remove(playerId, out var player)) return;

            player.IsTransferring = true;
            Console.WriteLine($"  [섹터{SectorId}] → {player.Name} 섹터 이동 시작 " +
                              $"→ 섹터{newSector.SectorId} [스레드:{Environment.CurrentManagedThreadId}]");

            onRemoved(player);

            // [2단계] 신 섹터에 추가 — 신 섹터의 DoAsync에서 IsTransferring 해제
            newSector.AddPlayer(player);
        });
    }

    // ── 상태 출력 ──

    public void PrintStatus()
    {
        DoAsync(() =>
        {
            if (_players.Count == 0) return;
            Console.WriteLine($"    섹터{SectorId} [{OriginX:F0},{OriginY:F0}]~" +
                              $"[{OriginX + Width:F0},{OriginY + Height:F0}]: {_players.Count}명");
            foreach (var p in _players.Values)
                Console.WriteLine($"      - {p.Name} ({p.X:F0},{p.Y:F0}) HP:{p.Hp}/{p.MaxHp} " +
                                  $"{(p.IsAlive ? "생존" : "사망")}{(p.IsTransferring ? " [이동중]" : "")}");
        });
    }
}
