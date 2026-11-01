using Game.Server.World;
using Game.Server.World.Monsters;
using Game.Shared.Vocabulary;
using Game.Shared.World;

namespace Game.Tests.Server;

/// <summary>T-301: the monster AI state machine (idle, wander, chase, attack, return) and dormancy.</summary>
public class MonsterAiTests
{
    /// <summary>Monsters that never wander on their own, so positions only change when the test makes them.</summary>
    private static readonly Game.Shared.Definitions.AiBalanceDef Still = TestMonsters.Ai with { WanderPauseMinMs = 10_000_000, WanderPauseMaxMs = 10_000_000 };

    private static readonly GridCell Home = new(5, 5);

    private static MonsterRig Rig(Game.Shared.Definitions.MonsterDef def, Game.Shared.Definitions.AiBalanceDef? ai = null, int width = 24, int height = 12,
        int viewRadius = 4, string[]? rows = null)
    {
        var spawn = TestMonsters.Spawn(def, Home.X, Home.Y);
        var grid = rows is null ? TestMonsters.Open(width, height, spawn) : TestMonsters.Grid(rows, spawn);
        var rig = new MonsterRig(grid, [def], ai ?? Still, viewRadius: viewRadius);
        rig.Ticks();   // tick 0 spawns
        return rig;
    }

    [Fact]
    public void Wandering_stays_within_wander_radius_of_home()
    {
        var rig = Rig(TestMonsters.Passive, TestMonsters.Ai);
        rig.AddPlayer(new GridCell(9, 8));   // keeps the monster awake, passive so no aggro
        var monster = rig.Monster;
        for (var i = 0; i < 1200; i++)
        {
            rig.Ticks();
            GridDistance.Chebyshev(monster.Cell, Home).ShouldBeLessThanOrEqualTo(TestMonsters.Ai.WanderRadius);
        }
        var walks = rig.Of<MovementStarted>().Count(e => e.Entity == monster.Id);
        walks.ShouldBeGreaterThanOrEqualTo(20);   // 120 s, a stroll every 1-2 s plus the walk
        rig.Of<AggroGained>().ShouldBeEmpty();
    }

    [Fact]
    public void Aggressive_monster_aggroes_a_player_in_range_with_line_of_sight()
    {
        var rig = Rig(TestMonsters.Aggressive);
        var player = rig.AddPlayer(new GridCell(9, 5));   // 4 cells, open ground
        rig.TickUntil(() => rig.Of<AggroGained>().Count > 0, 10, "aggro");
        rig.Of<AggroGained>().Single().ShouldBe(new AggroGained(rig.Monster.Id, player, AggroReasons.Sighted));
        rig.View(rig.Monster.Id).Target.ShouldBe(player);
        rig.State(rig.Monster.Id).ShouldBe(AiStates.Chase);
    }

    [Fact]
    public void Aggressive_monster_ignores_a_player_in_range_behind_a_wall()
    {
        string[] rows =
        [
            ".......#........",
            ".......#........",
            ".......#........",
            ".......#........",
            ".......#........",
            ".......#........",
            ".......#........",
            "................",
        ];
        var rig = Rig(TestMonsters.Aggressive, rows: rows);
        rig.AddPlayer(new GridCell(9, 5));   // 4 cells away, the wall column at x = 7 between them
        rig.Ticks(50);
        rig.Of<AggroGained>().ShouldBeEmpty();
        rig.Ai.Scans.ShouldBeGreaterThan(40);   // it did look
        rig.State(rig.Monster.Id).ShouldBe(AiStates.Idle);
    }

    [Fact]
    public void Aggressive_monster_ignores_a_player_in_sight_beyond_aggro_range()
    {
        var rig = Rig(TestMonsters.Aggressive);
        rig.AddPlayer(new GridCell(11, 5));   // 6 cells, range is 5
        rig.Ticks(50);
        rig.Of<AggroGained>().ShouldBeEmpty();
        rig.Ai.AwakeCount.ShouldBe(1);
    }

