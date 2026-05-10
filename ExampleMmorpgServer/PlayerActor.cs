using JobDispatcherNET;

namespace ExampleMmorpgServer;

/// <summary>
/// 플레이어 단위 Actor. 각 플레이어가 자신만의 AsyncExecutable을 가지므로
/// 서로 다른 플레이어의 패킷은 완전 병렬로 처리된다.
///
/// 같은 플레이어에 대한 요청(예: 여러 명이 동시에 같은 대상을 공격)은
/// 이 Actor의 큐에서 자연스럽게 직렬화되어 lock이 필요 없다.
///
/// 라이브러리 활용 포인트:
///   - 자기복제 heartbeat (StartRegen → DoAsyncAfter(RegenInterval, RegenTick))
///   - actor 간 메시지 패싱 (대상 lookup은 GameZone 큐로 위임 → race 없음)
///   - Despawn 시 자기 큐에서 spatial 제거 (Move/Damage 등 잔여 작업과 직렬화)
/// </summary>
public class PlayerActor : AsyncExecutable
{
    private readonly Player _player;
    private readonly GameZone _zone;
    private volatile bool _despawned;

    private static readonly TimeSpan RegenInterval = TimeSpan.FromSeconds(3);
    private const int RegenAmount = 30;

    public Player Player => _player;
    public string PlayerId => _player.PlayerId;
    public bool Despawned => _despawned;

    public PlayerActor(Player player, GameZone zone)
    {
        _player = player;
        _zone = zone;
    }

    /// <summary>
    /// 자가 회복 heartbeat 시작. 이후 DoAsyncAfter로 자기 큐에 다음 회복을 예약 →
    /// 자기복제 패턴으로 별도 타이머 스레드 없이 주기 작업이 동작한다.
    /// </summary>
    public void StartRegen() => DoAsync(RegenTick);

    private void RegenTick()
    {
        if (_despawned) return;

        if (_player.IsAlive && _player.Hp < _player.MaxHp)
        {
            int before = _player.Hp;
            _player.Hp = Math.Min(_player.MaxHp, _player.Hp + RegenAmount);
            int healed = _player.Hp - before;
            if (healed > 0)
            {
                Console.WriteLine($"  [{_player.Name}] 자동회복 +{healed} ({before}→{_player.Hp}) " +
                                  $"[스레드:{Environment.CurrentManagedThreadId}]");
                _player.SendPacket?.Invoke($"HP_UPDATE|{_player.Hp}|{_player.MaxHp}");
            }
        }

        DoAsyncAfter(RegenInterval, RegenTick);
    }

    /// <summary>이동 처리 — 자기 Actor에서 실행, 완전 병렬</summary>
    public void Move(float newX, float newY)
    {
        DoAsync(() =>
        {
            if (_despawned || !_player.IsAlive) return;

            float oldX = _player.X, oldY = _player.Y;
            _player.X = newX;
            _player.Y = newY;

            _zone.Spatial.UpdatePosition(_player, oldX, oldY);

            Console.WriteLine($"  [{_player.Name}] 이동 ({oldX:F1},{oldY:F1}) → ({newX:F1},{newY:F1}) " +
                              $"[스레드:{Environment.CurrentManagedThreadId}]");
            _player.SendPacket?.Invoke($"MOVE_OK|{newX:F1}|{newY:F1}");
        });
    }

    /// <summary>
    /// 근접 공격 — 공격자 Actor에서 스냅샷 캡처 후, 대상 ID를 GameZone 큐로 전달.
    /// 대상 lookup이 GameZone 큐 안에서 일어나므로 _actors race가 발생하지 않는다.
    /// </summary>
    public void MeleeAttack(string targetId, float meleeRange)
    {
        DoAsync(() =>
        {
            if (_despawned || !_player.IsAlive)
            {
                Console.WriteLine($"  [{_player.Name}] 사망 상태 — 공격 불가");
                return;
            }

            var snapshot = new AttackerSnapshot(
                _player.PlayerId, _player.Name,
                _player.X, _player.Y, _player.Attack);

            _zone.SendMeleeDamage(targetId, snapshot, meleeRange);
        });
    }

    /// <summary>
    /// 범위 공격 — 공격자 Actor에서 스냅샷 캡처 후, GameZone 큐에 fan-out을 위임.
    /// spatial 조회와 _actors lookup 모두 GameZone 큐에서 처리되어 race가 없다.
    /// </summary>
    public void AreaAttack(float centerX, float centerY, float radius, float maxCastRange)
    {
        DoAsync(() =>
        {
            if (_despawned || !_player.IsAlive)
            {
                Console.WriteLine($"  [{_player.Name}] 사망 상태 — 공격 불가");
                return;
            }

            float distToCenter = _player.DistanceTo(centerX, centerY);
            if (distToCenter > maxCastRange)
            {
                Console.WriteLine($"  [{_player.Name}] 시전 거리 초과 ({distToCenter:F1})");
                return;
            }

            var snapshot = new AttackerSnapshot(
                _player.PlayerId, _player.Name,
                _player.X, _player.Y, _player.Attack);

            Console.WriteLine($"  [{_player.Name}] AoE 시전 중심({centerX:F1},{centerY:F1}) 반경{radius:F1} " +
                              $"[스레드:{Environment.CurrentManagedThreadId}]");

            _zone.DispatchAreaDamage(snapshot, centerX, centerY, radius);
        });
    }

