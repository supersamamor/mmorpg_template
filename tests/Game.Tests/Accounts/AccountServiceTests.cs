using Game.Server.Accounts;
using Game.Shared.Vocabulary;

namespace Game.Tests.Accounts;

/// <summary>T-701: the account service's rules and limits, over an in-memory store and injected clocks.</summary>
public class AccountServiceTests
{
    [Fact]
    public async Task Register_signs_in_and_stores_only_the_hash_of_the_password()
    {
        var rig = new ServiceRig(new Argon2idHasher(1024, 1));
        var token = await rig.RegisterAsync("Ada");
        rig.Memory.OnlyHash.ShouldStartWith("$argon2id$");
        (await rig.Service.ListAsync(token, default)).Ok.ShouldBeTrue();
        (await rig.LoginAsync("ADA")).Ok.ShouldBeTrue("the username compares in its normalised form");
    }

    [Fact]
    public async Task Register_refuses_what_it_must()
    {
        var rig = new ServiceRig();
        await rig.RegisterAsync();
        (await rig.Service.RegisterAsync("ADA", ServiceRig.Password, "a", default)).Error.ShouldBe(AccountErrors.UsernameTaken);
        (await rig.Service.RegisterAsync("a b", ServiceRig.Password, "a", default)).Error.ShouldBe(AccountErrors.InvalidUsername);
        (await rig.Service.RegisterAsync("bob", "short", "a", default)).Error.ShouldBe(AccountErrors.InvalidPassword);
    }

    [Fact]
    public async Task An_unknown_user_and_a_wrong_password_cannot_be_told_apart()
    {
        var hasher = new FakeHasher();
        var rig = new ServiceRig(hasher);
        await rig.RegisterAsync();
        var log = rig.Log.Lines.Count;

        var calls = rig.Memory.Calls;
        var checks = hasher.Checks;
        var wrong = await rig.LoginAsync("ada", "a wrong password");
        var (wrongCalls, wrongChecks, wrongHashes) = (rig.Memory.Calls - calls, hasher.Checks - checks, hasher.Hashes);

        calls = rig.Memory.Calls;
        checks = hasher.Checks;
        var unknown = await rig.LoginAsync("nobody", "a wrong password");

        unknown.ShouldBe(wrong);
        unknown.Error.ShouldBe(AccountErrors.InvalidCredentials);
        (rig.Memory.Calls - calls, hasher.Checks - checks, hasher.Hashes).ShouldBe((wrongCalls, wrongChecks, wrongHashes), "the same work: one read, one check, no new hash");
        wrongChecks.ShouldBe(1);
        // The dummy costs what a real hash costs: it was made by the same hasher with today's parameters.
        hasher.Checked[^1].Split(':')[0].ShouldBe(hasher.Checked[^2].Split(':')[0]);
        rig.Log.Lines.Skip(log).Distinct().Count().ShouldBe(1, "and the same log line");
    }

    [Fact]
    public async Task A_login_with_outdated_parameters_stores_a_new_hash_and_keeps_the_sessions()
    {
        var old = new ServiceRig(new FakeHasher(1));
        var token = await old.RegisterAsync();
        var before = old.Memory.OnlyHash;
        var now = new ServiceRig(new FakeHasher(2), store: old.Memory);
        (await now.LoginAsync()).Ok.ShouldBeTrue();
        old.Memory.OnlyHash.ShouldNotBe(before);
        old.Memory.OnlyHash.ShouldStartWith("fake2:");
        (await now.Service.ListAsync(token, default)).Ok.ShouldBeTrue("a rehash is not a password change");
        var rehashed = old.Memory.OnlyHash;
        (await now.LoginAsync()).Ok.ShouldBeTrue();
        old.Memory.OnlyHash.ShouldBe(rehashed, "only once");
        (await now.LoginAsync("ada", "a wrong password")).Error.ShouldBe(AccountErrors.InvalidCredentials);
    }