    [Fact]
    public void Passive_monster_reacts_only_after_being_damaged()
    {
        var rig = Rig(TestMonsters.Passive);
        var player = rig.AddPlayer(new GridCell(6, 5));   // adjacent
        rig.Ticks(50);
        rig.Of<AggroGained>().ShouldBeEmpty();
        rig.Of<AttackRequested>().ShouldBeEmpty();

        var monster = rig.Monster.Id;
        rig.Instance.Events.Publish(new DamageDealt(player, monster, 3));
        rig.Ticks(2);   // dispatched at the end of the first tick, acted on in the second
        rig.Of<AggroGained>().Single().ShouldBe(new AggroGained(monster, player, AggroReasons.Attacked));
        rig.State(monster).ShouldBe(AiStates.Attack);
        rig.Of<AttackRequested>().Single().ShouldBe(new AttackRequested(monster, player, Home, new GridCell(6, 5), 1, rig.Instance.TickNumber * 100 - 100));
    }

    [Fact]
    public void Chase_repaths_no_more_often_than_the_interval_and_stops_at_attack_range()
    {
        var def = TestMonsters.Def("monster.chaser", aggressive: true, aggroRange: 5, chaseRange: 30, speed: 2);
        var rig = Rig(def, width: 32, viewRadius: 10);
        var monster = rig.Monster;
        var player = rig.AddPlayer(new GridCell(9, 5));
        rig.TickUntil(() => rig.State(monster.Id) == AiStates.Chase, 10, "chase");
        rig.Send(new MoveTo(player, new GridCell(20, 7)));   // faster than the monster

        rig.TickUntil(() => rig.State(monster.Id) == AiStates.Attack, 400, "attack");
        rig.Ticks(3);   // its StopMove applies on the next tick

        var starts = rig.TimedOf<MovementStarted>().Where(e => e.Event.Entity == monster.Id).ToList();
        starts.Count.ShouldBeGreaterThan(2);   // it re-planned while the player walked
        starts.Select(s => s.Event.Goal).Distinct().Count().ShouldBeGreaterThan(1);
        for (var i = 1; i < starts.Count; i++)
            (starts[i].Tick - starts[i - 1].Tick).ShouldBeGreaterThanOrEqualTo(Still.RepathIntervalMs / 100);
        GridDistance.Chebyshev(monster.Cell, rig.Get(player).Cell).ShouldBeLessThanOrEqualTo(def.AttackRange);
        rig.Of<MovementStopped>().Last(e => e.Entity == monster.Id).Reason.ShouldBeOneOf(MovementReasons.Cancelled, MovementReasons.Arrived);
        var tail = rig.Instance.TickNumber;
        rig.Ticks(10);
        rig.TimedOf<MovementStarted>().Count(e => e.Event.Entity == monster.Id && e.Tick >= tail).ShouldBe(0);   // stands and attacks
        rig.State(monster.Id).ShouldBe(AiStates.Attack);
    }

    [Theory]
    [InlineData(2.0, 20, 20)]     // 500 ms: exactly 20 in 10 s
    [InlineData(0.75, 7, 8)]      // 1333 ms: 7.5 in 10 s
    [InlineData(3.0, 29, 31)]     // 333 ms between 100 ms ticks: 30 on average
    public void Attack_requests_follow_aspd(double aspd, int min, int max)
    {
        var def = TestMonsters.Def("monster.biter", aggressive: true, aggroRange: 5, aspd: aspd);
        var rig = Rig(def);
        var player = rig.AddPlayer(new GridCell(6, 6));   // diagonal neighbour: in range 1
        rig.TickUntil(() => rig.Of<AttackRequested>().Count > 0, 10, "first attack");
        var first = rig.Instance.TickNumber;
        rig.Ticks(100);   // 10 s
        var attacks = rig.TimedOf<AttackRequested>().Where(a => a.Tick >= first - 1 && a.Tick < first + 99).ToList();   // first - 1: TickNumber is one past the tick that ran
        attacks.Count.ShouldBeInRange(min, max);
        attacks.ShouldAllBe(a => a.Event.Attacker == rig.Monster.Id && a.Event.Target == player && a.Event.Range == 1);
        rig.Monster.Facing.ShouldBe(Directions.Se);
    }

