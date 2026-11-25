using System.Diagnostics.CodeAnalysis;
using Game.Server.World;
using Game.Shared.Accounts;
using Game.Shared.Definitions;
using Game.Shared.Protocol;
using Game.Shared.Time;
using Game.Shared.Vocabulary;

namespace Game.Server.Accounts;

/// <summary>The answer to an account request: a value, or an "Account errors" term (with how long to wait, when that helps).</summary>
public readonly record struct AccountResult<T>(T? Value, string? Error, long RetryAfterMs = 0) where T : class
{
    /// <summary>True when there is a value.</summary>
    [MemberNotNullWhen(true, nameof(Value))]
    [MemberNotNullWhen(false, nameof(Error))]
    public bool Ok => Error is null;

    /// <summary>A refusal.</summary>
    public static AccountResult<T> Fail(string error, long retryAfterMs = 0) => new(null, error, retryAfterMs);

    /// <summary>A value.</summary>
    public static implicit operator AccountResult<T>(T value) => new(value, null);
}

/// <summary>A new session: its token (shown once) and how long it lasts.</summary>
public sealed record NewSession(string Token, TimeSpan Lifetime)
{
    /// <summary>No token in a log line or an exception.</summary>
    public override string ToString() => "NewSession { *** }";
}

/// <summary>
/// Sessions that someone ended (T-710): signed out, pushed out by a newer sign-in, or all of the account's by a password change. A session whose time merely ran out is not one.
/// </summary>
/// <param name="Account">Whose.</param>
/// <param name="Keys">Their keys (<see cref="SessionTokens.KeyOf(string?)"/>).</param>
/// <param name="All">True when every session of the account ended, also any it no longer had a record of.</param>
public sealed record EndedSessions(Guid Account, IReadOnlyCollection<string> Keys, bool All);

/// <summary>An accepted deletion: how long until everything is erased.</summary>
public sealed record ErasureAnswer(TimeSpan In);

/// <summary>A value for requests that only succeed or fail.</summary>
public sealed class Done
{
    /// <summary>The one value.</summary>
    public static readonly Done Value = new();
}

/// <summary>
/// Accounts, sessions and character slots (T-701, ARCHITECTURE §10, §11): the rules and limits, over an <see cref="IAccountStore"/>. Knows no HTTP and no sockets.
/// Thread-safe; its methods must not be called on a tick thread.
/// </summary>
/// <remarks>
/// <para>An unknown username and a wrong password are indistinguishable: the same answer, the same one database read, and the same one password hash (a dummy
/// with today's parameters when there is no account). The limits count usernames whether or not they exist.</para>
/// <para>Passwords and tokens are never logged; usernames are not either (accounts appear by id).</para>
/// </remarks>
public sealed class AccountService
{
    private readonly IAccountStore _store;
    private readonly IPasswordHasher _hasher;
    private readonly HashQueue _hashes;
    private readonly AccountLimits _limits;
    private readonly CharacterNameRules _names;
    private readonly AppearanceRules _looks;
    private readonly int _maxSlots;
    private readonly string _startJobId;
    private readonly IClock _clock;
    private readonly TimeProvider _time;
    private readonly IServerLog _log;
    private readonly KeyedBuckets _requests;
    private readonly KeyedBuckets _registers;
    private readonly KeyedBuckets _creates;
    private readonly FailureLock _accountFailures;
    private readonly FailureLock _addressFailures;
    private readonly FailureLock _helloFailures;
    private readonly string _dummyHash;