    [Fact]
    public async Task A_session_expires()
    {
        var rig = new ServiceRig();
        var token = await rig.RegisterAsync();
        rig.Time.Now += TimeSpan.FromHours(24) - TimeSpan.FromSeconds(1);
        (await rig.Service.ListAsync(token, default)).Ok.ShouldBeTrue();
        rig.Time.Now += TimeSpan.FromSeconds(1);
        (await rig.Service.ListAsync(token, default)).Error.ShouldBe(AccountErrors.InvalidSession);
        (await rig.Service.LogoutAsync(token, default)).Error.ShouldBe(AccountErrors.InvalidSession);
    }

    [Fact]
    public async Task Logout_ends_that_session_only()
    {
        var rig = new ServiceRig();
        var first = await rig.RegisterAsync();
        var second = (await rig.LoginAsync()).Value!.Token;
        (await rig.Service.LogoutAsync(first, default)).Ok.ShouldBeTrue();
        (await rig.Service.ListAsync(first, default)).Error.ShouldBe(AccountErrors.InvalidSession);
        (await rig.Service.LogoutAsync(first, default)).Error.ShouldBe(AccountErrors.InvalidSession);
        (await rig.Service.ListAsync(second, default)).Ok.ShouldBeTrue();
        foreach (var bad in new[] { null, "", "x", new string('A', 43) }) (await rig.Service.ListAsync(bad, default)).Error.ShouldBe(AccountErrors.InvalidSession);
    }

    [Fact]
    public async Task A_password_change_ends_every_session()
    {
        var rig = new ServiceRig();
        var first = await rig.RegisterAsync();
        var second = (await rig.LoginAsync()).Value!.Token;
        (await rig.Service.ChangePasswordAsync(first, "not the password", "a brand new one", default)).Error.ShouldBe(AccountErrors.InvalidCredentials);
        (await rig.Service.ChangePasswordAsync(first, ServiceRig.Password, "short", default)).Error.ShouldBe(AccountErrors.InvalidPassword);
        (await rig.Service.ChangePasswordAsync("x", ServiceRig.Password, "a brand new one", default)).Error.ShouldBe(AccountErrors.InvalidSession);
        (await rig.Service.ListAsync(second, default)).Ok.ShouldBeTrue();
        (await rig.Service.ChangePasswordAsync(first, ServiceRig.Password, "a brand new one", default)).Ok.ShouldBeTrue();
        foreach (var token in new[] { first, second }) (await rig.Service.ListAsync(token, default)).Error.ShouldBe(AccountErrors.InvalidSession);
        (await rig.LoginAsync()).Error.ShouldBe(AccountErrors.InvalidCredentials);
        (await rig.LoginAsync("ada", "a brand new one")).Ok.ShouldBeTrue();
    }

    [Fact]
    public async Task An_account_keeps_a_bounded_number_of_sessions()
    {
        var rig = new ServiceRig(limits: new AccountLimits { MaxSessions = 2 });
        var first = await rig.RegisterAsync();
        rig.Time.Now += TimeSpan.FromSeconds(1);
        var second = (await rig.LoginAsync()).Value!.Token;
        rig.Time.Now += TimeSpan.FromSeconds(1);
        var third = (await rig.LoginAsync()).Value!.Token;
        (await rig.Service.ListAsync(first, default)).Error.ShouldBe(AccountErrors.InvalidSession);
        (await rig.Service.ListAsync(second, default)).Ok.ShouldBeTrue();
        (await rig.Service.ListAsync(third, default)).Ok.ShouldBeTrue();
        rig.Memory.SessionCount.ShouldBe(2);
    }

    // ---- limits, with the injected clock ----