    [Fact]
    public void Monster_without_aspd_never_requests_an_attack()
    {
        var def = TestMonsters.Def("monster.harmless", aggressive: true, aggroRange: 5, aspd: 0);
        var rig = Rig(def);
        rig.AddPlayer(new GridCell(6, 5));
        rig.Ticks(50);
        rig.State(rig.Monster.Id).ShouldBe(AiStates.Attack);
        rig.Of<AttackRequested>().ShouldBeEmpty();
    }

    [Fact]
    public void Leash_beyond_chase_range_from_home_returns_ignoring_players()
    {
        var def = TestMonsters.Def("monster.leashed", aggressive: true, aggroRange: 5, chaseRange: 6, speed: 4);
        var rig = Rig(def, width: 32, viewRadius: 10);
        var monster = rig.Monster;
        var player = rig.AddPlayer(new GridCell(8, 5));
        rig.TickUntil(() => rig.State(monster.Id) == AiStates.Chase, 10, "chase");
        rig.Send(new MoveTo(player, new GridCell(28, 5)));

        rig.TickUntil(() => rig.Of<AggroLost>().Count > 0, 300, "leash");
        rig.Of<AggroLost>().Single().ShouldBe(new AggroLost(monster.Id, player, AggroReasons.Leash));
        rig.State(monster.Id).ShouldBe(AiStates.Return);
        GridDistance.Chebyshev(monster.Cell, Home).ShouldBe(def.Ai.ChaseRange + 1);
        var lost = rig.Instance.TickNumber;

        rig.Send(new MoveTo(player, new GridCell(7, 6)));   // the player follows it home, always within aggro range on the way
        rig.TickUntil(() => rig.State(monster.Id) == AiStates.Idle, 300, "home");
        monster.Cell.ShouldBe(Home);
        rig.TimedOf<AggroGained>().Count(e => e.Tick > lost).ShouldBe(0);
        rig.Instance.Events.Publish(new DamageDealt(player, monster.Id, 1));   // at home it listens again
        rig.TickUntil(() => rig.TimedOf<AggroGained>().Any(e => e.Tick > lost), 10, "re-aggro");
    }

    [Fact]
    public void Hits_while_returning_are_ignored()
    {
        var def = TestMonsters.Def("monster.leashed", aggressive: false, chaseRange: 2, speed: 2);
        var rig = Rig(def, width: 32, viewRadius: 10);
        var monster = rig.Monster;
        var player = rig.AddPlayer(new GridCell(7, 5));
        rig.Instance.Events.Publish(new DamageDealt(player, monster.Id, 1));
        rig.Ticks(2);
        rig.State(monster.Id).ShouldBe(AiStates.Chase);
        rig.Send(new MoveTo(player, new GridCell(20, 5)));
        rig.TickUntil(() => rig.State(monster.Id) == AiStates.Return, 100, "leash");
        rig.Instance.Events.Publish(new DamageDealt(player, monster.Id, 1));
        rig.Ticks(2);
        rig.State(monster.Id).ShouldBe(AiStates.Return);
        rig.Of<AggroGained>().Count.ShouldBe(1);
    }

    [Fact]
    public void Target_despawned_makes_it_give_up_and_go_home()
    {
        var rig = Rig(TestMonsters.Aggressive);
        var monster = rig.Monster;
        var player = rig.AddPlayer(new GridCell(8, 5));
        rig.TickUntil(() => rig.State(monster.Id) == AiStates.Attack, 50, "attack");
        rig.World.Despawn(player).ShouldBeTrue();
        rig.Ticks(2);   // removed in the first, noticed in the second
        rig.Of<AggroLost>().Single().ShouldBe(new AggroLost(monster.Id, player, AggroReasons.TargetGone));
        rig.TickUntil(() => rig.State(monster.Id) == AiStates.Idle, 100, "home");
        monster.Cell.ShouldBe(Home);
    }

