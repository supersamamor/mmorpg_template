using Game.Server.World;
using Game.Server.World.Aoi;
using Game.Server.World.Combat;
using Game.Server.World.Monsters;
using Game.Shared.Definitions;
using Game.Shared.Stats;
using Game.Shared.Vocabulary;
using Game.Shared.World;

namespace Game.Tests.Server;

/// <summary>T-302: vitals, monster attack requests, player auto-attack, refusals.</summary>
public class CombatSystemTests
{
    private static readonly MonsterDef Slime = TestCombat.Monster("monster.t_slime", maxHp: 60, atk: 7);
    private static readonly MonsterDef Tank = TestCombat.Monster("monster.t_tank", maxHp: 100_000, atk: 1);

    private static (CombatRig Rig, EntityId Monster, EntityId Player) Pair(MonsterDef def, GridCell monsterCell, GridCell playerCell,
        MapGrid? grid = null, bool ai = false, Game.Shared.Stats.StatSheet? sheet = null, ulong seed = 1)
    {
        var rig = new CombatRig(grid ?? TestCombat.Open(16, 10), [def], ai: ai, seed: seed);
        var monster = rig.AddMonster(def, monsterCell);
        var player = rig.AddPlayer(playerCell, sheet);
        rig.Ticks();
        return (rig, monster, player);
    }

    [Fact]
    public void Vitals_start_full_and_clamp_to_a_lower_max_hp()
    {
        var (rig, monster, player) = Pair(Slime, new GridCell(2, 2), new GridCell(8, 8));
        rig.Hp(monster).ShouldBe(60);
        var stats = rig.Get(player).Stats!;
        rig.Hp(player).ShouldBe(stats.GetInt(DerivedStats.MaxHp));
        rig.Get(player).Vitals!.Sp.ShouldBe(stats.GetInt(DerivedStats.MaxSp));

        stats.Modifiers.Add("test.curse", DerivedStats.MaxHp, ModifierOp.Override, 7);
        rig.Ticks();
        rig.Hp(player).ShouldBe(7);
        stats.Modifiers.RemoveBySource("test.curse");
        rig.Ticks();
        rig.Get(player).Vitals!.MaxHp.ShouldBeGreaterThan(7);
        rig.Hp(player).ShouldBe(7);   // a higher max leaves current HP alone
    }

    [Fact]
    public void A_monster_attack_request_hits_and_hp_drops_by_exactly_the_damage()
    {
        var (rig, monster, player) = Pair(Slime, new GridCell(2, 2), new GridCell(3, 2));
        var before = rig.Hp(player);
        rig.Request(monster, player);
        rig.Ticks(2);   // dispatched at the end of the first tick, resolved in the second

        var hit = rig.Of<DamageDealt>().ShouldHaveSingleItem();
        hit.Source.ShouldBe(monster);
        hit.Target.ShouldBe(player);
        HitResults.All.Contains(hit.Result).ShouldBeTrue();
        rig.Hp(player).ShouldBe(before - hit.Amount);
        hit.HpLeft.ShouldBe(rig.Hp(player));
        if (hit.Result != HitResults.Miss) hit.Amount.ShouldBeGreaterThan(0);
        rig.Combat.Resolved.ShouldBe(1);
    }

    [Fact]
    public void A_miss_deals_zero()
    {
        // The player's flee is far above the monster's hit: the hit chance clamps to its 5% floor, so almost every request misses.
        var (rig, monster, player) = Pair(Slime, new GridCell(2, 2), new GridCell(3, 2), sheet: TestCombat.PlayerSheet(flee: 5000));
        for (var i = 0; i < 40; i++)
        {
            rig.Request(monster, player);
            rig.Ticks();
        }
        rig.Ticks();
        var hits = rig.Of<DamageDealt>();
        hits.Count.ShouldBe(40);
        var misses = hits.Where(h => h.Result == HitResults.Miss).ToList();
        misses.Count.ShouldBeGreaterThan(20);
        misses.ShouldAllBe(h => h.Amount == 0);
        rig.Hp(player).ShouldBe(rig.Get(player).Vitals!.MaxHp - hits.Sum(h => h.Amount));
    }

