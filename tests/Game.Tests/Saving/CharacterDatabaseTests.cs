using Game.Server.Accounts;
using Game.Server.Persistence;
using Game.Server.World;
using Game.Server.World.Inventory;
using Game.Server.World.Saving;
using Game.Shared.Accounts;
using Game.Shared.Items;
using Game.Shared.Protocol;
using Game.Shared.World;
using Game.Tests.Accounts;
using Game.Tests.Network;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Game.Tests.Saving;

/// <summary>T-702 against a real PostgreSQL: what is stored, who may save, whose an item is, and what a crash leaves at every step of a save.</summary>
[Trait("Category", "Database")]
public sealed class CharacterDatabaseTests(DatabaseFixture fixture, Xunit.Abstractions.ITestOutputHelper output) : IClassFixture<DatabaseFixture>
{
    private static readonly DateTime Now = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
    private static long _lastItem;

    private GameDatabase Database => fixture.Database ?? throw new InvalidOperationException(DatabaseFactAttribute.ConnectionVariable + " is not set.");

    private PostgresCharacterStore Store => new(Database);

    private PostgresAccountStore Accounts => new(Database);

    /// <summary>Ids no server run hands out (run 0).</summary>
    private static long NewId() => Interlocked.Increment(ref _lastItem);

    private static string UniqueName() => new(System.Security.Cryptography.RandomNumberGenerator.GetItems<char>("abcdefghijklmnopqrstuvwxyz", 12));

    private async Task<(Guid Account, Guid Character)> CharacterAsync()
    {
        var account = Guid.NewGuid();
        (await Accounts.TryCreateAccountAsync(account, "u" + Guid.NewGuid().ToString("N")[..16], "hash", Now, default)).ShouldBeTrue();
        var name = UniqueName();
        var (outcome, character) = await Accounts.CreateCharacterAsync(account, Guid.NewGuid(), name, CharacterNameRules.Key(name), "job.wanderer", 3, Now, default);
        outcome.ShouldBe(CreateCharacterOutcome.Created);
        return (account, character!.Id);
    }

    /// <summary>A claimed character, as a server holds it: copies are made with <see cref="Save"/>.</summary>
    private static async Task<CharacterTicket> ClaimAsync(ICharacterStore store, Guid character, long run)
    {
        var claim = await store.ClaimAsync(character, run, default);
        claim.Outcome.ShouldBe(ClaimOutcome.Claimed);
        return new CharacterTicket(character) { Claim = claim.Claim, TakenVersion = claim.Version, LandedVersion = claim.Version };
    }

    private static CharacterSave Save(CharacterTicket ticket, CharacterState state, bool final = false) => new(ticket, ++ticket.TakenVersion, state, final, []);

    private static CharacterState With(params InventoryEntry[] items) => SavedContent.Full(NewId, Now) with { Items = items, AsideItems = [] };

    private async Task<List<ItemRow>> ItemsAsync(Guid character)
    {
        await using var db = Database.Open();
        return await db.Items.AsNoTracking().Where(i => i.CharacterId == character).OrderBy(i => i.Id).ToListAsync();
    }

    private async Task<CharacterStateRow> RowAsync(Guid character)
    {
        await using var db = Database.Open();
        return await db.CharacterStates.AsNoTracking().SingleAsync(s => s.CharacterId == character);
    }

    // ---- what is stored -----------------------------------------------------------------------------------------------------------------

    [DatabaseFact]
    public async Task Every_field_is_stored_and_read_back()
    {
        var run = await Store.StartRunAsync(default);
        var (account, character) = await CharacterAsync();
        var ticket = await ClaimAsync(Store, character, run);
        var full = SavedContent.Full(NewId, Now.AddTicks(120));   // microseconds: what PostgreSQL keeps
        (await Store.SaveAsync([Save(ticket, full, final: true)], default)).ShouldBeOfType<SaveResult.Saved>();

        var back = await Store.ClaimAsync(character, run, default);
        back.State.ShouldNotBeNull();
        var (expected, actual) = (SavedContent.Fields(full), SavedContent.Fields(back.State));
        foreach (var (name, value) in expected) actual[name].ShouldBe(value, name);
        expected.Values.ShouldAllBe(v => v != "[]" && v != "null" && v != "0" && v != "\"\"", "the sample holds something in every field, so a forgotten one shows");
        (await Accounts.FindCharacterAsync(account, character, default))!.JobId.ShouldBe("job.warden", "the character list shows the job");
        back.Version.ShouldBe(1);
    }

