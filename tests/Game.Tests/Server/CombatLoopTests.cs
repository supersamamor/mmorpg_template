using Game.Server.World;
using Game.Server.World.Combat;
using Game.Server.World.Monsters;
using Game.Shared.Definitions;
using Game.Shared.Progression;
using Game.Shared.Random;
using Game.Shared.Stats;
using Game.Shared.Vocabulary;
using Game.Shared.World;
using Game.Tests.World;

namespace Game.Tests.Server;

/// <summary>T-302: death, respawn, EXP distribution, drops and ground items, through intents and events.</summary>
public class CombatLoopTests
{
    // ---- monster death -----------------------------------------------------------------------------------------------------------------

    [Fact]
    public void A_killed_monster_dies_once_and_the_spawner_respawns_it_after_its_respawn_time()
    {
        var weak = TestCombat.Monster("monster.t_weak", maxHp: 5, atk: 0);
        var grid = TestCombat.Open(12, 8, [TestMonsters.Spawn(weak, 5, 4, respawnSeconds: 3)]);
        var rig = new CombatRig(grid, [weak], ai: true);
        var player = rig.AddPlayer(new GridCell(1, 4));
        rig.Ticks();
        var monster = rig.Instance.Entities.Single(e => e.Kind == EntityKind.Monster).Id;

        rig.Send(new AttackTarget(player, monster));
        rig.TickUntil(() => rig.Of<EntityDied>().Any(), 200, "kill");
        var (deathTick, died) = rig.TimedOf<EntityDied>().Single();
        died.Entity.ShouldBe(monster);
        died.Killer.ShouldBe(player);
        rig.Has(monster).ShouldBeTrue();   // despawned by the spawner, on its next tick

        rig.Ticks();
        rig.Has(monster).ShouldBeFalse();
        rig.TickUntil(() => rig.Of<EntitySpawned>().Count == 2, 100, "respawn");
        var (respawnTick, _) = rig.TimedOf<EntitySpawned>()[1];
        (respawnTick - deathTick).ShouldBe(30);   // 3 s after the tick of the death
        rig.Of<EntityDied>().Count.ShouldBe(1);
        var fresh = rig.Instance.Entities.Single(e => e.Kind == EntityKind.Monster);
        fresh.Vitals!.Hp.ShouldBe(5);
        fresh.Dead.ShouldBeFalse();
    }

    // ---- player death and respawn -----------------------------------------------------------------------------------------------------

    private static (CombatRig Rig, EntityId Monster, EntityId Player) Duel(int penaltyPercent = 10)
    {
        var biter = TestCombat.Monster("monster.t_biter", maxHp: 1000, atk: 50);
        var rig = new CombatRig(TestCombat.Open(12, 8), [biter], TestCombat.Balance(penaltyPercent));
        var monster = rig.AddMonster(biter, new GridCell(6, 4));
        var player = rig.AddPlayer(new GridCell(7, 4));
        rig.Ticks();
        return (rig, monster, player);
    }

    private static void KillPlayer(CombatRig rig, EntityId monster, EntityId player)
    {
        rig.Get(player).Cell = new GridCell(7, 4);   // next to the monster (a respawn puts it at (1, 1))
        for (var i = 0; i < 400 && !rig.Get(player).Dead; i++)
        {
            rig.Request(monster, player);
            rig.Ticks();
        }
        rig.Get(player).Dead.ShouldBeTrue();
        rig.Ticks(2);   // let the last requests resolve (refused: target dead)
    }

