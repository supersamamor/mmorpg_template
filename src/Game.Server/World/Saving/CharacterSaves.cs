using Game.Server.Accounts;
using Game.Server.Hosting;
using Game.Shared.Protocol;
using Game.Shared.Time;

namespace Game.Server.World.Saving;

/// <summary>How saving runs: server options, not content.</summary>
public sealed record CharacterSaveOptions
{
    /// <summary>How often a character in the world is saved (<c>--save-interval</c>, seconds).</summary>
    public int IntervalMs { get; init; } = 60_000;

    /// <summary>How long a shutdown waits for the saves (<c>--save-shutdown-wait</c>, seconds).</summary>
    public int ShutdownWaitMs { get; init; } = 20_000;

    /// <summary>Least time between two saves of one character that nothing waits for.</summary>
    public int SpacingMs { get; init; } = 1_000;

    /// <summary>The first wait after a failed save; it doubles up to <see cref="RetryCapMs"/>.</summary>
    public int RetryBaseMs { get; init; } = 500;

    /// <summary>The longest wait between attempts.</summary>
    public int RetryCapMs { get; init; } = 15_000;

    /// <summary>A character whose saves have failed for this long is at risk, and the operator is told.</summary>
    public int AtRiskMs { get; init; } = 30_000;

    /// <summary>How long a login waits for the character's last save to land before it is refused.</summary>
    public int LoginWaitMs { get; init; } = 3_000;

    /// <summary>Groups written at once.</summary>
    public int Writers { get; init; } = 4;

    /// <summary>How often the run says it is alive.</summary>
    public int BeatMs { get; init; } = 5_000;
}

/// <summary>The answer to a login that wants its character: the ticket to enter with and its state (null: a new character), or a <see cref="ByeReasons"/> term.</summary>
public sealed record EnterResult(CharacterTicket? Ticket, LoadedCharacter? Loaded, string? Refusal)
{
    internal static EnterResult Refuse(string reason) => new(null, null, reason);
}

/// <summary>How a drain ended: every save landed, or which characters' did not.</summary>
public sealed record DrainReport(IReadOnlyList<Guid> Failed, TimeSpan Took);

/// <summary>Counters of the saves since the server started.</summary>
public sealed record CharacterSaveStats(long Saves, long Groups, long LargestGroup, long Failures, long Lost, int Held, int Waiting, int AtRisk);

/// <summary>
/// The saved characters this server holds (T-702, ARCHITECTURE §3 "Saving characters"): claims and loads them before they enter, writes the copies the tick threads submit on
/// threads of its own, and gives them up when their last save has landed. Thread-safe; <see cref="Submit"/> and <see cref="NextItemId"/> are the only members a tick thread may call.
/// </summary>
/// <remarks>
/// <para><b>Order.</b> Per character only the newest copy waits, one is written at a time, and the store refuses an older one than it has. A copy with links (its character picked up
/// something another put down) is written together with the waiting copies it links to, and theirs, in one transaction, or after they have landed: never before. So a crash, at
/// any step, can lose what was last moved, but never leaves it with both.</para>
/// <para><b>Failure.</b> A failed write is tried again with backoff for as long as the server runs; the character plays on. One the server has lost (claimed elsewhere, or deleted)
/// is never written again, nor is anyone who holds something of its unsaved time: for them it is as if the server had crashed (<see cref="Lost"/>).</para>
/// </remarks>
public sealed class CharacterSaves
{
    /// <summary>Bits of an item id that count within one server run; the rest is the run's number.</summary>
    public const int ItemIdRunShift = 40;

    private readonly object _lock = new();
    private readonly Dictionary<Guid, CharacterTicket> _tickets = new();
    private readonly HashSet<CharacterTicket> _waiting = new();   // tickets with a copy to write
    private readonly SemaphoreSlim _wake = new(0);
    private readonly CancellationTokenSource _stop = new();
    private readonly List<Task> _workers = new();
    private readonly ICharacterStore _store;
    private readonly CharacterSaveOptions _options;
    private readonly IClock _clock;
    private readonly TimeProvider _time;
    private readonly IServerLog _log;
    private CharacterLoader? _loader;
    private long _run;
    private long _lastItem;
    private bool _closed;       // no more logins
    private long _saves, _groups, _largest, _failures, _lostCount;