    /// <summary>Creates the service. <paramref name="clock"/> drives the limits, <paramref name="time"/> the stored times (session expiry).</summary>
    public AccountService(IAccountStore store, IPasswordHasher hasher, HashQueue hashes, AccountLimits limits, BalanceDef balance, AppearanceDef appearance, IClock clock, TimeProvider time, IServerLog log)
    {
        _store = store;
        _hasher = hasher;
        _hashes = hashes;
        _limits = limits;
        _names = new CharacterNameRules(balance.Characters);
        _looks = new AppearanceRules(appearance, log);
        _maxSlots = balance.Characters.MaxSlots;
        _startJobId = balance.NewCharacter.StartJobId;
        _clock = clock;
        _time = time;
        _log = log;
        _requests = new KeyedBuckets(limits.RequestsPerMinute / 60, limits.RequestBurst, limits.MaxTracked);
        _registers = new KeyedBuckets(limits.RegistersPerHour / 3600, limits.RegisterBurst, limits.MaxTracked);
        _creates = new KeyedBuckets(limits.CreatesPerHour / 3600, limits.CreateBurst, limits.MaxTracked);
        _accountFailures = NewLock(limits.AccountFreeFailures);
        _addressFailures = NewLock(limits.AddressFreeFailures);
        _helloFailures = NewLock(limits.AddressFreeFailures);
        _dummyHash = hasher.Hash(SessionTokens.New()); // of a password nobody knows
        FailureLock NewLock(int free) => new(free, limits.LockBaseMs, limits.LockCapMs, limits.ForgetMs, limits.MaxTracked);
    }

    /// <summary>Whether a character is in the world now (played, or in its grace period): the gateway's answer. Such a character is not deleted.</summary>
    public Func<Guid, bool> InWorld { get; set; } = _ => false;

    /// <summary>Called after sessions have ended (the database has them ended by then): the gateway closes what was playing with them.</summary>
    public Action<EndedSessions> SessionsEnded { get; set; } = _ => { };

    private DateTime UtcNow => _time.GetUtcNow().UtcDateTime;

    /// <summary>Counts a request of <paramref name="address"/>; false when it has made too many.</summary>
    public bool AllowRequest(string address, out long retryAfterMs)
    {
        retryAfterMs = _requests.RetryAfterMs;
        return _requests.TryTake(address, _clock.NowMilliseconds);
    }

    /// <summary>Creates an account and signs it in.</summary>
    public async Task<AccountResult<NewSession>> RegisterAsync(string username, string password, string address, CancellationToken cancellation)
    {
        if (AccountRules.NormalizeUsername(username) is not { } name) return AccountResult<NewSession>.Fail(AccountErrors.InvalidUsername);
        if (AccountRules.NormalizePassword(password) is not { } secret) return AccountResult<NewSession>.Fail(AccountErrors.InvalidPassword);
        if (!_registers.TryTake(address, _clock.NowMilliseconds)) return AccountResult<NewSession>.Fail(AccountErrors.RateLimited, _registers.RetryAfterMs);
        return await GuardAsync<NewSession>(async () =>
        {
            var hash = await _hashes.RunAsync(() => _hasher.Hash(secret), cancellation).ConfigureAwait(false);
            var id = Guid.CreateVersion7(_time.GetUtcNow());
            if (!await _store.TryCreateAccountAsync(id, name, hash, UtcNow, cancellation).ConfigureAwait(false))
                return AccountResult<NewSession>.Fail(AccountErrors.UsernameTaken);
            _log.Info($"[accounts] account {id} registered from {address}.");
            return await OpenSessionAsync(id, hash, null, cancellation).ConfigureAwait(false) is { } session ? session : AccountResult<NewSession>.Fail(AccountErrors.InvalidCredentials);
        }).ConfigureAwait(false);
    }

    /// <summary>Signs in. An account that waits to be erased is refused with <c>account_deleting</c>, and only to someone who has its password (T-703).</summary>
    public Task<AccountResult<NewSession>> LoginAsync(string username, string password, string address, CancellationToken cancellation) => SignInAsync(username, password, address, keep: false, cancellation);