    [DatabaseFact]
    public async Task A_character_that_was_never_saved_is_new_and_a_claim_alone_does_not_change_that()
    {
        var run = await Store.StartRunAsync(default);
        var (_, character) = await CharacterAsync();
        for (var i = 1; i <= 3; i++)
        {
            var claim = await Store.ClaimAsync(character, run, default);
            (claim.Claim, claim.Version, claim.State).ShouldBe((i, 0L, null));
        }
        (await Store.ClaimAsync(Guid.NewGuid(), run, default)).Outcome.ShouldBe(ClaimOutcome.NotFound);
    }

    [DatabaseFact]
    public async Task Items_change_move_between_slots_and_go()
    {
        var run = await Store.StartRunAsync(default);
        var (_, character) = await CharacterAsync();
        var ticket = await ClaimAsync(Store, character, run);
        var (dagger, blade, potion) = (new InventoryEntry(NewId(), "item.bone_dirk", 1, "weapon"), new InventoryEntry(NewId(), "item.iron_blade", 1), new InventoryEntry(NewId(), "item.healing_draught", 5));
        await Store.SaveAsync([Save(ticket, With(dagger, blade, potion))], default);
        // Swapped in one save: each row passes through the other's slot.
        await Store.SaveAsync([Save(ticket, With(dagger with { Slot = null, Refine = 2 }, blade with { Slot = "weapon" }, potion with { Quantity = 4 }))], default);
        var rows = await ItemsAsync(character);
        rows.Select(r => (r.ItemId, r.Quantity, r.Slot, r.Refine)).ShouldBe([("item.bone_dirk", 1, null, 2), ("item.iron_blade", 1, "weapon", 0), ("item.healing_draught", 4, null, 0)]);
        await Store.SaveAsync([Save(ticket, With(blade with { Cards = ["item.a", "item.b"] }))], default);
        (await ItemsAsync(character)).Single().Cards.ShouldBe(["item.a", "item.b"]);
        await Store.SaveAsync([Save(ticket, With())], default);
        (await ItemsAsync(character)).ShouldBeEmpty();
    }

    [DatabaseFact]
    public async Task The_database_itself_refuses_a_bad_row()
    {
        var run = await Store.StartRunAsync(default);
        var (_, character) = await CharacterAsync();
        var ticket = await ClaimAsync(Store, character, run);
        await Store.SaveAsync([Save(ticket, With(new InventoryEntry(NewId(), "item.x", 1, "weapon")))], default);
        await using var db = Database.Open();
        foreach (var sql in new[]
                 {
                     $"INSERT INTO character_items (id, character_id, item_id, quantity, cards, refine) SELECT id, character_id, 'item.y', 1, ARRAY[]::text[], 0 FROM character_items WHERE character_id = '{character}'",
                     $"INSERT INTO character_items (id, character_id, item_id, quantity, slot, cards, refine) VALUES ({NewId()}, '{character}', 'item.y', 1, 'weapon', ARRAY[]::text[], 0)",
                     $"UPDATE character_items SET quantity = 0 WHERE character_id = '{character}'",
                     $"UPDATE character_states SET currency = -1 WHERE character_id = '{character}'",
                     $"DELETE FROM character_states WHERE character_id = '{character}'",
                 })
            (await Should.ThrowAsync<PostgresException>(() => db.Database.ExecuteSqlRawAsync(sql))).SqlState.ShouldStartWith("23", Case.Sensitive, sql);
    }

    // ---- who may save ---------------------------------------------------------------------------------------------------------------------

