using System.Collections.Concurrent;
using JobDispatcherNET;

namespace ExampleMmorpgServer;

/// <summary>
/// 게임 존. 단일 존 내에서 플레이어 Actor 기반 병렬 처리를 한다.
///
/// 스레딩 모델:
///   - 각 플레이어가 자신만의 PlayerActor(AsyncExecutable)를 소유
///   - 서로 다른 플레이어의 패킷은 완전 병렬 처리
///   - 같은 플레이어 대상 작업은 해당 Actor에서 자동 직렬화 (lock 없음)
///   - 공간 조회는 ConcurrentDictionary 기반 SpatialIndex (최소 lock)
/// </summary>
public class GameZone
{
    private readonly string _name;
    private readonly float _width;
    private readonly float _height;
    private readonly SpatialIndex _spatialIndex;
    private readonly ConcurrentDictionary<string, PlayerActor> _actors = [];

    private const float MeleeRange = 3.0f;
    private const float MaxCastRange = 15.0f;

    public string Name => _name;

    public GameZone(string name, float width, float height, float cellSize = 50f)
    {
        _name = name;
        _width = width;
        _height = height;
        _spatialIndex = new SpatialIndex(cellSize);
    }

    // ── 플레이어 입장/퇴장 ──

    public void EnterZone(Player player, float spawnX, float spawnY)
    {
        player.X = Math.Clamp(spawnX, 0, _width);
        player.Y = Math.Clamp(spawnY, 0, _height);

        var actor = new PlayerActor(player, _spatialIndex);
        _actors[player.PlayerId] = actor;
        _spatialIndex.Add(player);

        Console.WriteLine($"[{_name}] {player.Name} 입장 ({player.X:F1},{player.Y:F1}) " +
                          $"HP:{player.Hp} ATK:{player.Attack} DEF:{player.Defense}");
    }

    public void LeaveZone(string playerId)
    {
        if (_actors.TryRemove(playerId, out var actor))
        {
            _spatialIndex.Remove(actor.Player);
            Console.WriteLine($"[{_name}] {actor.Player.Name} 퇴장");
        }
    }

    // ── 패킷 라우팅: 해당 플레이어의 Actor로 전달 ──

    public void HandleMove(string playerId, float newX, float newY)
    {
        if (_actors.TryGetValue(playerId, out var actor))
        {
            float clampedX = Math.Clamp(newX, 0, _width);
            float clampedY = Math.Clamp(newY, 0, _height);
            actor.Move(clampedX, clampedY);
        }
    }

    public void HandleMeleeAttack(string attackerId, string targetId)
    {
        if (_actors.TryGetValue(attackerId, out var attacker) &&
            _actors.TryGetValue(targetId, out var target))
        {
            attacker.MeleeAttack(target, MeleeRange);
        }
    }

    public void HandleAreaAttack(string attackerId, float centerX, float centerY, float radius)
    {
        if (_actors.TryGetValue(attackerId, out var attacker))
        {
            attacker.AreaAttack(centerX, centerY, radius, MaxCastRange,
                id => _actors.TryGetValue(id, out var a) ? a : null);
        }
    }

    // ── 상태 출력 ──

    public void PrintStatus()
    {
        Console.WriteLine($"\n  [{_name}] ({_width}x{_height}) 플레이어: {_actors.Count}명");
        foreach (var actor in _actors.Values)
        {
            var p = actor.Player;
            Console.WriteLine($"    - {p.Name} ({p.X:F1},{p.Y:F1}) HP:{p.Hp}/{p.MaxHp} " +
                              $"{(p.IsAlive ? "생존" : "사망")}");
        }
    }

    public async Task DisposeAllActorsAsync()
    {
        foreach (var actor in _actors.Values)
            await actor.DisposeAsync();
    }
}