    [Fact]
    public void A_dead_player_stays_dead_until_respawn_then_comes_back_with_half_hp_at_the_respawn_point()
    {
        var (rig, monster, player) = Duel();
        rig.Progressions.TryGet(player, out var state).ShouldBeTrue();
        state.BaseExp = 5;
        KillPlayer(rig, monster, player);

        var died = rig.Of<EntityDied>().ShouldHaveSingleItem();
        died.ShouldBe(new EntityDied(player, monster, EntityKind.Player, null, new GridCell(7, 4)));
        rig.Hp(player).ShouldBe(0);
        rig.Ticks(30);
        rig.Get(player).Dead.ShouldBeTrue();
        rig.Get(player).Cell.ShouldBe(new GridCell(7, 4));

        rig.Send(new Respawn(player));
        rig.Ticks();
        var entity = rig.Get(player);
        entity.Dead.ShouldBeFalse();
        entity.Cell.ShouldBe(new GridCell(1, 1));
        var max = entity.Vitals!.MaxHp;
        entity.Vitals.Hp.ShouldBe(max * 50 / 100);
        rig.Of<PlayerRespawned>().ShouldHaveSingleItem().ShouldBe(new PlayerRespawned(player, TestCombat.MapId, new GridCell(1, 1), max / 2));

        rig.Send(new Respawn(player));   // alive: ignored
        rig.Ticks();
        rig.Of<PlayerRespawned>().Count.ShouldBe(1);
    }

    [Fact]
    public void Death_costs_a_share_of_the_level_requirement_and_never_a_level()
    {
        var requirement = TestGrids.SampleContent.ExpTables.Get("exp.base").Exp[0];   // level 1 -> 2
        var (rig, monster, player) = Duel(penaltyPercent: 10);
        rig.Progressions.TryGet(player, out var state).ShouldBeTrue();
        state.BaseExp = requirement - 1;
        KillPlayer(rig, monster, player);
        state.BaseExp.ShouldBe(requirement - 1 - requirement * 10 / 100);
        rig.Of<ExpLost>().ShouldHaveSingleItem().ShouldBe(new ExpLost(player, requirement * 10 / 100));

        // With little EXP the loss stops at 0 and the level stays.
        rig.Send(new Respawn(player));
        rig.Ticks();
        state.BaseExp = 1;
        KillPlayer(rig, monster, player);
        state.BaseExp.ShouldBe(0);
        state.BaseLevel.ShouldBe(1);
        rig.Of<ExpLost>()[1].BaseExp.ShouldBe(1);

        // Nothing to lose: no event.
        rig.Send(new Respawn(player));
        rig.Ticks();
        KillPlayer(rig, monster, player);
        rig.Of<ExpLost>().Count.ShouldBe(2);
    }

    [Fact]
    public void A_zero_penalty_takes_nothing()
    {
        var (rig, monster, player) = Duel(penaltyPercent: 0);
        rig.Progressions.TryGet(player, out var state).ShouldBeTrue();
        state.BaseExp = 10;
        KillPlayer(rig, monster, player);
        state.BaseExp.ShouldBe(10);
        rig.Of<ExpLost>().ShouldBeEmpty();
    }

    // ---- EXP distribution -------------------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData(new long[] { 37 }, 100, new long[] { 100 })]
    [InlineData(new long[] { 70, 30 }, 100, new long[] { 70, 30 })]
    [InlineData(new long[] { 5, 5, 5 }, 10, new long[] { 4, 3, 3 })]          // equal remainders: the first hitter
    [InlineData(new long[] { 1, 2, 3 }, 10, new long[] { 2, 3, 5 })]          // 1.67 / 3.33 / 5: the largest remainder (.67) gets it
    [InlineData(new long[] { 10, 0, 10 }, 7, new long[] { 4, 0, 3 })]
    [InlineData(new long[] { 3, 3 }, 0, new long[] { 0, 0 })]
    [InlineData(new long[] { 0, 0 }, 9, new long[] { 0, 0 })]
    public void Exp_is_split_by_damage_share_with_nothing_lost(long[] damage, long total, long[] expected)
    {
        var shares = new long[damage.Length];
        ExpSplit.Split(total, damage, shares);
        shares.ShouldBe(expected);
        if (damage.Any(d => d > 0)) shares.Sum().ShouldBe(total);
    }