    /// <summary>Creates the service; <see cref="StartAsync"/> registers the run.</summary>
    public CharacterSaves(ICharacterStore store, CharacterSaveOptions options, IClock clock, TimeProvider time, IServerLog log)
    {
        _store = store;
        _options = options;
        _clock = clock;
        _time = time;
        _log = log;
    }

    /// <summary>The server lost a character (or one that depends on it): its session must end and it must leave. Raised off the tick threads, not under the lock.</summary>
    public event Action<CharacterTicket>? Lost;

    /// <summary>The options.</summary>
    public CharacterSaveOptions Options => _options;

    /// <summary>The run's number, once started.</summary>
    public long Run => _run;

    /// <summary>Registers the run with the store. Throws <see cref="StoreUnavailableException"/>.</summary>
    public async Task StartAsync(CancellationToken cancellation = default)
    {
        _run = await _store.StartRunAsync(cancellation).ConfigureAwait(false);
        if (_run is < 1 or >= 1L << (63 - ItemIdRunShift)) throw new InvalidOperationException($"Server run {_run} is out of range for item ids.");
    }

    /// <summary>Starts writing. <paramref name="loader"/> checks loaded characters (it needs <see cref="NextItemId"/>, so it comes after the start).</summary>
    public void Open(CharacterLoader loader)
    {
        _loader = loader;
        for (var i = 0; i < Math.Max(1, _options.Writers); i++) _workers.Add(Task.Run(WriteLoopAsync));
        _workers.Add(Task.Run(BeatLoopAsync));
    }

    /// <summary>
    /// A new item id, unique for ever over every run and character: the run's number, which the database gives to no other run, above a counter that starts at 1 in each. Needs no
    /// database at hand, so a tick thread may call it.
    /// </summary>
    public long NextItemId()
    {
        var n = Interlocked.Increment(ref _lastItem);
        if (n >= 1L << ItemIdRunShift) throw new InvalidOperationException("This run has used up its item ids; restart the server.");
        return (_run << ItemIdRunShift) | n;
    }

    /// <summary>True while the character is held here: being loaded, in the world, or with a save that has not landed. Any thread.</summary>
    public bool Holds(Guid character)
    {
        lock (_lock) return _tickets.ContainsKey(character);
    }

    /// <summary>Counters.</summary>
    public CharacterSaveStats Stats
    {
        get
        {
            lock (_lock)
                return new CharacterSaveStats(Interlocked.Read(ref _saves), Interlocked.Read(ref _groups), Interlocked.Read(ref _largest), Interlocked.Read(ref _failures),
                    Interlocked.Read(ref _lostCount), _tickets.Count, _waiting.Count, _tickets.Values.Count(t => t.AtRisk));
        }
    }

    // ---- entering -----------------------------------------------------------------------------------------------------------------------

    /// <summary>
    /// Claims and loads a character for a login. Waits (bounded) for an earlier stay of it here to end, so it is never read from before its last save. Refused with
    /// <c>login_refused</c> (it does not exist, or is deleted), <c>save_pending</c> (its last save has not landed, or another live server holds it), <c>load_failed</c> (its state cannot
    /// be read) or <c>shutdown</c>; nothing is created or overwritten then. The caller spawns the character with the ticket, or gives it up (<see cref="GiveUp"/>).
    /// </summary>
    public async Task<EnterResult> EnterAsync(Guid character, CancellationToken cancellation)
    {
        TickThread.Forbid("Loading a character");
        var ticket = new CharacterTicket(character);
        var deadline = _clock.NowMilliseconds + _options.LoginWaitMs;
        while (true)
        {
            Task gone;
            lock (_lock)
            {
                if (_closed) return EnterResult.Refuse(ByeReasons.Shutdown);
                if (_tickets.TryAdd(character, ticket)) break;
                gone = _tickets[character].Gone.Task;
            }
            var left = deadline - _clock.NowMilliseconds;
            if (left <= 0)
            {
                _log.Warn($"[saves] character {character}: login refused, its last save has not landed.");
                return EnterResult.Refuse(ByeReasons.SavePending);
            }
            await Task.WhenAny(gone, Task.Delay((int)Math.Min(left, 100), cancellation)).ConfigureAwait(false);
            cancellation.ThrowIfCancellationRequested();
        }

        var claiming = _store.ClaimAsync(character, _run, CancellationToken.None);   // not cancelled with the connection: a claim that may have happened is given up, not forgotten
        try
        {
            var claim = await claiming.WaitAsync(cancellation).ConfigureAwait(false);
            switch (claim.Outcome)
            {
                case ClaimOutcome.NotFound:
                    End(ticket);
                    return EnterResult.Refuse(ByeReasons.LoginRefused);
                case ClaimOutcome.HeldElsewhere:
                    _log.Warn($"[saves] character {character}: login refused, another server holds it.");
                    End(ticket);
                    return EnterResult.Refuse(ByeReasons.SavePending);
            }
            ticket.Claim = claim.Claim;
            ticket.TakenVersion = claim.Version;
            ticket.LandedVersion = claim.Version;
            var loaded = claim.State is { } state ? _loader!.Load(state, _time.GetUtcNow().UtcDateTime) : null;
            foreach (var note in loaded?.Notes ?? []) _log.Warn($"[saves] character {character}: {note}.");
            return new EnterResult(ticket, loaded, null);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            // The connection is gone and must not wait for the database; the ticket stays until the claim is known and given up, so nobody else enters meanwhile.
            _ = GiveUpClaimAsync(ticket, claiming);
            throw;
        }
        catch (Exception e)
        {
            if (e is StoreUnavailableException) _log.Warn($"[saves] character {character}: load failed: {e.Message}.");
            else _log.Error($"[saves] character {character}: load failed.", e);
            await ReleaseAsync(ticket).ConfigureAwait(false);
            return EnterResult.Refuse(ByeReasons.LoadFailed);
        }
    }

