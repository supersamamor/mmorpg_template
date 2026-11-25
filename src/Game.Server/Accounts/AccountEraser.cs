using Game.Server.World;

namespace Game.Server.Accounts;

/// <summary>
/// Erases the accounts whose waiting time is over (T-703, ARCHITECTURE §10 "Accounts"): a pass at the start and then one every <see cref="PassInterval"/>, on a task of its own and never on a
/// tick thread. Each account is one transaction, so a process that dies in the middle leaves it whole for the next pass, and two servers at once share the work. An account one of whose
/// characters a live server still holds is left for a later pass.
/// </summary>
public sealed class AccountEraser : IAsyncDisposable
{
    /// <summary>Time between passes.</summary>
    public static readonly TimeSpan PassInterval = TimeSpan.FromMinutes(1);

    /// <summary>Most accounts a pass looks at: the rest waits for the next.</summary>
    public const int PerPass = 50;

    private readonly IAccountStore _store;
    private readonly TimeProvider _time;
    private readonly IServerLog _log;
    private readonly CancellationTokenSource _stop = new();
    private Task _loop = Task.CompletedTask;
    private bool _away;

    /// <summary>Creates it; <see cref="Start"/> also runs it.</summary>
    public AccountEraser(IAccountStore store, TimeProvider time, IServerLog log)
    {
        _store = store;
        _time = time;
        _log = log;
    }

    /// <summary>Creates it and starts its passes.</summary>
    public static AccountEraser Start(IAccountStore store, TimeProvider time, IServerLog log)
    {
        var eraser = new AccountEraser(store, time, log);
        eraser._loop = Task.Run(eraser.LoopAsync);
        return eraser;
    }

    /// <summary>One pass. Returns how many accounts were erased; a database that is away is logged once and tried again by the next pass.</summary>
    public async Task<int> PassAsync(CancellationToken cancellation = default)
    {
        var erased = 0;
        try
        {
            var now = _time.GetUtcNow().UtcDateTime;
            foreach (var account in await _store.ListErasableAsync(now, PerPass, cancellation).ConfigureAwait(false))
            {
                if (await _store.EraseAsync(account, now, cancellation).ConfigureAwait(false) != EraseOutcome.Erased) continue;
                erased++;
                _log.Info($"[accounts] account {account} is erased, with everything that was its.");
            }
            _away = false;
        }
        catch (StoreUnavailableException e)
        {
            if (!_away) _log.Warn($"[accounts] erasing deleted accounts waits: {e.Message}.");
            _away = true;
        }
        return erased;
    }

    private async Task LoopAsync()
    {
        using var timer = new PeriodicTimer(PassInterval, _time);
        try
        {
            do
            {
                try
                {
                    await PassAsync(_stop.Token).ConfigureAwait(false);
                }
                catch (Exception e) when (e is not OperationCanceledException)
                {
                    _log.Error("[accounts] erasing deleted accounts failed; trying again later.", e);
                }
            }
            while (await timer.WaitForNextTickAsync(_stop.Token).ConfigureAwait(false));
        }
        catch (OperationCanceledException)
        {
        }
    }

    /// <summary>Stops the passes; what is being erased is finished or undone.</summary>
    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync().ConfigureAwait(false);
        await _loop.ConfigureAwait(false);
    }
}