    private async Task<AccountResult<NewSession>> SignInAsync(string username, string password, string address, bool keep, CancellationToken cancellation)
    {
        if (AccountRules.NormalizeUsername(username) is not { } name) return AccountResult<NewSession>.Fail(AccountErrors.InvalidUsername);
        if (AccountRules.NormalizePassword(password) is not { } secret) return AccountResult<NewSession>.Fail(AccountErrors.InvalidPassword);
        var now = _clock.NowMilliseconds;
        var wait = _addressFailures.TryBegin(address, now);
        if (wait == 0 && (wait = _accountFailures.TryBegin(name, now)) > 0) _addressFailures.Undo(address); // nothing was tried: only the account is locked
        if (wait > 0)
        {
            _log.Warn($"[accounts] login from {address} refused: locked out after failures.");
            return AccountResult<NewSession>.Fail(AccountErrors.RateLimited, wait);
        }

        var result = await GuardAsync<NewSession>(async () =>
        {
            for (var attempt = 0; attempt < 2; attempt++)
            {
                var account = await _store.FindAccountAsync(name, cancellation).ConfigureAwait(false);
                var check = await _hashes.RunAsync(() => _hasher.Verify(secret, account?.PasswordHash ?? _dummyHash), cancellation).ConfigureAwait(false);
                if (account is null || check == PasswordCheck.Wrong) return AccountResult<NewSession>.Fail(AccountErrors.InvalidCredentials);
                if (account.EraseAfter is { } eraseAfter)
                {
                    if (!keep) return AccountResult<NewSession>.Fail(AccountErrors.AccountDeleting, Math.Max(0, (long)(eraseAfter - UtcNow).TotalMilliseconds));
                    if (!await _store.TryCancelErasureAsync(account.Id, account.PasswordHash, cancellation).ConfigureAwait(false)) continue; // erased, or taken back elsewhere: look again
                    _log.Info($"[accounts] account {account.Id} is kept: its deletion was taken back from {address}.");
                }
                var rehash = check == PasswordCheck.RightButOutdated ? await _hashes.RunAsync(() => _hasher.Hash(secret), cancellation).ConfigureAwait(false) : null;
                if (await OpenSessionAsync(account.Id, account.PasswordHash, rehash, cancellation).ConfigureAwait(false) is not { } session) continue; // the hash changed meanwhile: check again
                _log.Info($"[accounts] account {account.Id} signed in from {address}{(rehash is null ? "" : "; its password was rehashed with the current parameters")}.");
                return session;
            }
            return AccountResult<NewSession>.Fail(AccountErrors.InvalidCredentials);
        }).ConfigureAwait(false);

        if (result.Ok || result.Error == AccountErrors.AccountDeleting) // the password was right
        {
            _accountFailures.Succeed(name);
            _addressFailures.Undo(address); // not forgotten: an attacker's own good login must not buy them more guesses
        }
        else if (result.Error == AccountErrors.Unavailable)
        {
            _accountFailures.Undo(name);
            _addressFailures.Undo(address);
        }
        else _log.Warn($"[accounts] failed login from {address}.");
        return result;
    }

    /// <summary>Ends a session. Ending one that is not live is refused like any other use of it.</summary>
    public Task<AccountResult<Done>> LogoutAsync(string? token, CancellationToken cancellation) => GuardAsync<Done>(async () =>
    {
        if (SessionTokens.HashOf(token) is not { } hash || await _store.FindSessionAsync(hash, UtcNow, cancellation).ConfigureAwait(false) is not { } account
            || !await _store.DeleteSessionAsync(hash, cancellation).ConfigureAwait(false))
            return AccountResult<Done>.Fail(AccountErrors.InvalidSession);
        SessionsEnded(new EndedSessions(account, [SessionTokens.KeyOf(hash)], All: false));
        return Logged(account, "signed out", Done.Value);
    });