    [DatabaseFact]
    public async Task An_older_save_never_overwrites_a_newer_one()
    {
        var run = await Store.StartRunAsync(default);
        var (_, character) = await CharacterAsync();
        var ticket = await ClaimAsync(Store, character, run);
        var old = Save(ticket, With() with { Currency = 1 });
        var late = Save(ticket, With() with { Currency = 2 });
        var newest = Save(ticket, With() with { Currency = 3 });
        (await Store.SaveAsync([old], default)).ShouldBeOfType<SaveResult.Saved>();
        (await Store.SaveAsync([newest], default)).ShouldBeOfType<SaveResult.Saved>();
        (await Store.SaveAsync([late], default)).ShouldBeOfType<SaveResult.Saved>("it is in the newer one: nothing to do");
        (await Store.SaveAsync([newest], default)).ShouldBeOfType<SaveResult.Saved>("asked twice");
        var row = await RowAsync(character);
        (row.Currency, row.Version).ShouldBe((3L, 3L));
    }

    [DatabaseFact]
    public async Task Of_two_servers_only_the_newest_claim_saves()
    {
        var (first, second) = (await Store.StartRunAsync(default), await Store.StartRunAsync(default));
        second.ShouldBeGreaterThan(first);
        var (_, character) = await CharacterAsync();
        var a = await ClaimAsync(Store, character, first);
        (await Store.SaveAsync([Save(a, With() with { Currency = 1 })], default)).ShouldBeOfType<SaveResult.Saved>();
        (await Store.ClaimAsync(character, second, default)).Outcome.ShouldBe(ClaimOutcome.HeldElsewhere, "the first is alive");

        await Store.EndRunAsync(first, default);
        var b = await ClaimAsync(Store, character, second);
        b.TakenVersion.ShouldBe(1);
        (await Store.SaveAsync([Save(a, With() with { Currency = 1_000 })], default)).ShouldBeOfType<SaveResult.Lost>().Characters.ShouldBe([character]);
        (await Store.SaveAsync([Save(b, With() with { Currency = 2 })], default)).ShouldBeOfType<SaveResult.Saved>();
        (await Store.SaveAsync([Save(a, With() with { Currency = 1_001 }, final: true)], default)).ShouldBeOfType<SaveResult.Lost>();
        var row = await RowAsync(character);
        (row.Currency, row.HeldBy).ShouldBe((2L, second), "nothing of the late server's, and it is not released by it");
        await Store.ReleaseAsync(character, first, a.Claim, default);
        (await RowAsync(character)).HeldBy.ShouldBe(second);
    }

    [DatabaseFact]
    public async Task A_run_that_stops_beating_loses_its_hold_and_one_that_beats_keeps_it()
    {
        var (first, second) = (await Store.StartRunAsync(default), await Store.StartRunAsync(default));
        var (_, character) = await CharacterAsync();
        await ClaimAsync(Store, character, first);
        await using (var db = Database.Open())
            await db.Database.ExecuteSqlAsync($"UPDATE server_runs SET seen_at = now() - interval '1 hour' WHERE id = {first}");
        await Store.BeatAsync(first, default);
        (await Store.ClaimAsync(character, second, default)).Outcome.ShouldBe(ClaimOutcome.HeldElsewhere);
        await using (var db = Database.Open())
            await db.Database.ExecuteSqlAsync($"UPDATE server_runs SET seen_at = now() - make_interval(secs => {PostgresCharacterStore.LeaseSeconds + 1}) WHERE id = {first}");
        (await Store.ClaimAsync(character, second, default)).Outcome.ShouldBe(ClaimOutcome.Claimed);
    }

    [DatabaseFact]
    public async Task A_released_claim_and_a_final_save_free_the_character()
    {
        var (first, second) = (await Store.StartRunAsync(default), await Store.StartRunAsync(default));
        var (_, character) = await CharacterAsync();
        var a = await ClaimAsync(Store, character, first);
        await Store.ReleaseAsync(character, first, a.Claim, default);
        var b = await ClaimAsync(Store, character, second);
        (await Store.SaveAsync([Save(b, With(), final: true)], default)).ShouldBeOfType<SaveResult.Saved>();
        (await RowAsync(character)).HeldBy.ShouldBeNull();
        await ClaimAsync(Store, character, first);
    }