    [Theory]
    [InlineData("out_of_range")]
    [InlineData("attacker_dead")]
    [InlineData("target_dead")]
    [InlineData("target_gone")]
    [InlineData("safe_zone")]
    public void Monster_requests_that_no_longer_hold_are_refused_with_a_reason(string reason)
    {
        var safe = reason == AttackReasons.SafeZone ? new[] { new GridRect(3, 2, 2, 2) } : null;
        var (rig, monster, player) = Pair(Slime, new GridCell(2, 2), new GridCell(3, 2), TestCombat.Open(16, 10, safe: safe));
        switch (reason)
        {
            case AttackReasons.OutOfRange: rig.Get(player).Cell = new GridCell(6, 2); break;
            case AttackReasons.AttackerDead: rig.Get(monster).Dead = true; break;
            case AttackReasons.TargetDead: rig.Get(player).Dead = true; break;
        }
        rig.Request(monster, player);
        if (reason == AttackReasons.TargetGone) rig.World.Despawn(player);
        rig.Ticks(2);

        rig.Of<DamageDealt>().ShouldBeEmpty();
        var refused = rig.Of<AttackRefused>().ShouldHaveSingleItem();
        refused.ShouldBe(new AttackRefused(monster, player, reason));
        rig.Combat.RefusedCount(reason).ShouldBe(1);
    }

    [Fact]
    public void A_player_walks_into_range_and_attacks_at_its_aspd()
    {
        var (rig, monster, player) = Pair(Tank, new GridCell(10, 5), new GridCell(1, 5), sheet: TestCombat.PlayerSheet(aspd: 2));
        rig.Send(new AttackTarget(player, monster));
        rig.TickUntil(() => rig.Of<DamageDealt>().Any(), what: "first hit");
        GridDistance.Chebyshev(rig.Get(player).Cell, rig.Get(monster).Cell).ShouldBeLessThanOrEqualTo(1);
        rig.Of<MovementStarted>().ShouldContain(m => m.Entity == player);
        rig.Combat.TargetOf(player).ShouldBe(monster);

        var first = rig.TimedOf<DamageDealt>()[0].Tick;
        rig.Ticks(100);   // 10 s at 2 attacks per second
        var hits = rig.TimedOf<DamageDealt>().Where(h => h.Event.Source == player).ToList();
        hits.Count.ShouldBe(1 + 20);
        hits.Zip(hits.Skip(1)).ShouldAllBe(p => p.Second.Tick - p.First.Tick == 5);   // 500 ms apart
        hits[0].Tick.ShouldBe(first);
        rig.Of<AttackStopped>().ShouldBeEmpty();
    }

    [Fact]
    public void A_player_attack_stops_when_the_target_dies()
    {
        var weak = TestCombat.Monster("monster.t_weak", maxHp: 5);
        var (rig, monster, player) = Pair(weak, new GridCell(4, 4), new GridCell(3, 4));
        rig.Send(new AttackTarget(player, monster));
        rig.TickUntil(() => rig.Of<EntityDied>().Any(), what: "kill");
        rig.Get(monster).Dead.ShouldBeTrue();
        rig.Hp(monster).ShouldBe(0);
        var died = rig.Of<EntityDied>().ShouldHaveSingleItem();
        died.ShouldBe(new EntityDied(monster, player, EntityKind.Monster, weak.Id, new GridCell(4, 4)));
        var hits = rig.Of<DamageDealt>().Count;
        rig.Ticks(20);
        rig.Of<DamageDealt>().Count.ShouldBe(hits);
        rig.Of<AttackStopped>().ShouldHaveSingleItem().ShouldBe(new AttackStopped(player, monster, AttackReasons.TargetDead));
        rig.Combat.AttackingCount.ShouldBe(0);
    }

    [Fact]
    public void StopAttack_ends_the_auto_attack()
    {
        var (rig, monster, player) = Pair(Tank, new GridCell(4, 4), new GridCell(3, 4));
        rig.Send(new AttackTarget(player, monster));
        rig.Ticks(12);
        rig.Send(new StopAttack(player));
        rig.Ticks();
        var hits = rig.Of<DamageDealt>().Count;
        hits.ShouldBeGreaterThan(1);
        rig.Ticks(20);
        rig.Of<DamageDealt>().Count.ShouldBe(hits);
        rig.Of<AttackStopped>().ShouldHaveSingleItem().Reason.ShouldBe(AttackReasons.Cancelled);
    }

    [Fact]
    public void A_new_move_order_ends_the_auto_attack()
    {
        var (rig, monster, player) = Pair(Tank, new GridCell(12, 5), new GridCell(1, 5));
        rig.Send(new AttackTarget(player, monster));
        rig.Ticks(4);   // walking toward it
        rig.Send(new MoveTo(player, new GridCell(1, 9)));
        rig.Ticks(3);
        rig.Of<AttackStopped>().ShouldHaveSingleItem().Reason.ShouldBe(AttackReasons.Cancelled);
        rig.TickUntil(() => rig.Get(player).Cell.Equals(new GridCell(1, 9)), what: "the player's own walk");
        rig.Of<DamageDealt>().ShouldBeEmpty();
    }