    /// <summary>
    /// Changes the password (T-710). Every session of the account ends, this one included, with whatever plays with them; the caller, who has just proved the password, gets a new one.
    /// </summary>
    public Task<AccountResult<NewSession>> ChangePasswordAsync(string? token, string current, string next, string address, CancellationToken cancellation)
    {
        var fresh = AccountRules.NormalizePassword(next);
        string? hash = null;
        return WithPasswordAsync<NewSession>(token, current, address, "password change", fresh is null ? AccountErrors.InvalidPassword : null, async (auth, secret) =>
        {
            if (fresh == secret) return AccountResult<NewSession>.Fail(AccountErrors.SamePassword);
            hash ??= await _hashes.RunAsync(() => _hasher.Hash(fresh!), cancellation).ConfigureAwait(false);
            if (await _store.TryChangePasswordAsync(auth.Id, auth.PasswordHash, hash, UtcNow, cancellation).ConfigureAwait(false) is not { } ended) return null;
            _log.Info($"[accounts] account {auth.Id} changed its password from {address}; its {ended.Count} session(s) are ended.");
            SessionsEnded(new EndedSessions(auth.Id, ended.Select(SessionTokens.KeyOf).ToList(), All: true));
            try
            {
                return await OpenSessionAsync(auth.Id, hash, null, cancellation).ConfigureAwait(false) is { } session ? session : AccountResult<NewSession>.Fail(AccountErrors.InvalidSession);
            }
            catch (StoreUnavailableException)
            {
                return AccountResult<NewSession>.Fail(AccountErrors.InvalidSession); // the password is changed all the same: sign in with it
            }
        }, cancellation);
    }

    /// <summary>
    /// Asks for the account's deletion (T-703). Every session ends with whatever plays with it, nobody can sign in any more, and after <see cref="AccountLimits.EraseWait"/> everything is
    /// erased (<see cref="AccountEraser"/>). Until then <see cref="KeepAsync"/> takes it back.
    /// </summary>
    public Task<AccountResult<ErasureAnswer>> DeleteAccountAsync(string? token, string password, string address, CancellationToken cancellation) =>
        WithPasswordAsync<ErasureAnswer>(token, password, address, "account deletion", null, async (auth, _) =>
        {
            var now = UtcNow;
            if (await _store.TryRequestErasureAsync(auth.Id, auth.PasswordHash, now, now + _limits.EraseWait, cancellation).ConfigureAwait(false) is not { } ended) return null;
            _log.Info($"[accounts] account {auth.Id} is to be deleted, asked from {address}; its {ended.Count} session(s) are ended.");
            SessionsEnded(new EndedSessions(auth.Id, ended.Select(SessionTokens.KeyOf).ToList(), All: true));
            return new ErasureAnswer(_limits.EraseWait);
        }, cancellation);

    /// <summary>Takes a deletion back, with the account's username and password, and signs in. An account that does not wait to be erased is signed in to as by <see cref="LoginAsync"/>.</summary>
    public Task<AccountResult<NewSession>> KeepAsync(string username, string password, string address, CancellationToken cancellation) => SignInAsync(username, password, address, keep: true, cancellation);

    /// <summary>
    /// Runs <paramref name="work"/> for the session's account once its password is proved again: a stolen session must not be enough. A wrong one counts like a failed sign-in: for the
    /// address, and for the account (under its id, so a stolen session cannot lock the owner's sign-in). <paramref name="work"/> returns null when the stored hash changed meanwhile: it is
    /// checked again, once.
    /// </summary>
    private Task<AccountResult<T>> WithPasswordAsync<T>(string? token, string password, string address, string what, string? refusal, Func<AccountAuth, string, Task<AccountResult<T>?>> work,
        CancellationToken cancellation) where T : class => WithSessionAsync<T>(token, async account =>
    {
        if (refusal is not null) return AccountResult<T>.Fail(refusal);
        var key = account.ToString();
        var now = _clock.NowMilliseconds;
        var wait = _addressFailures.TryBegin(address, now);
        if (wait == 0 && (wait = _accountFailures.TryBegin(key, now)) > 0) _addressFailures.Undo(address); // nothing was tried: only the account is locked
        if (wait > 0)
        {
            _log.Warn($"[accounts] {what} of account {account} from {address} refused: locked out after failures.");
            return AccountResult<T>.Fail(AccountErrors.RateLimited, wait);
        }
        var judged = false;
        try
        {
            var secret = AccountRules.NormalizePassword(password);
            for (var attempt = 0; attempt < 2; attempt++) // twice, as at a sign-in: the stored hash may have changed meanwhile
            {
                if (await _store.FindAccountAsync(account, cancellation).ConfigureAwait(false) is not { } auth) return AccountResult<T>.Fail(AccountErrors.InvalidSession);
                if (secret is null || await _hashes.RunAsync(() => _hasher.Verify(secret, auth.PasswordHash), cancellation).ConfigureAwait(false) == PasswordCheck.Wrong)
                {
                    if (attempt > 0) break; // a password change won
                    judged = true;
                    _log.Warn($"[accounts] failed {what} of account {account} from {address}.");
                    return AccountResult<T>.Fail(AccountErrors.InvalidCredentials);
                }
                if (await work(auth, secret).ConfigureAwait(false) is not { } result) continue;
                if (result.Ok) _accountFailures.Succeed(key);
                return result;
            }
            return AccountResult<T>.Fail(AccountErrors.InvalidSession); // the change that won ended this session too
        }
        finally
        {
            if (!judged) // right, or nothing was found out about the password; an address's other failures stay counted, as at a sign-in
            {
                _accountFailures.Undo(key);
                _addressFailures.Undo(address);
            }
        }
    }, cancellation);