    [DatabaseFact]
    public async Task Twenty_servers_at_once_leave_one_holder()
    {
        var (_, character) = await CharacterAsync();
        var runs = new List<long>();
        for (var i = 0; i < 20; i++) runs.Add(await Store.StartRunAsync(default));
        runs.Distinct().Count().ShouldBe(20);
        var claims = await Task.WhenAll(runs.Select(r => Task.Run(() => Store.ClaimAsync(character, r, default))));
        claims.Count(c => c.Outcome == ClaimOutcome.Claimed).ShouldBe(1);
        claims.Count(c => c.Outcome == ClaimOutcome.HeldElsewhere).ShouldBe(19);
    }

    // ---- deleting (ADR-002 row 289) ---------------------------------------------------------------------------------------------------------

    [DatabaseFact]
    public async Task A_character_is_either_deleted_or_entered_never_both()
    {
        var run = await Store.StartRunAsync(default);
        var entered = 0;
        for (var i = 0; i < 40; i++)
        {
            var (account, character) = await CharacterAsync();
            var claim = Task.Run(() => Store.ClaimAsync(character, run, default));
            var delete = Task.Run(() => Accounts.DeleteCharacterAsync(account, character, Now, default));
            await Task.WhenAll(claim, delete);
            ((await claim).Outcome == ClaimOutcome.Claimed).ShouldNotBe(await delete, $"round {i}");
            if ((await claim).Outcome == ClaimOutcome.Claimed) entered++;
        }
        entered.ShouldBeInRange(0, 40);
    }

    [DatabaseFact]
    public async Task A_held_character_is_not_deleted_and_a_deleted_one_is_not_saved()
    {
        var run = await Store.StartRunAsync(default);
        var (account, character) = await CharacterAsync();
        var ticket = await ClaimAsync(Store, character, run);
        (await Accounts.DeleteCharacterAsync(account, character, Now, default)).ShouldBeFalse();
        await Store.SaveAsync([Save(ticket, With() with { Currency = 1 })], default);
        await using (var db = Database.Open())   // by another way than the account service (support)
            await db.Characters.Where(c => c.Id == character).ExecuteUpdateAsync(s => s.SetProperty(c => c.DeletedAt, Now));
        (await Store.SaveAsync([Save(ticket, With() with { Currency = 2 })], default)).ShouldBeOfType<SaveResult.Lost>();
        (await Store.ClaimAsync(character, run, default)).Outcome.ShouldBe(ClaimOutcome.NotFound);
        (await RowAsync(character)).Currency.ShouldBe(1);
    }

    [DatabaseFact]
    public async Task A_character_of_a_dead_run_can_be_deleted()
    {
        var run = await Store.StartRunAsync(default);
        var (account, character) = await CharacterAsync();
        await ClaimAsync(Store, character, run);
        await Store.EndRunAsync(run, default);
        (await Accounts.DeleteCharacterAsync(account, character, Now, default)).ShouldBeTrue();
    }

    // ---- whose an item is -------------------------------------------------------------------------------------------------------------------

    [DatabaseFact]
    public async Task An_item_that_is_another_characters_fails_the_whole_group()
    {
        var run = await Store.StartRunAsync(default);
        var (a, b, c) = ((await CharacterAsync()).Character, (await CharacterAsync()).Character, (await CharacterAsync()).Character);
        var (ta, tb, tc) = (await ClaimAsync(Store, a, run), await ClaimAsync(Store, b, run), await ClaimAsync(Store, c, run));
        var item = new InventoryEntry(NewId(), "item.iron_blade", 1);
        await Store.SaveAsync([Save(ta, With(item))], default);
        (await Store.SaveAsync([Save(tb, With(item)), Save(tc, With() with { Currency = 5 })], default)).ShouldBeOfType<SaveResult.Duplicated>().Items.ShouldBe([item.Id]);
        (await Store.SaveAsync([Save(ta, With(item)), Save(tb, With(item))], default)).ShouldBeOfType<SaveResult.Duplicated>().Items.ShouldBe([item.Id]);
        (await RowAsync(c)).Version.ShouldBe(0, "nothing of the group");
        (await ItemsAsync(a)).Single().Id.ShouldBe(item.Id);
        (await ItemsAsync(b)).ShouldBeEmpty();
    }