    [Fact]
    public void Exp_split_always_adds_up_and_never_overflows()
    {
        IRandom random = new Pcg32Random(5, 1);
        for (var round = 0; round < 500; round++)
        {
            var damage = Enumerable.Range(0, 1 + random.Next(6)).Select(_ => (long)random.Next(100_000)).ToArray();
            var total = (long)random.Next(1_000_000);
            var shares = new long[damage.Length];
            ExpSplit.Split(total, damage, shares);
            if (damage.Sum() > 0) shares.Sum().ShouldBe(total);
            for (var i = 0; i < damage.Length; i++)
                Math.Abs(shares[i] - (double)total * damage[i] / damage.Sum()).ShouldBeLessThan(1.0 + 1e-9);
        }
        var big = new long[2];
        ExpSplit.Split(long.MaxValue, [long.MaxValue / 2, long.MaxValue / 2], big);
        big.Sum().ShouldBe(long.MaxValue);
    }

    private static (CombatRig Rig, EntityId Monster) Target(MonsterDef def)
    {
        var rig = new CombatRig(TestCombat.Open(12, 8), [def]);
        var monster = rig.AddMonster(def, new GridCell(6, 4));
        rig.Ticks();
        return (rig, monster);
    }

    /// <summary>One resolver hit from <paramref name="player"/>: a request resolves the same for any attacker (the test stands in for the AI).</summary>
    private static void Hit(CombatRig rig, EntityId player, EntityId monster)
    {
        rig.Request(player, monster, range: 30);
        rig.Ticks(2);
    }

    [Fact]
    public void A_seventy_thirty_fight_pays_seventy_thirty()
    {
        var def = TestCombat.Monster("monster.t_exp", maxHp: 100, def: 0, baseExp: 50, jobExp: 10);
        var (rig, monster) = Target(def);
        var a = rig.AddPlayer(new GridCell(5, 4));
        var b = rig.AddPlayer(new GridCell(7, 4));
        rig.Ticks();
        // The players' atk is the formula's; give them exact damage with a flat atk and no defense on the monster.
        rig.Get(a).Stats!.Modifiers.Add("test", DerivedStats.Atk, ModifierOp.Override, 7);
        rig.Get(b).Stats!.Modifiers.Add("test", DerivedStats.Atk, ModifierOp.Override, 3);
        rig.Get(a).Stats!.Modifiers.Add("test", DerivedStats.Hit, ModifierOp.Override, 1000);
        rig.Get(b).Stats!.Modifiers.Add("test", DerivedStats.Hit, ModifierOp.Override, 1000);
        rig.Get(a).Stats!.Modifiers.Add("test", DerivedStats.Crit, ModifierOp.Override, 0);
        rig.Get(b).Stats!.Modifiers.Add("test", DerivedStats.Crit, ModifierOp.Override, 0);
        // Alternate one hit each until the monster dies; misses (5%) are re-tried so each lands exactly 10 hits.
        int hitsA = 0, hitsB = 0;
        while (!rig.Get(monster).Dead)
        {
            var who = hitsA <= hitsB ? a : b;
            var before = rig.Of<DamageDealt>().Count;
            Hit(rig, who, monster);
            if (rig.Of<DamageDealt>().Skip(before).Single().Result == HitResults.Miss) continue;
            if (who == a) hitsA++;
            else hitsB++;
        }
        hitsA.ShouldBe(10);
        hitsB.ShouldBe(10);
        var gains = rig.Of<ExpGained>().ToDictionary(g => g.Player);
        gains[a].Damage.ShouldBe(70);
        gains[b].Damage.ShouldBe(30);
        gains[a].BaseExp.ShouldBe(35);
        gains[b].BaseExp.ShouldBe(15);
        gains[a].JobExp.ShouldBe(7);
        gains[b].JobExp.ShouldBe(3);
    }

    [Fact]
    public void An_attacker_who_left_the_map_gets_nothing_and_the_rest_is_shared_out()
    {
        var def = TestCombat.Monster("monster.t_exp", maxHp: 100_000, baseExp: 90, jobExp: 9);
        var (rig, monster) = Target(def);
        var stays = rig.AddPlayer(new GridCell(5, 4));
        var leaves = rig.AddPlayer(new GridCell(7, 4));
        rig.Ticks();
        while (!rig.Combat.DamageLog(monster).Any(d => d.Attacker == leaves)) Hit(rig, leaves, monster);
        while (!rig.Combat.DamageLog(monster).Any(d => d.Attacker == stays)) Hit(rig, stays, monster);
        rig.World.Despawn(leaves);
        rig.Ticks();
        rig.Get(monster).Vitals!.Hp = 1;
        while (!rig.Get(monster).Dead) Hit(rig, stays, monster);

        var gain = rig.Of<ExpGained>().ShouldHaveSingleItem();
        gain.Player.ShouldBe(stays);
        gain.BaseExp.ShouldBe(90);
        gain.JobExp.ShouldBe(9);
        rig.Progressions.TryGet(leaves, out _).ShouldBeFalse();   // released when it left the world
    }