    /// <summary>The account's characters and the rules for a new one.</summary>
    public Task<AccountResult<CharactersAnswer>> ListAsync(string? token, CancellationToken cancellation) => WithSessionAsync<CharactersAnswer>(token, async account =>
        new CharactersAnswer(_maxSlots, _names.MinLength, _names.MaxLength,
            (await _store.ListCharactersAsync(account, cancellation).ConfigureAwait(false)).Select(Record).ToList()), cancellation);

    /// <summary>The rules for looks, which the gateway also uses for a login that has no character (the dev login).</summary>
    public AppearanceRules Looks => _looks;

    /// <summary>Creates a character with the default looks.</summary>
    public Task<AccountResult<CharacterRecord>> CreateAsync(string? token, string name, CancellationToken cancellation) => CreateAsync(token, name, null, cancellation);

    /// <summary>Creates a character in the account's lowest free slot, with the looks asked for (parts left out get the defaults) or refused with <c>invalid_appearance</c> (T-705).</summary>
    public Task<AccountResult<CharacterRecord>> CreateAsync(string? token, string name, AppearanceChoice? appearance, CancellationToken cancellation) => WithSessionAsync<CharacterRecord>(token, async account =>
    {
        if (_names.Normalize(name) is not { } clean) return AccountResult<CharacterRecord>.Fail(AccountErrors.InvalidName);
        if (_looks.Choose(appearance) is not { } looks) return AccountResult<CharacterRecord>.Fail(AccountErrors.InvalidAppearance);
        if (!_creates.TryTake(account.ToString(), _clock.NowMilliseconds)) return AccountResult<CharacterRecord>.Fail(AccountErrors.RateLimited, _creates.RetryAfterMs);
        var (outcome, character) = await _store.CreateCharacterAsync(account, Guid.CreateVersion7(_time.GetUtcNow()), clean, CharacterNameRules.Key(clean), _startJobId, _maxSlots,
            looks, UtcNow, cancellation).ConfigureAwait(false);
        return outcome switch
        {
            CreateCharacterOutcome.Created => Logged(account, $"created character {character!.Id}", Record(character) with { Appearance = looks }),
            CreateCharacterOutcome.NameTaken => AccountResult<CharacterRecord>.Fail(AccountErrors.NameTaken),
            CreateCharacterOutcome.AccountGone => AccountResult<CharacterRecord>.Fail(AccountErrors.InvalidSession),
            _ => AccountResult<CharacterRecord>.Fail(AccountErrors.NoFreeSlot),
        };
    }, cancellation);