    [DatabaseFact]
    public async Task Two_saves_at_once_cannot_both_get_an_item()
    {
        var run = await Store.StartRunAsync(default);
        for (var round = 0; round < 15; round++)
        {
            var (a, b) = ((await CharacterAsync()).Character, (await CharacterAsync()).Character);
            var (ta, tb) = (await ClaimAsync(Store, a, run), await ClaimAsync(Store, b, run));
            var item = new InventoryEntry(NewId(), "item.iron_blade", 1);
            var results = await Task.WhenAll(Task.Run(() => Store.SaveAsync([Save(ta, With(item))], default)), Task.Run(() => Store.SaveAsync([Save(tb, With(item))], default)));
            results.Count(r => r is SaveResult.Saved).ShouldBe(1);
            results.Count(r => r is SaveResult.Duplicated).ShouldBe(1);
            ((await ItemsAsync(a)).Count + (await ItemsAsync(b)).Count).ShouldBe(1);
        }
    }

    [DatabaseFact]
    public async Task Crossing_groups_at_once_do_not_deadlock_for_good()
    {
        var run = await Store.StartRunAsync(default);
        var tickets = new List<CharacterTicket>();
        for (var i = 0; i < 6; i++) tickets.Add(await ClaimAsync(Store, (await CharacterAsync()).Character, run));
        var random = new Random(1);
        for (var round = 0; round < 10; round++)
        {
            // Each character in one group at a time, as CharacterSaves keeps it; the groups' orders differ.
            var shuffled = tickets.OrderBy(_ => random.Next()).ToList();
            var groups = new[] { shuffled.Take(3).ToList(), shuffled.Skip(3).ToList() };
            var results = await Task.WhenAll(groups.Select(g => Task.Run(() => Store.SaveAsync(g.Select(t => Save(t, With(new InventoryEntry(NewId(), "item.x", 1)))).ToList(), default))));
            results.ShouldAllBe(r => r is SaveResult.Saved);
        }
    }

    // ---- crash safety: the fault injection ------------------------------------------------------------------------------------------------

    private sealed class Killed : Exception;

    public static TheoryData<string, bool> Steps
    {
        get
        {
            var data = new TheoryData<string, bool>();
            foreach (var step in PostgresCharacterStore.SaveSteps)
            foreach (var killBackend in new[] { false, true })
                data.Add(step, killBackend);
            return data;
        }
    }

