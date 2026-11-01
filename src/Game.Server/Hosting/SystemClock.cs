using System.Diagnostics;
using Game.Server.World;
using Game.Shared.Time;

namespace Game.Server.Hosting;

/// <summary>The real monotonic clock: <see cref="Stopwatch"/> time since this clock was created. Host only.</summary>
public sealed class SystemClock : IClock
{
    private readonly long _origin = Stopwatch.GetTimestamp();

    /// <inheritdoc />
    public long NowMilliseconds => (long)Stopwatch.GetElapsedTime(_origin).TotalMilliseconds;
}

/// <summary>Thread-safe console log with a timestamp and level on each line.</summary>
public sealed class ConsoleLog : IServerLog
{
    private readonly object _lock = new();

    /// <inheritdoc />
    public void Info(string message) => Write("INFO ", message, null);

    /// <inheritdoc />
    public void Warn(string message) => Write("WARN ", message, null);

    /// <inheritdoc />
    public void Error(string message, Exception? exception = null) => Write("ERROR", message, exception);

    private void Write(string level, string message, Exception? exception)
    {
        var line = $"{DateTime.Now:HH:mm:ss.fff} {level} {message}";
        lock (_lock)
        {
            Console.WriteLine(line);
            if (exception is not null) Console.WriteLine(exception);
        }
    }
}