    /// <summary>Deletes a character (softly), unless it is in the world.</summary>
    public Task<AccountResult<Done>> DeleteAsync(string? token, Guid character, CancellationToken cancellation) => WithSessionAsync<Done>(token, async account =>
    {
        if (await _store.FindCharacterAsync(account, character, cancellation).ConfigureAwait(false) is null) return AccountResult<Done>.Fail(AccountErrors.UnknownCharacter);
        if (InWorld(character)) return AccountResult<Done>.Fail(AccountErrors.CharacterInUse);
        return await _store.DeleteCharacterAsync(account, character, UtcNow, cancellation).ConfigureAwait(false) switch
        {
            DeleteCharacterOutcome.Deleted => Logged(account, $"deleted character {character}", Done.Value),
            DeleteCharacterOutcome.InUse => AccountResult<Done>.Fail(AccountErrors.CharacterInUse), // another server's
            _ => AccountResult<Done>.Fail(AccountErrors.UnknownCharacter),
        };
    }, cancellation);

    /// <summary>
    /// The account and character of a <c>hello</c>: a live session, and a character of that account. Failures are limited per address, because each costs a database read.
    /// </summary>
    public async Task<AccountResult<CharacterLogin>> ResolveAsync(string token, string character, string address, CancellationToken cancellation)
    {
        var wait = _helloFailures.TryBegin(address, _clock.NowMilliseconds);
        if (wait > 0) return AccountResult<CharacterLogin>.Fail(AccountErrors.RateLimited, wait);
        var result = !Guid.TryParseExact(character, "D", out var id)
            ? AccountResult<CharacterLogin>.Fail(AccountErrors.UnknownCharacter)
            : await WithSessionAsync<CharacterLogin>(token, async account =>
                await _store.FindCharacterAsync(account, id, cancellation).ConfigureAwait(false) is { } found
                    ? new CharacterLogin(account, found, _looks.Resolve(found.Id, found.Appearance))
                    : AccountResult<CharacterLogin>.Fail(AccountErrors.UnknownCharacter), cancellation).ConfigureAwait(false);
        if (result.Ok || result.Error == AccountErrors.Unavailable) _helloFailures.Undo(address);
        return result;
    }

    private CharacterRecord Record(CharacterEntry c) => new(c.Id.ToString("D"), c.Slot, c.Name, c.JobId, _looks.Resolve(c.Id, c.Appearance));

    private async Task<NewSession?> OpenSessionAsync(Guid account, string expectedHash, string? rehash, CancellationToken cancellation)
    {
        var token = SessionTokens.New();
        var now = UtcNow;
        if (await _store.TryAddSessionAsync(account, expectedHash, rehash, SessionTokens.HashOf(token)!, now, now + _limits.SessionLifetime, _limits.MaxSessions, cancellation)
                .ConfigureAwait(false) is not { } pushedOut) return null;
        if (pushedOut.Count > 0) SessionsEnded(new EndedSessions(account, pushedOut.Select(SessionTokens.KeyOf).ToList(), All: false));
        return new NewSession(token, _limits.SessionLifetime);
    }

    private Task<AccountResult<T>> WithSessionAsync<T>(string? token, Func<Guid, Task<AccountResult<T>>> work, CancellationToken cancellation) where T : class => GuardAsync(async () =>
        SessionTokens.HashOf(token) is { } hash && await _store.FindSessionAsync(hash, UtcNow, cancellation).ConfigureAwait(false) is { } account
            ? await work(account).ConfigureAwait(false)
            : AccountResult<T>.Fail(AccountErrors.InvalidSession));

    private async Task<AccountResult<T>> GuardAsync<T>(Func<Task<AccountResult<T>>> work) where T : class
    {
        try
        {
            return await work().ConfigureAwait(false);
        }
        catch (StoreUnavailableException e)
        {
            _log.Warn($"[accounts] unavailable: {e.Message}.");
            return AccountResult<T>.Fail(AccountErrors.Unavailable, 2_000);
        }
    }

    private T Logged<T>(Guid account, string what, T value)
    {
        _log.Info($"[accounts] account {account} {what}.");
        return value;
    }
}

/// <summary>Who a <c>hello</c> signs in as, and how it looks now (<see cref="AppearanceRules.Resolve"/>).</summary>
public sealed record CharacterLogin(Guid Account, CharacterEntry Character, AppearanceRecord Appearance);