    /// <summary>
    /// Ada has put a blade, some draughts and (as a trade will) currency into Bob's hands, and Bob a buckler into hers; neither is saved since. The save dies at <paramref name="step"/>: the process
    /// (<paramref name="killBackend"/> false: nothing more is sent on that connection), or the database's connection. Whatever the next server finds, each thing exists once or not
    /// at all, and it finds both characters from the same side of the hand-over.
    /// </summary>
    [DatabaseTheory]
    [MemberData(nameof(Steps))]
    public async Task A_crash_at_every_step_of_a_shared_save_duplicates_and_splits_nothing(string step, bool killBackend)
    {
        var run = await Store.StartRunAsync(default);
        var (ada, bob) = ((await CharacterAsync()).Character, (await CharacterAsync()).Character);
        var (ta, tb) = (await ClaimAsync(Store, ada, run), await ClaimAsync(Store, bob, run));
        var blade = new InventoryEntry(NewId(), "item.iron_blade", 1) { Refine = 4, Cards = ["item.gelcap_card"] };
        var buckler = new InventoryEntry(NewId(), "item.wooden_buckler", 1, "shield");
        var (draughts, taken) = (new InventoryEntry(NewId(), "item.healing_draught", 10), new InventoryEntry(NewId(), "item.healing_draught", 4));
        await Store.SaveAsync([Save(ta, With(blade, draughts) with { Currency = 100 }), Save(tb, With(buckler) with { Currency = 0 })], default);

        var store = new PostgresCharacterStore(Database)
        {
            OnStep = async at =>
            {
                if (at != step) return;
                if (!killBackend) throw new Killed();
                await using var db = Database.Open();   // every other connection of this test's database that is in a transaction
                await db.Database.ExecuteSqlRawAsync(
                    "SELECT pg_terminate_backend(pid) FROM pg_stat_activity WHERE datname = current_database() AND pid <> pg_backend_pid() AND state LIKE 'idle in transaction%'");
                NpgsqlConnection.ClearAllPools();
            },
        };
        CharacterSave[] group =
        [
            Save(ta, With(draughts with { Quantity = 6 }, buckler) with { Currency = 40 }, final: true),
            Save(tb, With(blade with { Slot = "weapon" }, taken) with { Currency = 60 }, final: true),
        ];
        var failure = await Record.ExceptionAsync(() => store.SaveAsync(group, default));
        if (!killBackend) failure.ShouldBeOfType<Killed>();

        // The next server, once the crashed one's run is over.
        await Store.EndRunAsync(run, default);
        var next = await Store.StartRunAsync(default);
        var (a, b) = (await Store.ClaimAsync(ada, next, default), await Store.ClaimAsync(bob, next, default));
        var after = a.Version == 2;
        (b.Version == 2).ShouldBe(after, "both saves or neither");
        // At "begun" the database has not heard of the transaction yet (the driver sends BEGIN with the first statement), so there was no connection to kill and the save went on.
        after.ShouldBe(step == "committed" || (step == "begun" && killBackend), "only a commit makes it so");
        var items = a.State!.Items.Select(i => ("ada", i)).Concat(b.State!.Items.Select(i => ("bob", i))).ToList();
        items.Select(i => i.i.Id).Distinct().Count().ShouldBe(items.Count, "no item twice");
        items.Where(i => i.i.ItemId == "item.iron_blade").Select(i => i.Item1).ShouldBe([after ? "bob" : "ada"]);
        items.Where(i => i.i.ItemId == "item.wooden_buckler").Select(i => i.Item1).ShouldBe([after ? "ada" : "bob"]);
        items.Where(i => i.i.ItemId == "item.healing_draught").Sum(i => i.i.Quantity).ShouldBe(10);
        (a.State.Currency + b.State.Currency).ShouldBe(100);
        items.Single(i => i.i.ItemId == "item.iron_blade").i.Refine.ShouldBe(4);
        items.Single(i => i.i.ItemId == "item.iron_blade").i.Cards.ShouldBe(["item.gelcap_card"]);

        // And the crashed server, if it only thought so (a lost answer, a stall), cannot undo what the next one has claimed.
        (await Store.SaveAsync(group, default)).ShouldBeOfType<SaveResult.Lost>();
    }

