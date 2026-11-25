namespace Game.Server.Hosting;

/// <summary>
/// Marks the threads that tick map instances, so work that must never run there (the database, ARCHITECTURE §3 "Persistence") can refuse to: a blocked tick
/// stops a whole map.
/// </summary>
public static class TickThread
{
    [ThreadStatic] private static bool _current;

    /// <summary>True on a tick thread.</summary>
    public static bool IsCurrent => _current;

    /// <summary>Called by <see cref="InstanceRunner"/> at the start of its thread.</summary>
    internal static void Enter() => _current = true;

    /// <summary>Throws <see cref="InvalidOperationException"/> on a tick thread.</summary>
    public static void Forbid(string what)
    {
        if (_current) throw new InvalidOperationException(what + " must not run on a tick thread: queue it and answer with an intent or an event.");
    }
}