    private async Task GiveUpClaimAsync(CharacterTicket ticket, Task<CharacterClaim> claiming)
    {
        try
        {
            ticket.Claim = (await claiming.ConfigureAwait(false)).Claim;
        }
        catch (Exception e)
        {
            _log.Warn($"[saves] character {ticket.CharacterId}: a claim nobody waits for failed: {e.Message}.");
        }
        await ReleaseAsync(ticket).ConfigureAwait(false);
    }

    /// <summary>The character with this ticket never entered the world: the server gives it up unchanged. Any thread; done in the background.</summary>
    public void GiveUp(CharacterTicket ticket)
    {
        lock (_lock)
            if (ticket.IsLost || ticket.Pending is not null || ticket.InFlight is not null) return;   // over already, or it has been saved after all: its last save ends it
        _ = Task.Run(() => ReleaseAsync(ticket));
    }

    private async Task ReleaseAsync(CharacterTicket ticket)
    {
        try
        {
            await _store.ReleaseAsync(ticket.CharacterId, _run, ticket.Claim, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception e)
        {
            // It stays marked as this run's: this server may claim it again, and any other once the run is over.
            _log.Warn($"[saves] character {ticket.CharacterId}: not released: {e.Message}.");
        }
        End(ticket);
    }

    private void End(CharacterTicket ticket)
    {
        lock (_lock) Remove(ticket);
        ticket.Gone.TrySetResult();
    }

    /// <summary>Under the lock.</summary>
    private void Remove(CharacterTicket ticket)
    {
        if (_tickets.TryGetValue(ticket.CharacterId, out var current) && ReferenceEquals(current, ticket)) _tickets.Remove(ticket.CharacterId);
        _waiting.Remove(ticket);
        ticket.Pending = null;
    }

    // ---- writing ------------------------------------------------------------------------------------------------------------------------

    /// <summary>Takes a copy to write. From a tick thread: a short lock and nothing else.</summary>
    public void Submit(CharacterSave save)
    {
        ArgumentNullException.ThrowIfNull(save);
        var ticket = save.Ticket;
        lock (_lock)
        {
            if (ticket.IsLost || ticket.Gone.Task.IsCompleted) return;
            ticket.Pending = save;   // an older copy that was still waiting is in the newer one
            if (save.Final && ticket.Failures == 0) ticket.NotBeforeMs = 0;   // somebody may be waiting to log in again
            if (ticket.UnsavedSinceMs == 0) ticket.UnsavedSinceMs = Math.Max(1, _clock.NowMilliseconds);
            _waiting.Add(ticket);
        }
        if (save.Final) _wake.Release();
    }

    /// <summary>
    /// Writes what waits until nothing does or <paramref name="wait"/> is over, with no new logins; then ends the run. For a shutdown, after every character has been asked to leave and
    /// while the instances still tick.
    /// </summary>
    public async Task<DrainReport> DrainAsync(TimeSpan wait)
    {
        var started = _clock.NowMilliseconds;
        lock (_lock)
        {
            _closed = true;
            foreach (var ticket in _waiting) ticket.NotBeforeMs = Math.Min(ticket.NotBeforeMs, started);   // the last chance: no backoff
        }
        while (_clock.NowMilliseconds - started < wait.TotalMilliseconds)
        {
            lock (_lock)
                if (_tickets.Count == 0) break;
            _wake.Release();
            await Task.Delay(20).ConfigureAwait(false);
        }
        await _stop.CancelAsync().ConfigureAwait(false);
        await Task.WhenAll(_workers).ConfigureAwait(false);   // a write in progress is bounded by the database's command timeout
        List<Guid> failed;
        lock (_lock) failed = _tickets.Keys.ToList();
        foreach (var character in failed) _log.Error($"[saves] character {character}: NOT SAVED at shutdown; it goes back to its last save.");
        try
        {
            // With something not saved the run ends all the same: the process is about to, and its claims would only keep the characters out until the lease ran out.
            await _store.EndRunAsync(_run, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception e)
        {
            _log.Warn($"[saves] the run was not ended in the database ({e.Message}); its characters are free when its lease runs out.");
        }
        return new DrainReport(failed, TimeSpan.FromMilliseconds(_clock.NowMilliseconds - started));
    }

    private async Task WriteLoopAsync()
    {
        while (!_stop.IsCancellationRequested)
        {
            List<CharacterSave>? group;
            List<CharacterTicket> doomed = new();
            lock (_lock) group = NextGroup(doomed);
            foreach (var ticket in doomed) Lose(ticket, "it holds something of a character that was lost");
            if (group is null)
            {
                try
                {
                    await _wake.WaitAsync(doomed.Count > 0 ? 0 : 50, _stop.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                continue;
            }
            await WriteAsync(group).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// The next copies to write in one transaction, marked as in flight, or null when none is due. Under the lock. Tickets that can never be saved again because of a lost one go to
    /// <paramref name="doomed"/>.
    /// </summary>
    private List<CharacterSave>? NextGroup(List<CharacterTicket> doomed)
    {
        var now = _clock.NowMilliseconds;
        foreach (var ticket in _waiting)
        {
            if (ticket.InFlight is not null || ticket.Pending is null || now < ticket.NotBeforeMs) continue;
            var group = new List<CharacterSave>();
            if (!Gather(ticket, group, doomed)) continue;
            foreach (var save in group) save.Ticket.InFlight = save;
            return group;
        }
        return null;
    }

    /// <summary>
    /// Adds the ticket's waiting copy and everything it must not land before: the copies it links to that have not landed, and theirs. False when one of those is being written, or
    /// is not taken yet (it will be within a tick or two), or never will be.
    /// </summary>
    private bool Gather(CharacterTicket ticket, List<CharacterSave> group, List<CharacterTicket> doomed)
    {
        var queue = new Queue<CharacterTicket>();
        queue.Enqueue(ticket);
        while (queue.Count > 0)
        {
            var next = queue.Dequeue();
            if (group.Exists(s => ReferenceEquals(s.Ticket, next))) continue;
            if (next.InFlight is not null || next.Pending is not { } save) return false;
            group.Add(save);
            foreach (var link in save.Links)
            {
                if (link.HasLanded) continue;
                if (link.Ticket.IsLost)
                {
                    doomed.Add(next);
                    return false;
                }
                if (link.Ticket.Pending is { } needed && needed.Version < link.Version) return false;
                queue.Enqueue(link.Ticket);
            }
        }
        return true;
    }

    private async Task WriteAsync(List<CharacterSave> group)
    {
        SaveResult? result = null;
        string? failure = null;
        try
        {
            result = await _store.SaveAsync(group, CancellationToken.None).ConfigureAwait(false);
        }
        catch (StoreUnavailableException e)
        {
            failure = e.Message;
        }
        catch (Exception e)
        {
            failure = e.GetType().Name;
            _log.Error("[saves] a save failed unexpectedly.", e);
        }

        switch (result)
        {
            case SaveResult.Saved:
                Landed(group);
                break;
            case SaveResult.Lost lost:
                lock (_lock) foreach (var save in group) Retry(save, _clock.NowMilliseconds);   // the others of the group are tried again without it, and find out
                foreach (var save in group)
                    if (lost.Characters.Contains(save.CharacterId)) Lose(save.Ticket, "another server holds it, or it is deleted");
                break;
            case SaveResult.Duplicated duplicated:
                _log.Error($"[saves] DUPLICATED ITEMS: {string.Join(", ", duplicated.Items)} of character(s) {string.Join(", ", group.Select(s => s.CharacterId))} belong to others in the database. Not saved.");
                lock (_lock) foreach (var save in group) Retry(save, _clock.NowMilliseconds);
                foreach (var save in group) Lose(save.Ticket, "an item of it is another character's");
                break;
            default:
                Failed(group, failure ?? "unknown");
                break;
        }
        _wake.Release();
    }

    private void Landed(List<CharacterSave> group)
    {
        var ended = new List<CharacterTicket>();
        var now = _clock.NowMilliseconds;
        lock (_lock)
        {
            foreach (var save in group)
            {
                var ticket = save.Ticket;
                ticket.LandedVersion = save.Version;
                ticket.InFlight = null;
                if (ticket.AtRisk) _log.Info($"[saves] character {ticket.CharacterId}: saved again after {now - ticket.UnsavedSinceMs} ms; no longer at risk.");
                ticket.AtRisk = false;
                ticket.UnsavedSinceMs = ReferenceEquals(ticket.Pending, save) ? 0 : now;
                ticket.Failures = 0;
                ticket.NotBeforeMs = ticket.Pending is { Final: true } ? 0 : now + _options.SpacingMs;
                if (ReferenceEquals(ticket.Pending, save))
                {
                    ticket.Pending = null;
                    _waiting.Remove(ticket);
                }
                if (!save.Final) continue;
                Remove(ticket);
                ended.Add(ticket);
            }
        }
        Interlocked.Add(ref _saves, group.Count);
        Interlocked.Increment(ref _groups);
        lock (_lock) _largest = Math.Max(_largest, group.Count);
        foreach (var ticket in ended) ticket.Gone.TrySetResult();
    }

    private void Failed(List<CharacterSave> group, string failure)
    {
        Interlocked.Increment(ref _failures);
        var now = _clock.NowMilliseconds;
        lock (_lock)
        {
            foreach (var save in group)
            {
                var ticket = save.Ticket;
                ticket.Failures++;
                Retry(save, now + Math.Min(_options.RetryCapMs, (long)_options.RetryBaseMs << Math.Min(ticket.Failures - 1, 20)));
                if (ticket.Failures == 1) _log.Warn($"[saves] character {ticket.CharacterId}: save {save.Version} failed ({failure}); trying again.");
                FlagIfAtRisk(ticket, now, failure);
            }
        }
    }

    /// <summary>Under the lock.</summary>
    private void FlagIfAtRisk(CharacterTicket ticket, long now, string why)
    {
        if (ticket.AtRisk || ticket.UnsavedSinceMs == 0 || now - ticket.UnsavedSinceMs < _options.AtRiskMs) return;
        ticket.AtRisk = true;
        _log.Error($"[saves] character {ticket.CharacterId} IS AT RISK: nothing saved for {now - ticket.UnsavedSinceMs} ms ({why}); it goes back to save {ticket.LandedVersion} if the server stops now.");
    }

    /// <summary>Under the lock: the copy is no longer in flight, and waits again (a newer one may have taken its place).</summary>
    private static void Retry(CharacterSave save, long notBefore)
    {
        save.Ticket.InFlight = null;
        save.Ticket.NotBeforeMs = notBefore;
    }

    /// <summary>The server has lost the character: nothing of it is saved any more, its session ends and it leaves the world.</summary>
    private void Lose(CharacterTicket ticket, string why)
    {
        lock (_lock)
        {
            if (ticket.IsLost) return;
            ticket.IsLost = true;
            Remove(ticket);
        }
        Interlocked.Increment(ref _lostCount);
        ticket.Gone.TrySetResult();
        _log.Error($"[saves] character {ticket.CharacterId} IS LOST to this server ({why}): it goes back to its last save ({ticket.LandedVersion}).");
        try
        {
            Lost?.Invoke(ticket);
        }
        catch (Exception e)
        {
            _log.Error("[saves] a listener of a lost character threw.", e);
        }
    }

    private async Task BeatLoopAsync()
    {
        while (!_stop.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(_options.BeatMs, _stop.Token).ConfigureAwait(false);
                lock (_lock)
                    foreach (var ticket in _waiting) FlagIfAtRisk(ticket, _clock.NowMilliseconds, $"its copy waits; {ticket.Failures} failure(s)");   // also one that waits for another's copy
                await _store.BeatAsync(_run, _stop.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception e)
            {
                _log.Warn($"[saves] the run's heartbeat failed ({e.Message}).");
            }
        }
    }
}
