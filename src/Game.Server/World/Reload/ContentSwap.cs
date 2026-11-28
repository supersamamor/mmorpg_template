using Game.Shared.Stats;

namespace Game.Server.World.Reload;

/// <summary>
/// One hot reload on its way to the instances (T-801): the new content, how a returning player's stat sheet is built from it, and who to tell when an
/// instance has swapped. <see cref="GameWorld.Reload"/> hands the same object to every instance; each calls <see cref="Swapped"/> once, on its tick thread.
/// </summary>
public sealed class ContentSwap
{
    private readonly Action<MapInstance, int, int, TimeSpan>? _swapped;
    private int _pending;

    /// <summary>
    /// Creates a swap of <paramref name="next"/>. <paramref name="playerSheet"/> builds a player's new stat sheet (base stats and job bonuses from its
    /// progression; worn items and passives are added by the systems as it comes back), or returns null to keep the old one. <paramref name="swapped"/>
    /// hears each instance: players re-entered, monsters and NPCs removed, how long it took.
    /// </summary>
    public ContentSwap(ServerContent next, Func<Entity, StatSheet?> playerSheet, Action<MapInstance, int, int, TimeSpan>? swapped = null)
    {
        Next = next ?? throw new ArgumentNullException(nameof(next));
        PlayerSheet = playerSheet ?? throw new ArgumentNullException(nameof(playerSheet));
        _swapped = swapped;
    }

    /// <summary>The content to swap in.</summary>
    public ServerContent Next { get; }

    /// <summary>Builds a returning player's sheet from <see cref="Next"/> (called on the tick thread).</summary>
    public Func<Entity, StatSheet?> PlayerSheet { get; }

    /// <summary>Instances that have not swapped yet (set by <see cref="GameWorld.Reload"/>).</summary>
    public int Pending => Volatile.Read(ref _pending);

    /// <summary>True once every instance it was handed to has swapped.</summary>
    public bool Done => Pending == 0;

    internal void Expect(int instances) => Volatile.Write(ref _pending, instances);

    internal void Swapped(MapInstance instance, int players, int removed, TimeSpan took)
    {
        Interlocked.Decrement(ref _pending);
        _swapped?.Invoke(instance, players, removed, took);
    }
}