    [Fact]
    public async Task Failed_logins_lock_the_username_whether_or_not_it_exists()
    {
        foreach (var user in new[] { "ada", "nobody" })
        {
            var hasher = new FakeHasher();
            var rig = new ServiceRig(hasher);
            await rig.RegisterAsync();
            for (var i = 0; i < 6; i++) (await rig.LoginAsync(user, "a wrong password", "10.0.0." + i)).Error.ShouldBe(AccountErrors.InvalidCredentials);
            var checks = hasher.Checks;
            var locked = await rig.LoginAsync(user, ServiceRig.Password, "10.0.0.99");
            locked.Error.ShouldBe(AccountErrors.RateLimited);
            locked.RetryAfterMs.ShouldBe(5000);
            hasher.Checks.ShouldBe(checks, "a locked username costs no hash");
            rig.Clock.Advance(4999);
            (await rig.LoginAsync(user, ServiceRig.Password, "10.0.0.99")).Error.ShouldBe(AccountErrors.RateLimited);
            rig.Clock.Advance(1);
            (await rig.LoginAsync(user, "a wrong password", "10.0.0.99")).Error.ShouldBe(AccountErrors.InvalidCredentials);
            (await rig.LoginAsync(user, ServiceRig.Password, "10.0.0.99")).RetryAfterMs.ShouldBe(10_000, "each failure doubles the lock");
        }
    }

    [Fact]
    public async Task A_good_login_clears_the_usernames_failures()
    {
        var rig = new ServiceRig();
        await rig.RegisterAsync();
        for (var round = 0; round < 3; round++)
        {
            for (var i = 0; i < 4; i++) (await rig.LoginAsync("ada", "a wrong password")).Error.ShouldBe(AccountErrors.InvalidCredentials);
            (await rig.LoginAsync()).Ok.ShouldBeTrue();
        }
    }

    [Fact]
    public async Task Failed_logins_lock_the_address_and_a_good_login_of_its_own_buys_no_more_tries()
    {
        var rig = new ServiceRig();
        await rig.RegisterAsync();
        for (var i = 0; i < 21; i++)
        {
            (await rig.LoginAsync("user" + i, "a wrong password", "198.51.100.1")).Error.ShouldBe(AccountErrors.InvalidCredentials, $"try {i}");
            if (i < 20) (await rig.LoginAsync("ada", ServiceRig.Password, "198.51.100.1")).Ok.ShouldBeTrue();
        }
        (await rig.LoginAsync("ada", ServiceRig.Password, "198.51.100.1")).Error.ShouldBe(AccountErrors.RateLimited);
        (await rig.LoginAsync("ada", ServiceRig.Password, "198.51.100.2")).Ok.ShouldBeTrue("another address is not affected");
        rig.Clock.Advance(5000);
        (await rig.LoginAsync("ada", ServiceRig.Password, "198.51.100.1")).Ok.ShouldBeTrue();
    }

    [Fact]
    public async Task A_burst_of_guesses_at_once_gets_no_more_tries_than_one_after_another()
    {
        var hasher = new FakeHasher();
        var rig = new ServiceRig(hasher, new AccountLimits { AddressFreeFailures = 1000 });
        await rig.RegisterAsync();
        rig.Memory.Gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var tries = Enumerable.Range(0, 50).Select(i => rig.LoginAsync("ada", "a wrong password " + i)).ToList();
        rig.Memory.Gate.SetResult();
        var results = await Task.WhenAll(tries);
        results.Count(r => r.Error == AccountErrors.InvalidCredentials).ShouldBe(6);
        results.Count(r => r.Error == AccountErrors.RateLimited).ShouldBe(44);
        hasher.Checks.ShouldBe(6);
    }

    [Fact]
    public async Task Registrations_and_requests_are_limited_per_address()
    {
        var rig = new ServiceRig();
        for (var i = 0; i < 5; i++) (await rig.Service.RegisterAsync("user" + i, ServiceRig.Password, "a", default)).Ok.ShouldBeTrue();
        var limited = await rig.Service.RegisterAsync("user9", ServiceRig.Password, "a", default);
        limited.Error.ShouldBe(AccountErrors.RateLimited);
        limited.RetryAfterMs.ShouldBe(180_000);
        (await rig.Service.RegisterAsync("user9", ServiceRig.Password, "b", default)).Ok.ShouldBeTrue();
        rig.Clock.Advance(180_000);
        (await rig.Service.RegisterAsync("user10", ServiceRig.Password, "a", default)).Ok.ShouldBeTrue();

        for (var i = 0; i < 30; i++) rig.Service.AllowRequest("c", out _).ShouldBeTrue();
        rig.Service.AllowRequest("c", out var retry).ShouldBeFalse();
        retry.ShouldBe(1000);
        rig.Service.AllowRequest("d", out _).ShouldBeTrue();
        rig.Clock.Advance(1000);
        rig.Service.AllowRequest("c", out _).ShouldBeTrue();
    }