    /// <summary>
    /// The same from the game: Ada drops a blade, Bob picks it up, and saves die at a random step or all the time until the process dies at a random moment. Afterwards each item exists
    /// once, or (what was on the ground, or last moved) not at all.
    /// </summary>
    [DatabaseFact]
    public async Task Handing_items_over_and_crashing_at_random_never_duplicates_one()
    {
        var random = new Random(702);
        var outcomes = new SortedDictionary<string, int>();
        for (var round = 0; round < 40; round++)
        {
            var (ada, bob) = ((await CharacterAsync()).Character, (await CharacterAsync()).Character);
            var dead = false;
            var deaths = random.Next(0, 3);
            var store = new PostgresCharacterStore(Database)
            {
                OnStep = async _ =>
                {
                    await Task.Yield();
                    bool die;
                    lock (random) die = Volatile.Read(ref dead) || (deaths > 0 && random.Next(20) == 0);
                    if (die) throw new StoreUnavailableException("test: died here");
                },
            };
            var rig = SaveRig.Start(store, SaveRig.Fast with { IntervalMs = 30 });
            var (a, b) = (await rig.EnterAsync(ada), await rig.EnterAsync(bob));
            var blade = rig.Put(a, "item.iron_blade");
            rig.Tickets[a].SaveSoon = true;
            rig.Ticks(2);
            if (random.Next(2) == 0) await rig.SettledAsync();
            var ticksToLive = random.Next(1, 25);
            var holder = a;
            for (var tick = 0; tick < ticksToLive; tick++)
            {
                var other = holder == a ? b : a;
                if (rig.Bag(holder).Find(blade) is not null)
                {
                    rig.Send(new DropItem(holder, blade, 1));
                    if (random.Next(3) == 0 && rig.Bag(holder).Entries.FirstOrDefault(e => e.ItemId == "item.healing_draught") is { } draught) rig.Send(new DropItem(holder, draught.Id, 1));
                }
                foreach (var ground in rig.On(a).Entities.Where(e => e.Kind == EntityKind.Item && e.GroundItem?.Source is not null).ToList()) rig.Send(new PickUp(other, ground.Id));
                rig.Ticks(1);
                if (rig.Bag(other).Find(blade) is not null) holder = other;
                if (random.Next(4) == 0) await Task.Delay(random.Next(0, 8));
            }
            if (random.Next(2) == 0)
            {
                // Sometimes there is time for what happened to land (with a limit: a save may keep dying; under load a fixed wait is too short for any).
                rig.Tickets[a].SaveSoon = rig.Tickets[b].SaveSoon = true;
                rig.Ticks(2);
                var waited = System.Diagnostics.Stopwatch.StartNew();
                bool Landed(EntityId who) => rig.Tickets[who].LandedVersion >= rig.Tickets[who].TakenVersion;
                while (!(Landed(a) && Landed(b)) && waited.ElapsedMilliseconds < 3_000) await Task.Delay(5);
            }
            Volatile.Write(ref dead, true);   // the process dies: whatever was under way ends where it is
            await rig.StopAsync();

            await Store.EndRunAsync(rig.Saves.Run, default);
            var run = await Store.StartRunAsync(default);
            var (sa, sb) = ((await Store.ClaimAsync(ada, run, default)).State, (await Store.ClaimAsync(bob, run, default)).State);
            var all = (sa?.Items ?? []).Concat(sb?.Items ?? []).ToList();
            all.Count(i => i.Id == blade).ShouldBeLessThanOrEqualTo(1, $"round {round}");
            all.Select(i => i.Id).Distinct().Count().ShouldBe(all.Count);
            all.Where(i => i.ItemId == "item.healing_draught").Sum(i => i.Quantity).ShouldBeLessThanOrEqualTo(6, $"round {round}: three each at the start, however they moved");
            var where = all.Count(i => i.Id == blade) == 0 ? "lost" : (sa?.Items ?? []).Any(i => i.Id == blade) ? "ada" : "bob";
            outcomes[where] = outcomes.GetValueOrDefault(where) + 1;
        }
        output.WriteLine(string.Join(", ", outcomes.Select(o => $"{o.Key} {o.Value}")));
        outcomes.Count.ShouldBe(3, "the rounds end in every way there is: " + string.Join(", ", outcomes.Select(o => $"{o.Key} {o.Value}")));
    }

    /// <summary>Without the links, the same hand-over does duplicate: what the order rule is for (and proof that the test above can fail).</summary>
    [DatabaseFact]
    public async Task Without_the_order_the_receivers_save_would_be_refused_by_the_database()
    {
        await using var rig = SaveRig.Start(Store);
        var (a, b) = (await rig.EnterAsync((await CharacterAsync()).Character), await rig.EnterAsync((await CharacterAsync()).Character));
        var blade = rig.Put(a, "item.iron_blade");
        await rig.SaveAsync(a);
        await rig.SettledAsync();
        rig.Bag(b).SetEntries(rig.Bag(b).Entries.Append(rig.Bag(a).Find(blade)!).ToList());   // no PutDown, no DependOn
        rig.Bag(a).SetEntries(rig.Bag(a).Entries.Where(e => e.Id != blade).ToList());
        rig.Tickets[b].SaveSoon = true;
        rig.Ticks(2);
        await Poll.UntilAsync(() => rig.Tickets[b].IsLost, l => l, "refused");
        rig.Log.Lines.ShouldContain(l => l.Contains("DUPLICATED ITEMS"));
    }

    // ---- the whole stack ------------------------------------------------------------------------------------------------------------------