    [Fact]
    public void A_level_up_through_a_kill_updates_the_stat_sheet()
    {
        var def = TestCombat.Monster("monster.t_rich", maxHp: 1, baseExp: 5_000, jobExp: 5_000);
        var (rig, monster) = Target(def);
        var player = rig.AddPlayer(new GridCell(5, 4));
        rig.Ticks();
        var sheet = rig.Get(player).Stats!;
        var maxBefore = rig.Get(player).Vitals!.MaxHp;
        rig.Send(new AttackTarget(player, monster));
        rig.TickUntil(() => rig.Get(monster).Dead, 100, "kill");

        rig.Progressions.TryGet(player, out var state).ShouldBeTrue();
        state.BaseLevel.ShouldBeGreaterThan(1);
        state.JobLevel.ShouldBeGreaterThan(1);
        sheet.Base.BaseLevel.ShouldBe(state.BaseLevel);
        sheet.Base.JobLevel.ShouldBe(state.JobLevel);
        var levelUps = rig.Of<PlayerProgressed>().Where(p => p.Change is LevelUp).ToList();
        levelUps.Count.ShouldBe(state.BaseLevel - 1);
        levelUps.ShouldAllBe(p => p.Player == player && p.Name == EventNames.LevelUp);
        rig.Of<PlayerProgressed>().Count(p => p.Change is JobLevelUp).ShouldBe(state.JobLevel - 1);
        var gain = rig.Of<ExpGained>().ShouldHaveSingleItem();
        (gain.BaseLevel, gain.JobLevel).ShouldBe((state.BaseLevel, state.JobLevel));
        // max_hp scales with base level in the sample formulas: the vitals see the new max; current HP is not refilled.
        rig.Get(player).Vitals!.MaxHp.ShouldBe(sheet.GetInt(DerivedStats.MaxHp));
        rig.Get(player).Vitals!.MaxHp.ShouldBeGreaterThan(maxBefore);
        rig.Hp(player).ShouldBe(maxBefore);
    }

    // ---- drops ------------------------------------------------------------------------------------------------------------------------

    [Fact]
    public void Drop_entries_roll_independently_with_chance_and_quantity()
    {
        var table = TestCombat.Drops("drop.t", new DropEntryDef { ItemId = "item.always", Chance = 1, Min = 2, Max = 5 },
            new DropEntryDef { ItemId = "item.never", Chance = 0 }, new DropEntryDef { ItemId = "item.half", Chance = 0.5 });
        IRandom random = new Pcg32Random(17, 3);
        var rolled = new List<RolledDrop>();
        var quantities = new HashSet<int>();
        var halves = 0;
        for (var i = 0; i < 2000; i++)
        {
            rolled.Clear();
            DropRoller.Roll(table, random, rolled);
            var always = rolled.Where(r => r.ItemId == "item.always").ShouldHaveSingleItem();
            always.Quantity.ShouldBeInRange(2, 5);
            quantities.Add(always.Quantity);
            rolled.ShouldNotContain(r => r.ItemId == "item.never");
            halves += rolled.Count(r => r.ItemId == "item.half");
            rolled.Where(r => r.ItemId == "item.half").ShouldAllBe(r => r.Quantity == 1);
        }
        quantities.Order().ShouldBe(new[] { 2, 3, 4, 5 });
        halves.ShouldBeInRange(900, 1100);
    }

