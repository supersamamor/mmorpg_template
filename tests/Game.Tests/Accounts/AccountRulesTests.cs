using Game.Server.Accounts;
using Game.Server.Hosting;
using Game.Server.Persistence;
using Game.Shared.Accounts;
using Game.Shared.Definitions;
using Game.Tests.World;

namespace Game.Tests.Accounts;

/// <summary>T-701: the pure rules (usernames, passwords, character names), hashing, tokens and limits.</summary>
public class AccountRulesTests
{
    [Theory]
    [InlineData("Ada", "ada")]
    [InlineData("  ada ", "ada")]
    [InlineData("ＡＤＡ", "ada")]   // full width
    [InlineData("a.b-c_9", "a.b-c_9")]
    [InlineData("9lives", "9lives")]
    [InlineData("abcdefghijklmnopqrstuvwx", "abcdefghijklmnopqrstuvwx")]
    public void A_username_has_one_normalised_form(string typed, string stored) => AccountRules.NormalizeUsername(typed).ShouldBe(stored);

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("ab")]
    [InlineData("abcdefghijklmnopqrstuvwxy")]
    [InlineData(".ada")]
    [InlineData("a da")]
    [InlineData("аda")]             // a Cyrillic letter that looks like a Latin one
    [InlineData("ada\u0000")]
    [InlineData("ad\ud800a")]       // a lone surrogate
    [InlineData("ada@example.com")] // no e-mail addresses: no personal data
    public void Anything_else_is_not_a_username(string? typed) => AccountRules.NormalizeUsername(typed).ShouldBeNull();

    [Fact]
    public void Passwords_are_limited_by_length_only_and_never_trimmed()
    {
        AccountRules.NormalizePassword("123456789").ShouldBeNull();
        AccountRules.NormalizePassword("1234567890").ShouldBe("1234567890");
        AccountRules.NormalizePassword("          ").ShouldBe("          ");
        AccountRules.NormalizePassword(new string('x', 128)).ShouldNotBeNull();
        AccountRules.NormalizePassword(new string('x', 129)).ShouldBeNull();
        AccountRules.NormalizePassword("pass\ud800word!!").ShouldBeNull();
        AccountRules.NormalizePassword("cafécafé 1").ShouldBe("cafécafé 1");
    }

    [Theory]
    [InlineData("Ada", "Ada")]
    [InlineData(" Ada ", "Ada")]
    [InlineData("Anna-Lee", "Anna-Lee")]
    [InlineData("O'Dell", "O'Dell")]
    [InlineData("Ada9", "Ada9")]          // digits and the underscore: the owner's request (T-814)
    [InlineData("Sam_01", "Sam_01")]
    [InlineData("007", "007")]
    public void A_character_name_is_kept_as_typed(string typed, string stored) => Sample().Normalize(typed).ShouldBe(stored);

    [Theory]
    [InlineData(null)]
    [InlineData("Al")]
    [InlineData("Abcdefghijklmnopq")]
    [InlineData("-Ada")]
    [InlineData("Ada-")]
    [InlineData("Ad--a")]
    [InlineData("Ad-'a")]
    [InlineData("_Sam")]
    [InlineData("Sam_")]
    [InlineData("Sa__m")]
    [InlineData("Sa-_m")]
    [InlineData("Ada Lee")]
    [InlineData("Аdа")]
    [InlineData("Ada​")]
    public void Content_decides_what_a_name_may_hold(string? typed) => Sample().Normalize(typed).ShouldBeNull();

    [Fact]
    public void Names_that_differ_in_letter_case_have_one_key() => CharacterNameRules.Key("AdA").ShouldBe(CharacterNameRules.Key("aDa"));

    [Fact]
    public void The_rules_come_from_content()
    {
        var rules = new CharacterNameRules(new CharactersBalanceDef { NameMinLength = 1, NameMaxLength = 4, NameLetters = ["0-9", "é"], NameSeparators = ["_"] });
        rules.Normalize("1_é").ShouldBe("1_é");
        rules.Normalize("a").ShouldBeNull();
        rules.Normalize("12345").ShouldBeNull();
        CharacterNameRules.TryParseRange("z-a", out _).ShouldBeFalse();
        CharacterNameRules.TryParseRange("ab", out _).ShouldBeFalse();
    }

    private static CharacterNameRules Sample() => new(TestGrids.SampleContent.Balance.Characters);

    // ---- hashing ----

    [Fact]
    public void Argon2id_checks_its_own_hashes_and_never_stores_the_password()
    {
        var hasher = new Argon2idHasher();
        var stored = hasher.Hash(ServiceRig.Password);
        stored.ShouldStartWith("$argon2id$v=19$m=19456,t=2,p=1$");
        stored.ShouldNotContain(ServiceRig.Password);
        stored.Length.ShouldBeLessThanOrEqualTo(256); // the column
        hasher.Hash(ServiceRig.Password).ShouldNotBe(stored, "a new salt each time");
        hasher.Verify(ServiceRig.Password, stored).ShouldBe(PasswordCheck.Right);
        hasher.Verify(ServiceRig.Password + "x", stored).ShouldBe(PasswordCheck.Wrong);
    }

    [Fact]
    public void The_stored_parameters_are_the_ones_a_check_uses()
    {
        var low = new Argon2idHasher(1024, 1);
        var stored = low.Hash(ServiceRig.Password);
        low.Verify(ServiceRig.Password, stored.Replace("m=1024", "m=2048")).ShouldBe(PasswordCheck.Wrong);
        low.Verify(ServiceRig.Password, stored.Replace("t=1", "t=2")).ShouldBe(PasswordCheck.Wrong);
    }

    [Fact]
    public void A_hash_with_other_parameters_still_checks_and_says_it_is_outdated()
    {
        var old = new Argon2idHasher(1024, 1).Hash(ServiceRig.Password);
        new Argon2idHasher(2048, 1).Verify(ServiceRig.Password, old).ShouldBe(PasswordCheck.RightButOutdated);
        new Argon2idHasher(1024, 2).Verify(ServiceRig.Password, old).ShouldBe(PasswordCheck.RightButOutdated);
        new Argon2idHasher(2048, 1).Verify("another password", old).ShouldBe(PasswordCheck.Wrong);
    }

    [Theory]
    [InlineData("")]
    [InlineData("plain text")]
    [InlineData("$argon2id$v=19$m=1024,t=1,p=1$AAAA$AAAA")]
    [InlineData("$argon2id$v=19$m=99999999,t=1,p=1$AAAAAAAAAAAAAAAAAAAAAA$AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA")]   // would take 95 GiB
    [InlineData("$argon2i$v=19$m=1024,t=1,p=1$AAAAAAAAAAAAAAAAAAAAAA$AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA")]
    public void A_damaged_hash_matches_nothing(string stored) => new Argon2idHasher(1024, 1).Verify(ServiceRig.Password, stored).ShouldBe(PasswordCheck.Wrong);

    [Fact]
    public async Task The_hash_queue_bounds_how_much_runs_and_waits()
    {
        using var queue = new HashQueue(2, 1, TimeSpan.FromSeconds(30));
        using var release = new ManualResetEventSlim();
        var running = 0;
        var most = 0;
        int Work()
        {
            var now = Interlocked.Increment(ref running);
            InterlockedMax(ref most, now);
            release.Wait();
            return Interlocked.Decrement(ref running);
        }
        var admitted = Enumerable.Range(0, 3).Select(_ => queue.RunAsync(Work, default)).ToList();
        await Should.ThrowAsync<StoreUnavailableException>(() => queue.RunAsync(Work, default)); // 2 run, 1 waits, the fourth is told to come back
        release.Set();
        await Task.WhenAll(admitted);
        most.ShouldBeLessThanOrEqualTo(2);
        (await queue.RunAsync(() => 7, default)).ShouldBe(7);
    }

    private static void InterlockedMax(ref int target, int value)
    {
        int seen;
        while ((seen = Volatile.Read(ref target)) < value && Interlocked.CompareExchange(ref target, value, seen) != seen) { }
    }

    // ---- tokens ----

    [Fact]
    public void Tokens_are_256_random_bits_and_only_their_hash_is_comparable()
    {
        var tokens = Enumerable.Range(0, 200).Select(_ => SessionTokens.New()).ToList();
        tokens.Distinct().Count().ShouldBe(200);
        tokens.ShouldAllBe(t => t.Length == SessionTokens.TokenLength);
        SessionTokens.HashOf(tokens[0])!.Length.ShouldBe(32);
        SessionTokens.HashOf(tokens[0]).ShouldBe(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.ASCII.GetBytes(tokens[0])));
        foreach (var bad in new[] { null, "", "x", tokens[0] + "x", tokens[0][..42] + "=", tokens[0][..42] + "é" }) SessionTokens.HashOf(bad).ShouldBeNull();
    }

    [Fact]
    public void Records_with_secrets_do_not_print_them()
    {
        new Game.Shared.Protocol.CredentialsRequest("ada", "secret-pw").ToString().ShouldNotContain("secret-pw");
        new Game.Shared.Protocol.SessionAnswer("secret-tok", 1).ToString().ShouldNotContain("secret-tok");
        new NewSession("secret-tok", TimeSpan.Zero).ToString().ShouldNotContain("secret-tok");
        new AccountAuth(Guid.Empty, "secret-hash").ToString().ShouldNotContain("secret-hash");
        new Game.Shared.Protocol.HelloMessage(1, null, null, null, "secret-tok", "c").ToString().ShouldNotContain("secret-tok");
        new Game.Shared.Protocol.HelloMessage(1, null, null, "secret-res").ToString().ShouldNotContain("secret-res");
    }

    // ---- limits ----

    [Fact]
    public void Failures_lock_for_longer_and_longer_up_to_a_cap()
    {
        var guard = new FailureLock(freeFailures: 3, lockBaseMs: 1000, lockCapMs: 4000, forgetMs: 60_000, maxTracked: 10);
        var now = 0L;
        for (var i = 0; i < 3; i++) guard.TryBegin("a", now).ShouldBe(0);
        foreach (var expected in new[] { 1000, 2000, 4000, 4000 })
        {
            guard.TryBegin("a", now).ShouldBe(0, "this attempt is let through, and counted");
            guard.TryBegin("a", now).ShouldBe(expected);
            guard.TryBegin("a", now + expected - 1).ShouldBe(1);
            now += expected;
        }
        guard.TryBegin("b", now).ShouldBe(0, "another key is not affected");
        guard.TryBegin("a", now + 120_000).ShouldBe(0, "forgotten after a long time without a failure");
        guard.TryBegin("a", now + 120_000).ShouldBe(0);
    }

    [Fact]
    public void Attempts_at_once_count_before_any_of_them_has_failed()
    {
        var guard = new FailureLock(5, 1000, 4000, 60_000, 10);
        Enumerable.Range(0, 100).Count(_ => guard.TryBegin("a", 0) == 0).ShouldBe(6); // 5 free, and the one that locks
    }

    [Fact]
    public void Success_forgets_and_undo_takes_back_one()
    {
        var guard = new FailureLock(2, 1000, 4000, 60_000, 10);
        guard.TryBegin("a", 0);
        guard.TryBegin("a", 0);
        guard.Succeed("a");
        guard.Tracked.ShouldBe(0);
        guard.TryBegin("a", 0);
        guard.TryBegin("a", 0);
        guard.Undo("a");
        guard.TryBegin("a", 0).ShouldBe(0);
        guard.TryBegin("a", 0).ShouldBe(0);
        guard.TryBegin("a", 0).ShouldBe(1000);
        guard.Undo("nobody");
    }

    [Fact]
    public void The_tables_cannot_grow_without_bound()
    {
        var guard = new FailureLock(5, 1000, 4000, 60_000, maxTracked: 100);
        var buckets = new KeyedBuckets(1, 5, maxTracked: 100);
        for (var i = 0; i < 1000; i++)
        {
            guard.TryBegin("k" + i, i);
            buckets.TryTake("k" + i, i);
        }
        guard.Tracked.ShouldBeLessThanOrEqualTo(100);
    }

    [Fact]
    public void Buckets_refill_with_the_clock()
    {
        var buckets = new KeyedBuckets(1, 2, 10);
        buckets.TryTake("a", 0).ShouldBeTrue();
        buckets.TryTake("a", 0).ShouldBeTrue();
        buckets.TryTake("a", 0).ShouldBeFalse();
        buckets.TryTake("b", 0).ShouldBeTrue();
        buckets.TryTake("a", 1000).ShouldBeTrue();
        buckets.TryTake("a", 1000).ShouldBeFalse();
    }

    [Theory]
    [InlineData("203.0.113.7", "203.0.113.7")]
    [InlineData("::ffff:203.0.113.7", "203.0.113.7")]
    [InlineData("2001:db8:1:2:3:4:5:6", "2001:db8:1:2::/64")]
    [InlineData("[2001:db8:1:2:ffff::9]", "2001:db8:1:2::/64")]
    [InlineData("something", "something")]
    public void Addresses_are_counted_per_subscriber(string address, string key) => AddressKeys.Of(address).ShouldBe(key);

    // ---- the database's door ----

    [Fact]
    public void Database_work_and_hashing_refuse_to_run_on_a_tick_thread()
    {
        Exception? open = null, hash = null;
        var thread = new Thread(() =>
        {
            TickThread.Enter();
            open = Record.Exception(() => new GameDatabase("Host=127.0.0.1;Database=x;Username=x").Open());
            hash = Record.Exception(() => { _ = new HashQueue(1, 1, TimeSpan.Zero).RunAsync(() => 1, default).GetAwaiter().GetResult(); });
        });
        thread.Start();
        thread.Join();
        open.ShouldBeOfType<InvalidOperationException>().Message.ShouldContain("tick thread");
        hash.ShouldBeOfType<InvalidOperationException>().Message.ShouldContain("tick thread");
        TickThread.IsCurrent.ShouldBeFalse();
    }

    [Fact]
    public void The_schema_state_follows_from_the_applied_migrations()
    {
        GameDatabase.Compare(["1", "2"], ["1", "2"]).ShouldBe(SchemaState.UpToDate);
        GameDatabase.Compare(["1", "2"], []).ShouldBe(SchemaState.Missing);
        GameDatabase.Compare(["1", "2"], ["1"]).ShouldBe(SchemaState.Behind);
        GameDatabase.Compare(["1", "2"], ["1", "2", "3"]).ShouldBe(SchemaState.Newer);
        GameDatabase.Compare(["1", "2"], ["3"]).ShouldBe(SchemaState.Newer);
    }

    [Theory]
    [InlineData("not a connection string with hunter2")]
    [InlineData("Host=;Password=hunter2")]
    [InlineData("Host=x;Port=hunter2")]
    [InlineData("Nonsense=hunter2")]
    public void A_bad_connection_string_is_refused_without_repeating_it(string text) =>
        Should.Throw<DatabaseException>(() => new GameDatabase(text)).ToString().ShouldNotContain("hunter2");

    [Fact]
    public async Task An_unreachable_database_is_reported_without_the_connection_string()
    {
        await using var database = new GameDatabase("Host=127.0.0.1;Port=1;Database=x;Username=x;Password=hunter2;Timeout=2");
        var error = await Should.ThrowAsync<DatabaseException>(() => database.CheckSchemaAsync());
        error.ToString().ShouldNotContain("hunter2");
        error.Message.ShouldContain("cannot be used");
        var store = new PostgresAccountStore(database);
        (await Should.ThrowAsync<StoreUnavailableException>(() => store.FindAccountAsync("ada", default))).ToString().ShouldNotContain("hunter2");
    }
}