    [Fact]
    public async Task Character_creations_are_limited_per_account()
    {
        var rig = new ServiceRig(limits: new AccountLimits { CreateBurst = 2, CreatesPerHour = 1 });
        var token = await rig.RegisterAsync();
        await rig.CreateAsync(token, "Ada");
        (await rig.Service.CreateAsync(token, "Ada", default)).Error.ShouldBe(AccountErrors.NameTaken);
        (await rig.Service.CreateAsync(token, "Bea", default)).Error.ShouldBe(AccountErrors.RateLimited);
        rig.Clock.Advance(3_600_001);
        await rig.CreateAsync(token, "Bea");
    }

    [Fact]
    public async Task A_database_that_is_away_is_unavailable_and_counts_against_nobody()
    {
        var rig = new ServiceRig();
        var token = await rig.RegisterAsync();
        rig.Memory.Away = true;
        for (var i = 0; i < 30; i++) (await rig.LoginAsync()).Error.ShouldBe(AccountErrors.Unavailable);
        (await rig.Service.ListAsync(token, default)).Error.ShouldBe(AccountErrors.Unavailable);
        (await rig.Service.RegisterAsync("bob", ServiceRig.Password, "a", default)).Error.ShouldBe(AccountErrors.Unavailable);
        for (var i = 0; i < 30; i++) (await rig.Service.ResolveAsync(token, Guid.NewGuid().ToString("D"), "a", default)).Error.ShouldBe(AccountErrors.Unavailable);
        rig.Memory.Away = false;
        (await rig.LoginAsync()).Ok.ShouldBeTrue();
    }

    // ---- characters ----

    [Fact]
    public async Task Characters_take_the_lowest_free_slot_and_names_are_unique_whatever_their_letter_case()
    {
        var rig = new ServiceRig();
        var ada = await rig.RegisterAsync("ada");
        var bob = await rig.RegisterAsync("bob");
        var first = await rig.CreateAsync(ada, "Lyra");
        first.Slot.ShouldBe(0);
        first.Job.ShouldBe(Game.Tests.World.TestGrids.SampleContent.Balance.NewCharacter.StartJobId);
        Guid.TryParseExact(first.Id, "D", out _).ShouldBeTrue();
        (await rig.Service.CreateAsync(bob, "LYRA", default)).Error.ShouldBe(AccountErrors.NameTaken);
        (await rig.Service.CreateAsync(ada, "L1", default)).Error.ShouldBe(AccountErrors.InvalidName);
        (await rig.CreateAsync(ada, "Mira")).Slot.ShouldBe(1);
        (await rig.CreateAsync(ada, "Nara")).Slot.ShouldBe(2);
        (await rig.Service.CreateAsync(ada, "Opal", default)).Error.ShouldBe(AccountErrors.NoFreeSlot);
        (await rig.Service.DeleteAsync(ada, Guid.Parse(first.Id), default)).Ok.ShouldBeTrue();
        (await rig.CreateAsync(ada, "Opal")).Slot.ShouldBe(0);
        (await rig.Service.CreateAsync(bob, "Lyra", default)).Error.ShouldBe(AccountErrors.NameTaken, "a deleted character keeps its name");
        var list = (await rig.Service.ListAsync(ada, default)).Value!;
        list.Slots.ShouldBe(3);
        list.Characters.Select(c => c.Name).ShouldBe(["Opal", "Mira", "Nara"]);
        (await rig.Service.ListAsync(bob, default)).Value!.Characters.ShouldBeEmpty();
    }