    [Fact]
    public void Drops_are_deterministic_per_seed()
    {
        var table = TestGrids.SampleContent.Drops.Get("drop.thornback_boar");
        List<RolledDrop> Run(ulong seed)
        {
            IRandom random = new Pcg32Random(seed, 1);
            var all = new List<RolledDrop>();
            for (var i = 0; i < 200; i++) DropRoller.Roll(table, random, all);
            return all;
        }
        Run(9).ShouldBe(Run(9));
        Run(9).ShouldNotBe(Run(10));
    }

    [Fact]
    public void Drops_never_land_on_blocked_cells()
    {
        string[] rows =
        [
            "#####",
            "#.#.#",
            "##.##",
            "#.#.#",
            "#####",
        ];
        var grid = TestCombat.Grid(rows);
        IRandom random = new Pcg32Random(4, 4);
        var seen = new HashSet<GridCell>();
        for (var i = 0; i < 500; i++)
        {
            DropRoller.TryPlace(grid, new GridCell(2, 2), 1, random, out var cell).ShouldBeTrue();
            grid.IsWalkable(cell.X, cell.Y).ShouldBeTrue();
            GridDistance.Chebyshev(cell, new GridCell(2, 2)).ShouldBeLessThanOrEqualTo(1);
            seen.Add(cell);
        }
        seen.Count.ShouldBe(5);   // the center and its four walkable diagonals
        DropRoller.TryPlace(grid, new GridCell(2, 2), 0, random, out var center).ShouldBeTrue();
        center.ShouldBe(new GridCell(2, 2));
        DropRoller.TryPlace(grid, new GridCell(0, 0), 0, random, out _).ShouldBeFalse();
    }

    [Fact]
    public void A_kill_drops_ground_items_owned_by_the_top_damager_that_expire()
    {
        var table = TestCombat.Drops("drop.t_sure", new DropEntryDef { ItemId = "item.gel_glob", Chance = 1, Min = 2, Max = 2 },
            new DropEntryDef { ItemId = "item.boar_tusk", Chance = 1 });
        var def = TestCombat.Monster("monster.t_loot", maxHp: 1, drops: ["drop.t_sure"]);
        var rig = new CombatRig(TestCombat.Open(12, 8), [def], TestCombat.Balance(priorityMs: 3000, lifetimeMs: 10_000), [table]);
        var monster = rig.AddMonster(def, new GridCell(6, 4));
        var player = rig.AddPlayer(new GridCell(5, 4));
        rig.Ticks();
        rig.Send(new AttackTarget(player, monster));
        rig.TickUntil(() => rig.Get(monster).Dead, 100, "kill");
        var deathMs = rig.Now - TickRate.IntervalMs;

        var dropped = rig.Of<ItemDropped>();
        dropped.Select(d => (d.ItemId, d.Quantity)).ShouldBe(new[] { ("item.gel_glob", 2), ("item.boar_tusk", 1) });
        foreach (var d in dropped)
        {
            var item = rig.Get(d.Item);
            item.Kind.ShouldBe(EntityKind.Item);
            item.DefinitionId.ShouldBe(d.ItemId);
            item.Cell.ShouldBe(d.Cell);
            GridDistance.Chebyshev(d.Cell, new GridCell(6, 4)).ShouldBeLessThanOrEqualTo(1);
            item.GroundItem.ShouldBe(new GroundItem(d.ItemId, d.Quantity, monster, player, deathMs, deathMs + 3000, deathMs + 10_000));
            (d.Owner, d.OwnerUntilMs, d.ExpiresAtMs).ShouldBe((player, deathMs + 3000, deathMs + 10_000));
        }

        var first = dropped[0].Item;
        rig.TickUntil(() => !rig.Has(first), 200, "expiry");
        (rig.Now - TickRate.IntervalMs - deathMs).ShouldBe(10_000);   // gone in the tick its lifetime ended
        rig.Of<ItemExpired>().Select(e => e.Item).ShouldBe(dropped.Select(d => d.Item));
        rig.Instance.Entities.ShouldNotContain(e => e.Kind == EntityKind.Item);
    }

