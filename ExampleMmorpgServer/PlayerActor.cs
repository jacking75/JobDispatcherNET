using JobDispatcherNET;

namespace ExampleMmorpgServer;

/// <summary>
/// 플레이어 단위 Actor. 각 플레이어가 자신만의 AsyncExecutable을 가지므로
/// 서로 다른 플레이어의 패킷은 완전 병렬로 처리된다.
///
/// 같은 플레이어에 대한 요청(예: 여러 명이 동시에 같은 대상을 공격)은
/// 이 Actor의 큐에서 자연스럽게 직렬화되어 lock이 필요 없다.
/// </summary>
public class PlayerActor : AsyncExecutable
{
    private readonly Player _player;
    private readonly SpatialIndex _spatialIndex;

    public Player Player => _player;
    public string PlayerId => _player.PlayerId;

    public PlayerActor(Player player, SpatialIndex spatialIndex)
    {
        _player = player;
        _spatialIndex = spatialIndex;
    }

    /// <summary>
    /// 이동 처리 — 자기 Actor에서 실행, 완전 병렬
    /// </summary>
    public void Move(float newX, float newY)
    {
        DoAsync(() =>
        {
            if (!_player.IsAlive) return;

            float oldX = _player.X, oldY = _player.Y;
            _player.X = newX;
            _player.Y = newY;

            // 공간 인덱스 갱신 (ConcurrentDictionary — 최소 lock)
            _spatialIndex.UpdatePosition(_player, oldX, oldY);

            Console.WriteLine($"  [{_player.Name}] 이동 ({oldX:F1},{oldY:F1}) → ({newX:F1},{newY:F1}) " +
                              $"[스레드:{Environment.CurrentManagedThreadId}]");
        });
    }

    /// <summary>
    /// 근접 공격 — 공격자 Actor에서 스냅샷 캡처 후, 대상 Actor로 전달
    /// </summary>
    public void MeleeAttack(PlayerActor targetActor, float meleeRange)
    {
        DoAsync(() =>
        {
            if (!_player.IsAlive)
            {
                Console.WriteLine($"  [{_player.Name}] 사망 상태 — 공격 불가");
                return;
            }

            // 1단계: 공격자 스냅샷 캡처 (이 Actor 안이므로 안전)
            var snapshot = new AttackerSnapshot(
                _player.PlayerId, _player.Name,
                _player.X, _player.Y, _player.Attack);

            // 2단계: 대상 Actor의 DoAsync로 전달 → 대상 Actor 스레드에서 처리
            targetActor.ReceiveMeleeDamage(snapshot, meleeRange);
        });
    }

    /// <summary>
    /// 범위 공격 — 공격자 Actor에서 스냅샷 캡처 후, 주변 플레이어 각각의 Actor로 전달
    /// </summary>
    public void AreaAttack(float centerX, float centerY, float radius, float maxCastRange,
        Func<string, PlayerActor?> findActor)
    {
        DoAsync(() =>
        {
            if (!_player.IsAlive)
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

            // 공간 인덱스에서 범위 내 플레이어 조회 (ConcurrentDictionary 읽기 — 최소 lock)
            var nearbyPlayers = _spatialIndex.QueryRadius(centerX, centerY, radius);

            Console.WriteLine($"  [{_player.Name}] AoE 시전 중심({centerX:F1},{centerY:F1}) 반경{radius:F1} " +
                              $"대상탐색:{nearbyPlayers.Count}명 [스레드:{Environment.CurrentManagedThreadId}]");

            // 각 대상의 Actor로 데미지 전달 → 대상별 병렬 처리!
            foreach (var target in nearbyPlayers)
            {
                if (target.PlayerId == _player.PlayerId) continue;

                var targetActor = findActor(target.PlayerId);
                targetActor?.ReceiveAreaDamage(snapshot, centerX, centerY, radius);
            }
        });
    }

    /// <summary>
    /// 근접 데미지 수신 — 대상 Actor에서 실행 (공격자와 병렬, 같은 대상은 직렬)
    /// </summary>
    public void ReceiveMeleeDamage(AttackerSnapshot attacker, float meleeRange)
    {
        DoAsync(() =>
        {
            if (!_player.IsAlive)
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

            if (!_player.IsAlive)
                HandleDeath(attacker.Name);
        });
    }

    /// <summary>
    /// AoE 데미지 수신 — 대상 Actor에서 실행
    /// </summary>
    public void ReceiveAreaDamage(AttackerSnapshot attacker, float centerX, float centerY, float radius)
    {
        DoAsync(() =>
        {
            if (!_player.IsAlive) return;

            if (_player.DistanceTo(centerX, centerY) > radius) return;

            int rawDamage = (int)(attacker.Attack * 0.7f);
            int damage = _player.TakeDamage(rawDamage);

            Console.WriteLine($"  [{_player.Name}] AoE피격: {attacker.Name} → {_player.Name} " +
                              $"데미지:{damage} 남은HP:{_player.Hp} " +
                              $"[스레드:{Environment.CurrentManagedThreadId}]");

            if (!_player.IsAlive)
                HandleDeath(attacker.Name);
        });
    }

    private void HandleDeath(string killerName)
    {
        Console.WriteLine($"  ★ {_player.Name} 사망! (처치자: {killerName}) " +
                          $"[스레드:{Environment.CurrentManagedThreadId}]");

        // 5초 후 부활 — DoAsyncAfter로 이 Actor의 큐에 예약
        DoAsyncAfter(TimeSpan.FromSeconds(5), () =>
        {
            _player.Hp = _player.MaxHp;
            float oldX = _player.X, oldY = _player.Y;
            _player.X = Random.Shared.NextSingle() * 200f;
            _player.Y = Random.Shared.NextSingle() * 200f;

            _spatialIndex.UpdatePosition(_player, oldX, oldY);

            Console.WriteLine($"  ★ {_player.Name} 부활! ({_player.X:F1},{_player.Y:F1}) HP:{_player.Hp} " +
                              $"[스레드:{Environment.CurrentManagedThreadId}]");
        });
    }
}

/// <summary>
/// 공격자 스냅샷. 불변 데이터이므로 스레드 간 전달 시 안전하다.
/// </summary>
public readonly record struct AttackerSnapshot(
    string PlayerId, string Name,
    float X, float Y, int Attack);
