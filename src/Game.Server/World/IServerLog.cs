namespace Game.Server.World;

/// <summary>Minimal log sink used by the simulation core and the host. Implementations must be thread-safe.</summary>
public interface IServerLog
{
    /// <summary>Normal operation.</summary>
    void Info(string message);

    /// <summary>Something degraded but the server keeps going (overrun, dropped work).</summary>
    void Warn(string message);

    /// <summary>A failure, optionally with the exception that caused it.</summary>
    void Error(string message, Exception? exception = null);
}