    [Fact]
    public void Loot_priority_is_the_owners_alone_until_it_runs_out()
    {
        var owner = new EntityId(1);
        var other = new EntityId(2);
        var item = new GroundItem("item.x", 1, new EntityId(9), owner, 1000, 4000, 61_000);
        LootRules.MayPickUp(item, owner, 1000).ShouldBeTrue();
        LootRules.MayPickUp(item, other, 1000).ShouldBeFalse();
        LootRules.MayPickUp(item, other, 3999).ShouldBeFalse();
        LootRules.MayPickUp(item, other, 4000).ShouldBeTrue();
        LootRules.MayPickUp(item, owner, 60_999).ShouldBeTrue();
        LootRules.MayPickUp(item, owner, 61_000).ShouldBeFalse();   // expired
        LootRules.MayPickUp(item with { Owner = null }, other, 1000).ShouldBeTrue();
    }

    [Fact]
    public void Monsters_without_damage_from_players_drop_items_anyone_may_take()
    {
        var table = TestCombat.Drops("drop.t_sure", new DropEntryDef { ItemId = "item.gel_glob", Chance = 1 });
        var def = TestCombat.Monster("monster.t_loot", maxHp: 1, drops: ["drop.t_sure"]);
        var rig = new CombatRig(TestCombat.Open(12, 8), [def], drops: [table]);
        var victim = rig.AddMonster(def, new GridCell(6, 4));
        var killer = rig.AddMonster(def, new GridCell(5, 4));
        rig.Ticks();
        while (!rig.Get(victim).Dead)
        {
            rig.Request(killer, victim);
            rig.Ticks();
        }
        rig.Of<ExpGained>().ShouldBeEmpty();
        rig.Of<ItemDropped>().ShouldHaveSingleItem().Owner.ShouldBeNull();
    }

    // ---- golden fight -----------------------------------------------------------------------------------------------------------------

    private static List<string> ScriptedFight(ulong seed)
    {
        var def = TestCombat.Monster("monster.t_golden", maxHp: 80, atk: 6, def: 2, hit: 30, flee: 20, aspd: 1.5,
            drops: ["drop.gel_slime"]);
        var rig = new CombatRig(TestCombat.Open(12, 8, [TestMonsters.Spawn(def, 6, 4)]), [def], ai: true, seed: seed);
        var sheet = TestCombat.PlayerSheet(aspd: 1.2, speed: 4, maxHp: 200);
        sheet.Modifiers.Add("test", DerivedStats.Atk, ModifierOp.Override, 15);
        var player = rig.AddPlayer(new GridCell(2, 4), sheet);
        rig.Ticks();
        var monster = rig.Instance.Entities.Single(e => e.Kind == EntityKind.Monster).Id;
        rig.Send(new AttackTarget(player, monster));
        rig.TickUntil(() => rig.Get(monster).Dead || rig.Get(player).Dead, 2000, "the fight ends");
        rig.Ticks(2);
        return rig.TimedOf<DamageDealt>().Select(h => $"{h.Tick}:{(h.Event.Source == player ? "P" : "M")}:{h.Event.Result}:{h.Event.Amount}")
            .Concat(rig.Of<EntityDied>().Select(d => "died:" + (d.Entity == player ? "P" : "M")))
            .Concat(rig.Of<ItemDropped>().Select(d => $"drop:{d.ItemId}x{d.Quantity}@{d.Cell}"))
            .ToList();
    }

    [Fact]
    public void A_scripted_fight_replays_the_same_damage_sequence_and_outcome()
    {
        var first = ScriptedFight(42);
        ScriptedFight(42).ShouldBe(first);
        string.Join(" ", first).ShouldBe(GoldenFight);
    }

    // Tick:attacker:result:amount, recorded with the sample formulas and seed 42 (player 1.2 aspd, monster 1.5); a change here means combat, the AI timing or the formulas changed.
    private const string GoldenFight =
        "9:P:hit:14 11:M:hit:6 18:M:hit:6 18:P:miss:0 25:M:hit:6 26:P:hit:14 31:M:hit:6 35:P:hit:14 38:M:hit:6 43:P:hit:14 45:M:hit:6 51:M:hit:6 51:P:hit:14 58:M:hit:6 59:P:miss:0 65:M:hit:6 68:P:miss:0 72:M:hit:6 76:P:miss:0 78:M:hit:6 84:P:hit:14 died:M";
}