    /// <summary>근접 데미지 수신 — 대상 Actor에서 실행 (공격자와 병렬, 같은 대상은 직렬)</summary>
    public void ReceiveMeleeDamage(AttackerSnapshot attacker, float meleeRange)
    {
        DoAsync(() =>
        {
            if (_despawned || !_player.IsAlive)
            {
                Console.WriteLine($"  [{_player.Name}] 이미 사망 — {attacker.Name}의 공격 무효");
                return;
            }

            float dist = _player.DistanceTo(attacker.X, attacker.Y);
            if (dist > meleeRange)
            {
                Console.WriteLine($"  [{_player.Name}] {attacker.Name}의 공격 거리 초과 ({dist:F1})");
                return;
            }

            int damage = _player.TakeDamage(attacker.Attack);
            Console.WriteLine($"  [{_player.Name}] 근접피격: {attacker.Name} → {_player.Name} " +
                              $"데미지:{damage} 남은HP:{_player.Hp} " +
                              $"[스레드:{Environment.CurrentManagedThreadId}]");
            _player.SendPacket?.Invoke($"DAMAGE|{attacker.PlayerId}|{damage}|{_player.Hp}");

            if (!_player.IsAlive)
                HandleDeath(attacker.Name);
        });
    }

    /// <summary>AoE 데미지 수신 — 대상 Actor에서 실행</summary>
    public void ReceiveAreaDamage(AttackerSnapshot attacker, float centerX, float centerY, float radius)
    {
        DoAsync(() =>
        {
            if (_despawned || !_player.IsAlive) return;
            if (_player.DistanceTo(centerX, centerY) > radius) return;

            int rawDamage = (int)(attacker.Attack * 0.7f);
            int damage = _player.TakeDamage(rawDamage);

            Console.WriteLine($"  [{_player.Name}] AoE피격: {attacker.Name} → {_player.Name} " +
                              $"데미지:{damage} 남은HP:{_player.Hp} " +
                              $"[스레드:{Environment.CurrentManagedThreadId}]");
            _player.SendPacket?.Invoke($"DAMAGE|{attacker.PlayerId}|{damage}|{_player.Hp}");

            if (!_player.IsAlive)
                HandleDeath(attacker.Name);
        });
    }

    /// <summary>
    /// Despawn — 자기 큐에서 _despawned 플래그 + spatial 제거.
    /// GameZone이 _actors에서 제거한 직후 호출되며, 이 시점 이후 큐에 들어오는
    /// 모든 작업은 _despawned 체크로 무시된다. 자기복제 RegenTick도 자연 종료.
    /// </summary>
    public void Despawn()
    {
        DoAsync(() =>
        {
            if (_despawned) return;
            _despawned = true;
            _zone.Spatial.Remove(_player);
            Console.WriteLine($"  [{_player.Name}] 디스폰 (spatial 제거) " +
                              $"[스레드:{Environment.CurrentManagedThreadId}]");
        });
    }

    private void HandleDeath(string killerName)
    {
        Console.WriteLine($"  ★ {_player.Name} 사망! (처치자: {killerName}) " +
                          $"[스레드:{Environment.CurrentManagedThreadId}]");
        _player.SendPacket?.Invoke($"DEATH|{killerName}");

        // 5초 후 부활 — DoAsyncAfter로 이 Actor의 큐에 예약
        DoAsyncAfter(TimeSpan.FromSeconds(5), () =>
        {
            if (_despawned) return;

            float oldX = _player.X, oldY = _player.Y;
            _player.Hp = _player.MaxHp;
            _player.X = Random.Shared.NextSingle() * 200f;
            _player.Y = Random.Shared.NextSingle() * 200f;

            _zone.Spatial.UpdatePosition(_player, oldX, oldY);

            Console.WriteLine($"  ★ {_player.Name} 부활! ({_player.X:F1},{_player.Y:F1}) HP:{_player.Hp} " +
                              $"[스레드:{Environment.CurrentManagedThreadId}]");
            _player.SendPacket?.Invoke($"RESPAWN|{_player.X:F1}|{_player.Y:F1}|{_player.Hp}");
        });
    }
}

/// <summary>공격자 스냅샷. 불변 데이터이므로 스레드 간 전달 시 안전.</summary>
public readonly record struct AttackerSnapshot(
    string PlayerId, string Name,
    float X, float Y, int Attack);