    [Fact]
    public void A_stop_order_while_walking_in_ends_the_auto_attack()
    {
        var (rig, monster, player) = Pair(Tank, new GridCell(12, 5), new GridCell(1, 5));
        rig.Send(new AttackTarget(player, monster));
        rig.Ticks(4);
        rig.Send(new StopMove(player));
        rig.Ticks(3);
        rig.Of<AttackStopped>().ShouldHaveSingleItem().Reason.ShouldBe(AttackReasons.Cancelled);
        rig.Ticks(30);
        rig.Of<DamageDealt>().ShouldBeEmpty();
    }

    [Fact]
    public void Players_cannot_target_players_or_start_inside_a_safe_zone()
    {
        var grid = TestCombat.Open(16, 10, safe: [new GridRect(0, 0, 3, 3)]);
        var rig = new CombatRig(grid, [Tank]);
        var monster = rig.AddMonster(Tank, new GridCell(3, 1));
        var inSafe = rig.AddPlayer(new GridCell(2, 1));
        var other = rig.AddPlayer(new GridCell(8, 8));
        rig.Ticks();

        rig.Send(new AttackTarget(other, inSafe));
        rig.Send(new AttackTarget(inSafe, monster));
        rig.Send(new AttackTarget(other, other));
        rig.Ticks(20);

        rig.Of<AttackRefused>().ShouldBe([
            new AttackRefused(other, inSafe, AttackReasons.InvalidTarget),
            new AttackRefused(inSafe, monster, AttackReasons.SafeZone),
            new AttackRefused(other, other, AttackReasons.InvalidTarget),
        ]);
        rig.Of<DamageDealt>().ShouldBeEmpty();
        rig.Combat.AttackingCount.ShouldBe(0);
    }

    [Fact]
    public void A_target_behind_walls_is_unreachable()
    {
        string[] rows =
        [
            "..........",
            "......###.",
            "......#.#.",
            "......###.",
            "..........",
        ];
        var (rig, monster, player) = Pair(Tank, new GridCell(7, 2), new GridCell(1, 2), TestCombat.Grid(rows));
        rig.Send(new AttackTarget(player, monster));
        rig.TickUntil(() => rig.Of<AttackStopped>().Any(), 50, "gives up");
        rig.Of<AttackStopped>().Single().Reason.ShouldBe(AttackReasons.Unreachable);
        rig.Of<DamageDealt>().ShouldBeEmpty();
    }

    [Fact]
    public void A_dead_entitys_requests_are_dropped_and_it_receives_no_hits()
    {
        var (rig, monster, player) = Pair(Tank, new GridCell(4, 4), new GridCell(3, 4));
        rig.Get(player).Vitals!.Hp = 1;
        rig.Get(player).Stats!.Modifiers.Add("test", DerivedStats.Def, ModifierOp.Override, 0);
        while (!rig.Get(player).Dead)
        {
            rig.Request(monster, player);
            rig.Ticks();
        }
        rig.Ticks(2);
        var hits = rig.Of<DamageDealt>().Count;
        var refused = rig.Combat.RefusedCount(AttackReasons.TargetDead);
        rig.Request(monster, player);
        rig.Send(new AttackTarget(player, monster));   // dropped by the router: the player is dead
        rig.Ticks(3);
        rig.Of<DamageDealt>().Count.ShouldBe(hits);
        rig.Combat.RefusedCount(AttackReasons.TargetDead).ShouldBe(refused + 1);
        rig.Instance.Counters.IntentsDroppedDeadEntity.ShouldBe(1);
    }

    [Fact]
    public void Monsters_fight_back_end_to_end_with_the_ai()
    {
        var biter = TestCombat.Monster("monster.t_biter", maxHp: 400, atk: 3, aspd: 1);
        var rig = new CombatRig(TestCombat.Open(16, 10, [TestMonsters.Spawn(biter, 6, 5)]), [biter], ai: true);
        var player = rig.AddPlayer(new GridCell(1, 5));
        rig.Ticks();
        var monster = rig.Instance.Entities.Single(e => e.Kind == EntityKind.Monster).Id;
        rig.Send(new AttackTarget(player, monster));
        rig.TickUntil(() => rig.Of<DamageDealt>().Any(d => d.Source == monster), 200, "the monster hits back");
        rig.Of<AggroGained>().ShouldContain(a => a.Monster == monster && a.Target == player && a.Reason == AggroReasons.Attacked);
        rig.Hp(player).ShouldBeLessThan(rig.Get(player).Vitals!.MaxHp);
    }

    // ---- line of sight -------------------------------------------------------------------------------------------------------------

    private static readonly string[] Wall =
    [
        "..........",
        "....#.....",
        "....#.....",
        "....#.....",
        "..........",
    ];

