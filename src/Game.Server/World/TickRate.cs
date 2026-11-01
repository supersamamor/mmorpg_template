namespace Game.Server.World;

/// <summary>The server tick rate. Locked decision (CLAUDE.md): 10 Hz. This is the only place it is defined.</summary>
public static class TickRate
{
    /// <summary>Ticks per second.</summary>
    public const int Hz = 10;

    /// <summary>Simulated milliseconds per tick.</summary>
    public const int IntervalMs = 1000 / Hz;
}