    [Fact]
    public async Task Only_its_account_deletes_a_character_and_not_while_it_is_in_the_world()
    {
        var rig = new ServiceRig();
        var ada = await rig.RegisterAsync("ada");
        var bob = await rig.RegisterAsync("bob");
        var id = Guid.Parse((await rig.CreateAsync(ada, "Lyra")).Id);
        (await rig.Service.DeleteAsync(bob, id, default)).Error.ShouldBe(AccountErrors.UnknownCharacter);
        (await rig.Service.DeleteAsync(ada, Guid.NewGuid(), default)).Error.ShouldBe(AccountErrors.UnknownCharacter);
        rig.Service.InWorld = c => c == id;
        (await rig.Service.DeleteAsync(ada, id, default)).Error.ShouldBe(AccountErrors.CharacterInUse);
        (await rig.Service.DeleteAsync(bob, id, default)).Error.ShouldBe(AccountErrors.UnknownCharacter, "someone else does not learn that it is in the world");
        rig.Service.InWorld = _ => false;
        (await rig.Service.DeleteAsync(ada, id, default)).Ok.ShouldBeTrue();
        (await rig.Service.DeleteAsync(ada, id, default)).Error.ShouldBe(AccountErrors.UnknownCharacter);
    }

    [Fact]
    public async Task A_hello_needs_a_live_session_and_a_character_of_that_account()
    {
        var rig = new ServiceRig();
        var ada = await rig.RegisterAsync("ada");
        var bob = await rig.RegisterAsync("bob");
        var id = (await rig.CreateAsync(ada, "Lyra")).Id;
        var ok = await rig.Service.ResolveAsync(ada, id, "a", default);
        ok.Value!.Character.Name.ShouldBe("Lyra");
        (await rig.Service.ResolveAsync(bob, id, "a", default)).Error.ShouldBe(AccountErrors.UnknownCharacter);
        (await rig.Service.ResolveAsync("x", id, "a", default)).Error.ShouldBe(AccountErrors.InvalidSession);
        (await rig.Service.ResolveAsync(ada, "not a guid", "a", default)).Error.ShouldBe(AccountErrors.UnknownCharacter);
        rig.Time.Now += TimeSpan.FromDays(2);
        (await rig.Service.ResolveAsync(ada, id, "a", default)).Error.ShouldBe(AccountErrors.InvalidSession);
    }

    [Fact]
    public async Task Failed_hellos_lock_the_address_before_they_cost_more_reads()
    {
        var rig = new ServiceRig();
        for (var i = 0; i < 21; i++) (await rig.Service.ResolveAsync(new string('A', 43), Guid.NewGuid().ToString("D"), "a", default)).Error.ShouldBe(AccountErrors.InvalidSession);
        var calls = rig.Memory.Calls;
        (await rig.Service.ResolveAsync(new string('A', 43), Guid.NewGuid().ToString("D"), "a", default)).Error.ShouldBe(AccountErrors.RateLimited);
        rig.Memory.Calls.ShouldBe(calls);
        rig.Clock.Advance(5000);
        (await rig.Service.ResolveAsync(new string('A', 43), Guid.NewGuid().ToString("D"), "a", default)).Error.ShouldBe(AccountErrors.InvalidSession);
    }
}

/// <summary>The one test here that measures time: never in parallel with others.</summary>
[Collection(TimingCollection.Name)]
public class AccountTimingTests
{
    [Fact]
    public async Task The_dummy_of_the_real_hasher_takes_as_long_as_a_real_check()
    {
        var rig = new ServiceRig(new Argon2idHasher(), new AccountLimits { AccountFreeFailures = 1000, AddressFreeFailures = 1000 });
        await rig.RegisterAsync();
        async Task<double> MedianAsync(string user)
        {
            var times = new List<double>();
            for (var i = 0; i < 15; i++)
            {
                var start = System.Diagnostics.Stopwatch.GetTimestamp();
                await rig.LoginAsync(user, "a wrong password");
                times.Add(System.Diagnostics.Stopwatch.GetElapsedTime(start).TotalMilliseconds);
            }
            return times.Order().ElementAt(7);
        }
        await MedianAsync("ada"); // warm up
        var known = await MedianAsync("ada");
        var unknown = await MedianAsync("nobody");
        (unknown / known).ShouldBeInRange(0.4, 2.5, $"known {known:0.0} ms, unknown {unknown:0.0} ms"); // wide: other tests share the machine. Without the dummy it is near 0.
    }
}