    [Fact]
    public void A_ranged_monster_request_without_line_of_sight_is_refused()
    {
        var archer = TestCombat.Monster("monster.t_archer", attackRange: 3);
        var (rig, monster, player) = Pair(archer, new GridCell(6, 2), new GridCell(3, 2), TestCombat.Grid(Wall));
        rig.Request(monster, player, range: 3);   // the wall at x = 4 is between them
        rig.Ticks(2);
        rig.Of<DamageDealt>().ShouldBeEmpty();
        rig.Of<AttackRefused>().ShouldHaveSingleItem().ShouldBe(new AttackRefused(monster, player, AttackReasons.NoLineOfSight));
        rig.Combat.RefusedCount(AttackReasons.NoLineOfSight).ShouldBe(1);

        rig.Get(player).Cell = new GridCell(4, 4);   // below the end of the wall: in sight
        LineOfSight.HasLineOfSight(rig.Instance.Grid, new GridCell(6, 2), new GridCell(4, 4)).ShouldBeTrue();
        rig.Request(monster, player, range: 3);
        rig.Ticks(2);
        rig.Of<DamageDealt>().ShouldHaveSingleItem().Source.ShouldBe(monster);
    }

    [Fact]
    public void Melee_skips_the_line_of_sight_check()
    {
        // Diagonal neighbours with both corner cells blocking: no line of sight, but range 1 does not ask.
        string[] rows =
        [
            ".#..",
            "#...",
            "....",
        ];
        var (rig, monster, player) = Pair(Slime, new GridCell(0, 0), new GridCell(1, 1), TestCombat.Grid(rows));
        LineOfSight.HasLineOfSight(rig.Instance.Grid, new GridCell(0, 0), new GridCell(1, 1)).ShouldBeFalse();
        rig.Request(monster, player);
        rig.Ticks(2);
        rig.Of<AttackRefused>().ShouldBeEmpty();
        rig.Of<DamageDealt>().ShouldHaveSingleItem();
    }

    [Fact]
    public void A_ranged_player_without_line_of_sight_walks_closer_until_it_has_it()
    {
        var rig = new CombatRig(TestCombat.Grid(Wall), [Tank], TestCombat.Balance(attackRange: 3));
        var monster = rig.AddMonster(Tank, new GridCell(6, 2));
        var player = rig.AddPlayer(new GridCell(3, 2));   // in range (3) but the wall hides the target
        rig.Ticks();
        rig.Send(new AttackTarget(player, monster));
        rig.TickUntil(() => rig.Of<DamageDealt>().Any(), 100, "first hit");

        var from = rig.Get(player).Cell;
        from.ShouldNotBe(new GridCell(3, 2));
        GridDistance.Chebyshev(from, new GridCell(6, 2)).ShouldBeLessThanOrEqualTo(3);
        LineOfSight.HasLineOfSight(rig.Instance.Grid, from, new GridCell(6, 2)).ShouldBeTrue();
        rig.Of<MovementStarted>().ShouldContain(m => m.Entity == player);
        rig.Of<AttackStopped>().ShouldBeEmpty();
        rig.Of<AttackRefused>().ShouldBeEmpty();
    }

    // ---- disconnect grace period ---------------------------------------------------------------------------------------------------

    [Fact]
    public void A_character_in_its_grace_period_can_still_be_hit_and_killed()
    {
        var biter = TestCombat.Monster("monster.t_biter", maxHp: 10_000, atk: 40, aspd: 2, aggressive: true, aggroRange: 6);
        var rig = new CombatRig(TestCombat.Open(16, 10, [TestMonsters.Spawn(biter, 8, 5)]), [biter], ai: true, aoi: true);
        var sink = new RecordingSink();
        var player = rig.AddPlayer(new GridCell(4, 5));
        rig.Ticks();
        rig.Send(new AttachView(player, sink));
        rig.Ticks();
        rig.Get(player).View.ShouldBe(sink);

        // The session drops: the gateway starts the grace period exactly like this (Gateway.StartGrace): stop, detach the view.
        rig.Send(new StopMove(player));
        rig.Send(new AttachView(player, null));
        rig.Ticks();
        rig.Get(player).View.ShouldBeNull();

        rig.TickUntil(() => rig.Get(player).Dead, 300, "the monster kills the disconnected character");
        rig.Of<DamageDealt>().Where(d => d.Target == player).Sum(d => d.Amount).ShouldBeGreaterThan(0);
        rig.Of<EntityDied>().ShouldContain(d => d.Entity == player);
        rig.Ticks(30);
        rig.Get(player).Dead.ShouldBeTrue();   // stays dead; a resumed session sees that and sends Respawn
        rig.Send(new Respawn(player));
        rig.Ticks();
        rig.Get(player).Dead.ShouldBeFalse();
    }
}
