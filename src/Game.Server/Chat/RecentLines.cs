namespace Game.Server.Chat;

/// <summary>One line as the server keeps it for reports (T-901), in memory only.</summary>
public sealed record SaidLine(long Id, string Channel, Guid? SenderAccountId, Guid? SenderCharacterId, string Text, DateTime SaidAt);

/// <summary>
/// The lines of the last <see cref="ChatOptions.ReportWindowSeconds"/>, in memory, so a player can report one it received and the server stores what was really said (the client never
/// sends the text). Each account that plays keeps the numbers of the last <see cref="ChatOptions.ReportableLinesPerPlayer"/> lines that reached it: a report must name one of those. At most
/// <see cref="ChatOptions.RecentLinesMax"/> lines in all; the oldest go first. Nothing here is written anywhere: a restart forgets it. Thread-safe.
/// </summary>
public sealed class RecentLines(ChatOptions options, TimeProvider time)
{
    private readonly object _lock = new();
    private readonly Dictionary<long, SaidLine> _lines = new();
    private readonly Queue<SaidLine> _order = new();
    private readonly Dictionary<Guid, Received> _received = new();
    private readonly Dictionary<(long Line, Guid Reporter), bool> _reported = new();
    private long _next;

    /// <summary>Lines kept now.</summary>
    public int Count
    {
        get { lock (_lock) return _lines.Count; }
    }

    /// <summary>A new line's number: one more than the last, never 0.</summary>
    public long NextId() => Interlocked.Increment(ref _next);

    /// <summary>Keeps <paramref name="line"/> and notes it reached each of <paramref name="recipients"/> (accounts).</summary>
    public void Keep(SaidLine line, IEnumerable<Guid> recipients)
    {
        lock (_lock)
        {
            Expire(time.GetUtcNow().UtcDateTime);
            while (_order.Count >= options.RecentLinesMax) Drop(_order.Dequeue());
            _lines[line.Id] = line;
            _order.Enqueue(line);
            foreach (var account in recipients)
            {
                if (!_received.TryGetValue(account, out var ring)) _received[account] = ring = new Received(options.ReportableLinesPerPlayer);
                ring.Add(line.Id);
            }
        }
    }

    /// <summary>
    /// The line <paramref name="id"/> if it reached <paramref name="reporter"/> and is still kept, else null. <paramref name="already"/>: the reporter reported it before (then the line is
    /// returned too, and nothing is marked).
    /// </summary>
    public SaidLine? Reportable(long id, Guid reporter, out bool already)
    {
        already = false;
        lock (_lock)
        {
            Expire(time.GetUtcNow().UtcDateTime);
            if (!_lines.TryGetValue(id, out var line) || !_received.TryGetValue(reporter, out var ring) || !ring.Contains(id)) return null;
            already = _reported.ContainsKey((id, reporter));
            return line;
        }
    }

    /// <summary>Marks the line as reported by <paramref name="reporter"/>, so a second report is refused.</summary>
    public void MarkReported(long id, Guid reporter)
    {
        lock (_lock)
            if (_lines.ContainsKey(id)) _reported[(id, reporter)] = true;
    }

    /// <summary>Forgets what reached accounts that do not play here any more and have had no line for the window.</summary>
    public void Prune(IReadOnlyCollection<Guid> playing)
    {
        lock (_lock)
        {
            Expire(time.GetUtcNow().UtcDateTime);
            var keep = playing as ISet<Guid> ?? playing.ToHashSet();
            foreach (var account in _received.Keys.ToList())
                if (!keep.Contains(account) && !_received[account].Ids.Any(_lines.ContainsKey)) _received.Remove(account);
        }
    }

    private void Expire(DateTime now)
    {
        var oldest = now.AddSeconds(-options.ReportWindowSeconds);
        while (_order.Count > 0 && _order.Peek().SaidAt < oldest) Drop(_order.Dequeue());
    }

    private void Drop(SaidLine line)
    {
        _lines.Remove(line.Id);
        if (_reported.Count > 0)
            foreach (var key in _reported.Keys.Where(k => k.Line == line.Id).ToList()) _reported.Remove(key);
    }

    /// <summary>The numbers of the last lines one account received (a ring).</summary>
    private sealed class Received(int size)
    {
        private readonly long[] _ids = new long[size];
        private int _at;

        public IEnumerable<long> Ids => _ids.Where(i => i != 0);

        public void Add(long id)
        {
            _ids[_at] = id;
            _at = (_at + 1) % _ids.Length;
        }

        public bool Contains(long id) => Array.IndexOf(_ids, id) >= 0;
    }
}