    private Task<GatewayRig> StackAsync() => GatewayRig.StartAsync(accounts: new AccountRigOptions(Accounts, new FakeHasher(), new AccountLimits(), TimeProvider.System)
    {
        Characters = Store, SaveOptions = new CharacterSaveOptions { IntervalMs = 500, SpacingMs = 100 },
    });

    [DatabaseFact]
    public async Task Play_stop_the_server_start_another_and_find_it_all_back()
    {
        string token, id;
        long currency;
        GridCell cell;
        List<InventoryEntryRecord> before;
        var user = "u" + Guid.NewGuid().ToString("N")[..16];
        var first = await StackAsync();
        {
            token = (await first.Accounts!.RegisterAsync(user, ServiceRig.Password, "a", default)).Value!.Token;
            id = (await first.Accounts.CreateAsync(token, UniqueName(), default)).Value!.Id;
            var client = await first.PlayAsync(token, id);
            await client.WaitForAsync<InventoryMessage>();
            cell = first.NearStart((3, 0), (-3, 0), (0, 3));
            await client.SendAsync(new MoveMessage(cell));
            await Poll.UntilAsync(() => client.Seen(client.Id)?.Cell, c => c?.Equals(cell) == true, "the walk");
            await client.SendAsync(new RaiseStatMessage("str", 1));
            await client.WaitForAsync<StatsMessage>(m => m.Stats.Any(s => s.Stat == "str" && s.Value == 2));
            await client.SendAsync(new UnequipMessage("weapon"));
            await client.WaitForAsync<InventoryChangedMessage>(m => m.Cause == "unequipped");
            first.Inventories.TryGet(new EntityId(client.Id), out var bag).ShouldBeTrue();
            currency = bag.Currency = 4242;
            before = bag.Entries.Select(OwnInventory.Record).ToList();
        }
        await first.DisposeAsync();
        first.Drained!.Failed.ShouldBeEmpty();

        await using var second = await StackAsync();
        second.Saves!.Run.ShouldBeGreaterThan(first.Saves!.Run);
        var back = await second.PlayAsync(token, id);   // the account's session outlives the server
        var inventory = await back.WaitForAsync<InventoryMessage>();
        inventory.Currency.ShouldBe(currency);
        inventory.Entries.Select(e => (e.Id, e.Item, e.Quantity, e.Slot, e.Refine)).ShouldBe(before.Select(e => (e.Id, e.Item, e.Quantity, e.Slot, e.Refine)));
        (await back.WaitForAsync<StatsMessage>()).Stats.Single(s => s.Stat == "str").Value.ShouldBe(2);
        back.Seen(back.Id)!.Cell.ShouldBe(cell);
        back.Seen(back.Id)!.Weapon.ShouldBe("unarmed");
    }

    [DatabaseFact]
    public async Task A_database_that_goes_away_while_playing_is_survived()
    {
        await using var rig = SaveRig.Start(Store);
        var (_, character) = await CharacterAsync();
        var id = await rig.EnterAsync(character);
        await rig.SettledAsync();
        for (var i = 0; i < 5; i++)
        {
            await using (var admin = new NpgsqlConnection(new NpgsqlConnectionStringBuilder(fixture.ConnectionString) { Pooling = false }.ConnectionString))
            {
                await admin.OpenAsync();
                await using var kill = new NpgsqlCommand("SELECT pg_terminate_backend(pid) FROM pg_stat_activity WHERE datname = current_database() AND pid <> pg_backend_pid()", admin);
                await kill.ExecuteNonQueryAsync();
            }
            rig.Bag(id).Currency = i + 1;
            await rig.SaveAsync(id);
        }
        NpgsqlConnection.ClearAllPools();
        (await RowAsync(character)).Currency.ShouldBe(5);
        rig.Tickets[id].IsLost.ShouldBeFalse();
    }
}

/// <summary>A theory under the rule of <see cref="DatabaseFactAttribute"/>.</summary>
public sealed class DatabaseTheoryAttribute : TheoryAttribute
{
    public DatabaseTheoryAttribute() => Skip = new DatabaseFactAttribute().Skip;
}
