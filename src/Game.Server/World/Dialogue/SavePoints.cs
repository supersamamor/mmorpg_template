using System.Collections.Concurrent;
using Game.Shared.World;

namespace Game.Server.World.Dialogue;

/// <summary>Where a character comes back after death and where <c>teleport</c> <c>to_save_point</c> goes: a cell of a map.</summary>
public readonly record struct SavePoint(string MapId, GridCell Cell);

/// <summary>
/// Characters' save points (T-601). A character without one uses its map's respawn point (<c>CombatContent.RespawnPointFor</c>). Shared by every
/// instance: the dialogue system sets it, combat reads it. Saved with the character (T-702).
/// </summary>
public interface ISavePointStore
{
    /// <summary>The character's save point, if it set one.</summary>
    bool TryGet(EntityId player, out SavePoint point);

    /// <summary>Sets (replaces) the character's save point.</summary>
    void Set(EntityId player, SavePoint point);

    /// <summary>Forgets a character that left the world; a saved one has been copied for its last save (T-702).</summary>
    void Release(EntityId player);
}

/// <summary>Save points of the characters in the world, in memory, keyed by entity id.</summary>
public sealed class InMemorySavePointStore : ISavePointStore
{
    private readonly ConcurrentDictionary<EntityId, SavePoint> _points = new();

    /// <summary>Characters with a save point.</summary>
    public int Count => _points.Count;

    /// <inheritdoc />
    public bool TryGet(EntityId player, out SavePoint point) => _points.TryGetValue(player, out point);

    /// <inheritdoc />
    public void Set(EntityId player, SavePoint point) => _points[player] = point;

    /// <inheritdoc />
    public void Release(EntityId player) => _points.TryRemove(player, out _);
}
