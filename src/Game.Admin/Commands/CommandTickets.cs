using System.Security.Cryptography;

namespace Game.Admin.Commands;

/// <summary>A command waiting for its confirmation: exactly what the confirmation showed.</summary>
public sealed record PendingCommand(string StaffSessionId, string Action, string Json, DateTimeOffset ExpiresAt);

/// <summary>
/// The confirmations the panel has asked for. The browser gets only a random ticket; what will be sent stays here, so it cannot differ from what was shown. A ticket is
/// taken once: a double press, a reload or the back button cannot send a command again. Tickets last five minutes, belong to one sign-in, and are bounded. Thread-safe.
/// </summary>
public sealed class CommandTickets(TimeProvider time)
{
    /// <summary>How long a confirmation waits.</summary>
    public static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(5);

    /// <summary>The most waiting at once; the oldest makes room.</summary>
    public const int MaxPending = 500;

    private readonly object _lock = new();
    private readonly Dictionary<string, PendingCommand> _pending = new(StringComparer.Ordinal);

    /// <summary>Keeps a command and returns its ticket.</summary>
    public string Issue(string staffSessionId, string action, string json)
    {
        var ticket = Convert.ToHexString(RandomNumberGenerator.GetBytes(16));
        var now = time.GetUtcNow();
        lock (_lock)
        {
            foreach (var old in _pending.Where(p => p.Value.ExpiresAt <= now).Select(p => p.Key).ToList()) _pending.Remove(old);
            while (_pending.Count >= MaxPending) _pending.Remove(_pending.MinBy(p => p.Value.ExpiresAt).Key);
            _pending[ticket] = new PendingCommand(staffSessionId, action, json, now + Lifetime);
        }
        return ticket;
    }

    /// <summary>Takes the command of a ticket: once, only by the sign-in it was made for, only for its action, only in time. Otherwise null.</summary>
    public PendingCommand? Take(string? ticket, string staffSessionId, string action)
    {
        lock (_lock)
        {
            if (ticket is null || !_pending.TryGetValue(ticket, out var pending) || pending.StaffSessionId != staffSessionId || pending.Action != action) return null;
            _pending.Remove(ticket);
            return pending.ExpiresAt > time.GetUtcNow() ? pending : null;
        }
    }
}