    [Fact]
    public void Dead_target_makes_it_give_up()
    {
        var rig = Rig(TestMonsters.Aggressive);
        var monster = rig.Monster;
        var player = rig.AddPlayer(new GridCell(6, 5));
        rig.TickUntil(() => rig.State(monster.Id) == AiStates.Attack, 20, "attack");
        rig.Get(player).Dead = true;
        rig.Ticks();
        rig.Of<AggroLost>().Single().Reason.ShouldBe(AggroReasons.TargetGone);
    }

    [Fact]
    public void Unreachable_target_is_dropped()
    {
        string[] rows =
        [
            "..........#.....",
            "..........#.....",
            "..........#.....",
            "..........#.....",
            "..........#.....",
            "..........#.....",
            "..........#.....",
            "..........#.....",
        ];
        var def = TestMonsters.Def("monster.ranged_out", chaseRange: 20);
        var rig = Rig(def, rows: rows);
        var monster = rig.Monster;
        var player = rig.AddPlayer(new GridCell(12, 5));   // across a wall that splits the map
        rig.Instance.Events.Publish(new DamageDealt(player, monster.Id, 1));
        rig.TickUntil(() => rig.Of<AggroLost>().Count > 0, 20, "give up");
        rig.Of<AggroLost>().Single().Reason.ShouldBe(AggroReasons.Unreachable);
        rig.Of<MoveRejected>().ShouldContain(r => r.Entity == monster.Id && r.Reason == MovementReasons.NoPath);
    }

    [Fact]
    public void Return_that_takes_too_long_puts_the_monster_home()
    {
        var def = TestMonsters.Def("monster.slowpoke", chaseRange: 1, speed: 0.5);
        var ai = Still with { ReturnTimeoutMs = 500 };
        var rig = Rig(def, ai);
        var monster = rig.Monster;
        var player = rig.AddPlayer(new GridCell(9, 5));
        rig.Instance.Events.Publish(new DamageDealt(player, monster.Id, 1));
        rig.TickUntil(() => rig.State(monster.Id) == AiStates.Return, 200, "leash");
        rig.TickUntil(() => rig.State(monster.Id) == AiStates.Idle, 20, "warped home");
        monster.Cell.ShouldBe(Home);
        rig.Of<WarpRejected>().ShouldBeEmpty();
        rig.Ai.Count.ShouldBe(1);       // a same-map warp keeps its AI
        rig.Spawner.LiveCount.ShouldBe(1);
    }

    [Fact]
    public void Monsters_far_from_every_player_are_dormant_until_one_approaches()
    {
        var rig = Rig(TestMonsters.Aggressive, TestMonsters.Ai, width: 48, viewRadius: 1);   // buckets of 4: awake within the 3 x 3 buckets around a player
        rig.Ticks(200);
        rig.Ai.AwakeCount.ShouldBe(0);
        rig.Ai.DormantCount.ShouldBe(1);
        rig.Ai.Scans.ShouldBe(0);
        rig.Of<MovementStarted>().ShouldBeEmpty();   // no wandering either

        var player = rig.AddPlayer(new GridCell(40, 5));
        rig.Ticks(50);
        rig.Ai.AwakeCount.ShouldBe(0);
        rig.Of<MovementStarted>().Where(e => e.Entity != player).ShouldBeEmpty();

        rig.Send(new MoveTo(player, new GridCell(9, 5)));
        rig.TickUntil(() => rig.Ai.AwakeCount == 1, 100, "wake");
        rig.Get(player).Cell.X.ShouldBeLessThan(12);   // its bucket window reaches the monster's bucket
        rig.TickUntil(() => rig.Of<AggroGained>().Count > 0, 50, "aggro once awake");
    }
}
