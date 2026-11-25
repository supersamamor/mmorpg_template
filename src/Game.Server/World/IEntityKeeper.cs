namespace Game.Server.World;

/// <summary>
/// Hears of saved characters (<see cref="Entity.Save"/>) as they leave the world, on the tick thread of the instance that holds them, in phase 3, before any system forgets
/// them (T-702). The world knows nothing of saving beyond this.
/// </summary>
public interface IEntityKeeper
{
    /// <summary>Whether a character asked to leave may leave in this tick; if not, it is asked again in the next.</summary>
    bool MayLeave(Entity entity, long tick);

    /// <summary>The character leaves now; its state is still whole.</summary>
    void Leaving(Entity entity);

    /// <summary>The character was removed on its way here and never arrives: a warp in flight, or a spawn that never entered.</summary>
    void Dropped(Entity entity, MapChangeReason reason);
}
