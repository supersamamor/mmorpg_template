using System.Collections.Concurrent;
using Game.Server.Accounts;
using Game.Server.Net;
using Game.Server.World;
using Game.Shared.Protocol;
using Game.Shared.Vocabulary;

namespace Game.Server.Admin;

/// <summary>
/// The checks the game asks of moderation (T-806), on every server process with a database, admin API or not: the database decides.
/// <list type="bullet">
/// <item><see cref="BanOfAsync"/>: the account service asks it at every sign-in and every <c>hello</c>.</item>
/// <item><see cref="MaySpeakAsync"/>: "may this account speak now?" Chat (T-901) calls it before it sends anything a player says. A mute is cached for <see cref="CacheMs"/> per account
/// (bounded), so a burst of lines costs one read; a mute or unmute made through this server's admin API is seen at once, one made through another process within the cache time.</item>
/// </list>
/// Thread-safe; never call it on a tick thread (it may read the database).
/// </summary>
public sealed class ModerationChecks(IModerationStore store, TimeProvider time)
{
    /// <summary>How long a mute answer is kept.</summary>
    public const int CacheMs = 5_000;

    /// <summary>Most accounts kept; past it the cache starts again.</summary>
    public const int MaxCached = 10_000;

    private readonly ConcurrentDictionary<Guid, (DateTime? MutedUntil, DateTime Checked)> _mutes = new();

    /// <summary>The account's ban in force now, or null.</summary>
    public Task<BanState?> BanOfAsync(Guid account, CancellationToken cancellation) => Moderation.BanOfAsync(store, account, time, cancellation);

    /// <summary>
    /// True when the account may speak now: no mute in force. When the database cannot answer it says false (a moderation check fails closed: a muted player must not get through a database
    /// outage), and the caller tells the player to try again.
    /// </summary>
    public async ValueTask<bool> MaySpeakAsync(Guid account, CancellationToken cancellation)
    {
        var now = time.GetUtcNow().UtcDateTime;
        if (_mutes.TryGetValue(account, out var known) && (now - known.Checked).TotalMilliseconds < CacheMs) return known.MutedUntil is not { } u || u <= now;
        Sanction? mute;
        try
        {
            mute = await store.ActiveSanctionAsync(account, SanctionKinds.Mute, now, cancellation).ConfigureAwait(false);
        }
        catch (StoreUnavailableException)
        {
            return false;
        }
        if (_mutes.Count >= MaxCached) _mutes.Clear();
        _mutes[account] = (mute?.Until, now);
        return mute is null;
    }

    /// <summary>Forgets what is cached for the account (its mute changed here).</summary>
    public void Forget(Guid account) => _mutes.TryRemove(account, out _);
}

/// <summary>
/// The ban watch (T-806): every <see cref="IntervalMs"/>, asks the database which accounts playing on this server are banned, and ends their games (<c>bye banned</c>, the character leaves
/// saved). A ban made through this server's admin API ends the game at once; this is what ends a game on <b>another</b> process with the same database, which the ban's own command cannot
/// reach (ADR-002 row 320): such a player keeps playing at most one interval (and a database round trip) after the ban. It runs on every server with a database.
/// </summary>
public sealed class BanWatch : IAsyncDisposable
{
    /// <summary>How often it looks.</summary>
    public const int IntervalMs = 10_000;

    private readonly CancellationTokenSource _stop = new();
    private readonly Task _loop;

    private BanWatch(IModerationStore store, Gateway gateway, TimeProvider time, IServerLog log, int intervalMs) =>
        _loop = Task.Run(() => RunAsync(store, gateway, time, log, intervalMs, _stop.Token));

    /// <summary>Starts watching.</summary>
    public static BanWatch Start(IModerationStore store, Gateway gateway, TimeProvider time, IServerLog log, int intervalMs = IntervalMs) => new(store, gateway, time, log, intervalMs);

    /// <summary>One look: ends the games of the banned accounts that play here. Returns how many accounts it ended.</summary>
    public static async Task<int> SweepAsync(IModerationStore store, Gateway gateway, TimeProvider time, IServerLog log, CancellationToken cancellation)
    {
        var playing = gateway.PlayingAccounts();
        if (playing.Count == 0) return 0;
        var now = time.GetUtcNow().UtcDateTime;
        var banned = await store.BannedAmongAsync(playing, now, cancellation).ConfigureAwait(false);
        foreach (var account in banned)
        {
            var ban = await store.ActiveSanctionAsync(account, SanctionKinds.Ban, now, cancellation).ConfigureAwait(false);
            var left = gateway.EndAccount(account, ByeReasons.Banned, Moderation.SecondsLeft(ban?.Until, now), "banned (the ban watch)");
            log.Info($"[admin] account {account} is banned: its game here ended ({left} character(s) left the world).");
        }
        return banned.Count;
    }

    private static async Task RunAsync(IModerationStore store, Gateway gateway, TimeProvider time, IServerLog log, int intervalMs, CancellationToken stop)
    {
        while (!stop.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(intervalMs, stop).ConfigureAwait(false);
                await SweepAsync(store, gateway, time, log, stop).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stop.IsCancellationRequested)
            {
                return;
            }
            catch (StoreUnavailableException e)
            {
                log.Warn($"[admin] the ban watch could not look: {e.Message}.");
            }
            catch (Exception e)
            {
                log.Error("[admin] the ban watch failed.", e);
            }
        }
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;   // once: the host stops it early and disposes it again at the end
        await _stop.CancelAsync().ConfigureAwait(false);
        await _loop.ConfigureAwait(false);
        _stop.Dispose();
    }

    private int _disposed;
}
