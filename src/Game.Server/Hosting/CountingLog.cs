using Game.Server.World;

namespace Game.Server.Hosting;

/// <summary>A log that counts its errors and warnings on the way through (for the admin API's status, T-804). Thread-safe like the log it wraps.</summary>
public sealed class CountingLog(IServerLog inner) : IServerLog
{
    private long _errors;
    private long _warnings;

    /// <summary>Errors logged since the start.</summary>
    public long Errors => Interlocked.Read(ref _errors);

    /// <summary>Warnings logged since the start.</summary>
    public long Warnings => Interlocked.Read(ref _warnings);

    /// <inheritdoc />
    public void Info(string message) => inner.Info(message);

    /// <inheritdoc />
    public void Warn(string message)
    {
        Interlocked.Increment(ref _warnings);
        inner.Warn(message);
    }

    /// <inheritdoc />
    public void Error(string message, Exception? exception = null)
    {
        Interlocked.Increment(ref _errors);
        inner.Error(message, exception);
    }
}
