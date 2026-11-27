using Game.Server.World;
using Game.Server.World.Combat;
using Game.Server.World.Party;
using Game.Server.World.Quests;
using Game.Shared.Definitions;
using Game.Shared.Party;
using Game.Shared.Vocabulary;
using Game.Shared.World;
using Game.Tests.Server;
using Game.Tests.World;

namespace Game.Tests.Party;

/// <summary>
/// T-902's EXP share: golden values of the sample <c>party_exp</c> (one, two and a full party), the even split, and kills through the real combat system with a party in range, out of
/// range, dead, on another map, past the level gap, next to a player without a party; solo EXP is unchanged; party kill credit goes through the event bus to the quests.
/// </summary>
public sealed class PartyExpTests
{
    private static readonly PartyExpRule Sample = PartyExpRule.Of(TestGrids.SampleContent.Formulas, TestGrids.SampleContent.Balance);

    // ---- the formula and the split (golden: content/formulas.yaml party_exp, by hand) ----

    [Theory]
    [InlineData(50, 1, 50)]     // one member alone: exactly its part, as without a party
    [InlineData(50, 2, 55)]     // floor(50 * 110 / 100)
    [InlineData(50, 5, 70)]     // a full party: floor(50 * 140 / 100)
    [InlineData(35, 2, 38)]     // floor(38.5)
    [InlineData(7, 2, 7)]       // floor(7.7)
    [InlineData(10, 5, 14)]
    [InlineData(0, 3, 0)]
    public void Party_exp_golden_values(long exp, int shareCount, long expected) =>
        Sample.Pool(exp, shareCount, 0, Sample.NewContext()).ShouldBe(expected);

    [Fact]
    public void The_sample_numbers_are_the_contents()
    {
        Sample.Balance.ShouldBe(new PartyBalanceDef { MaxMembers = 5, InviteTimeoutMs = 30_000, ExpRange = 12, KillCreditRange = 12, ExpLevelGap = 15 });
        Sample.Shares(15).ShouldBeTrue();
        Sample.Shares(16).ShouldBeFalse();
    }

    [Theory]
    [InlineData(55, new long[] { 28, 27 })]
    [InlineData(70, new long[] { 14, 14, 14, 14, 14 })]
    [InlineData(14, new long[] { 3, 3, 3, 3, 2 })]
    [InlineData(2, new long[] { 1, 1, 0 })]
    public void The_even_split_adds_up_and_gives_the_rest_to_the_first(long total, long[] expected)
    {
        var shares = new long[expected.Length];
        PartyExpRule.Even(total, shares);
        shares.ShouldBe(expected);
        shares.Sum().ShouldBe(total);
    }

    // ---- kills through combat ----

    private static MapGrid OtherMap() => MapGrid.Create(new MapGridFile
    {
        MapId = "map.other", Width = 8, Height = 8, Cells = Convert.ToBase64String(Enumerable.Repeat(CellFlagBits.Walkable, 64).ToArray()), Spawns = [],
    });

    private static readonly MonsterDef Mob = TestCombat.Monster("monster.t_party", maxHp: 1, def: 0, baseExp: 50, jobExp: 10);

    private sealed class Fight
    {
        public readonly PartyRoster Roster = new();
        public readonly CombatRig Rig;
        public readonly EntityId Monster;

        public Fight(MonsterDef? def = null, MapGrid[]? moreMaps = null, bool party = true)
        {
            Rig = new CombatRig(TestCombat.Open(40, 8), [def ?? Mob], moreMaps: moreMaps, parties: party ? Roster : null);
            Monster = Rig.AddMonster(def ?? Mob, new GridCell(10, 4));
            Rig.Ticks();
        }

        public EntityId Player(int x, int atk = 1000)
        {
            var id = Rig.AddPlayer(new GridCell(x, 4));
            Rig.Ticks();
            var sheet = Rig.Get(id).Stats!;
            sheet.Modifiers.Add("test", DerivedStats.Atk, ModifierOp.Override, atk);
            sheet.Modifiers.Add("test", DerivedStats.Hit, ModifierOp.Override, 1000);
            sheet.Modifiers.Add("test", DerivedStats.Crit, ModifierOp.Override, 0);
            return id;
        }

        public void Party(params EntityId[] members) => Roster.Set(new PartyView(1, members[0], members));

        /// <summary>One landed hit by <paramref name="who"/> (a miss is tried again).</summary>
        public void Hit(EntityId who)
        {
            while (true)
            {
                var before = Rig.Of<DamageDealt>().Count;
                Rig.Request(who, Monster, range: 40);
                Rig.Ticks(2);
                if (Rig.Of<DamageDealt>().Skip(before).Single().Result != HitResults.Miss) return;
            }
        }

        public Dictionary<EntityId, (long Base, long Job)> Gains() => Rig.Of<ExpGained>().ToDictionary(g => g.Player, g => (g.BaseExp, g.JobExp));
    }

    [Fact]
    public void Solo_exp_is_unchanged_with_parties_on_the_server_and_without()
    {
        foreach (var withRoster in new[] { false, true })
        {
            var fight = new Fight(party: withRoster);
            var solo = fight.Player(9);
            if (withRoster)
            {
                var a = fight.Player(20);
                var b = fight.Player(21);
                fight.Party(a, b);   // somebody else's party, nearby
            }
            fight.Hit(solo);
            fight.Gains().ShouldBe(new Dictionary<EntityId, (long, long)> { [solo] = (50, 10) }, withRoster ? "with a party nearby" : "no parties");
        }
    }

    [Fact]
    public void Two_members_in_range_share_party_exp_evenly_the_leader_first()
    {
        var fight = new Fight();
        var a = fight.Player(9);
        var b = fight.Player(10 + 12);   // exactly at exp_range
        fight.Party(a, b);
        fight.Hit(a);
        fight.Gains().ShouldBe(new Dictionary<EntityId, (long, long)> { [a] = (28, 6), [b] = (27, 5) });   // 55 and 11 (50 and 10 x 110%)
        fight.Rig.Of<ExpGained>().Single(g => g.Player == b).Damage.ShouldBe(0);
    }

    [Fact]
    public void A_full_party_shares_one_hundred_forty_percent()
    {
        var fight = new Fight();
        var members = Enumerable.Range(0, 5).Select(i => fight.Player(8 + i)).ToArray();
        fight.Party(members);
        fight.Hit(members[2]);
        var gains = fight.Gains();
        members.Select(m => gains[m]).ShouldBe([(14L, 3L), (14L, 3L), (14L, 3L), (14L, 3L), (14L, 2L)]);   // 70 and 14
    }

    [Fact]
    public void A_member_out_of_range_dead_or_on_another_map_gets_nothing_and_does_not_count()
    {
        var other = OtherMap();
        var fight = new Fight(moreMaps: [other]);
        var a = fight.Player(9);
        var far = fight.Player(10 + 13);   // one cell past exp_range
        var dead = fight.Player(11);
        var away = fight.Rig.World.Spawn(EntityKind.Player, "map.other", new GridCell(2, 2), Directions.S, e => e.Stats = TestCombat.PlayerSheet());
        fight.Rig.Ticks();
        fight.Rig.Get(dead).Dead = true;
        fight.Party(a, far, dead, away);
        fight.Hit(a);
        fight.Gains().ShouldBe(new Dictionary<EntityId, (long, long)> { [a] = (50, 10) });   // share_count 1: its own part, no bonus
    }

    [Fact]
    public void Past_the_level_gap_the_party_does_not_share_and_its_dealers_split_by_damage()
    {
        var fight = new Fight();
        var a = fight.Player(9);
        var b = fight.Player(11);
        fight.Party(a, b);
        fight.Rig.Progressions.GetOrCreate(b, () => new Game.Shared.Progression.ProgressionState()).BaseLevel = 17;   // a is level 1: 16 apart
        fight.Hit(a);
        fight.Gains().ShouldBe(new Dictionary<EntityId, (long, long)> { [a] = (50, 10) });
    }

    [Fact]
    public void A_party_and_a_player_without_one_split_by_damage_first()
    {
        var def = TestCombat.Monster("monster.t_party_big", maxHp: 100, def: 0, baseExp: 50, jobExp: 10);
        var fight = new Fight(def);
        var solo = fight.Player(9, atk: 30);
        var a = fight.Player(11, atk: 70);
        var b = fight.Player(12, atk: 1);
        fight.Party(a, b);
        fight.Hit(solo);
        fight.Hit(a);
        fight.Rig.Get(fight.Monster).Dead.ShouldBeTrue();
        // 30 / 70 of 50 and 10: the solo player 15 and 3; the party 35 and 7, then 110% for two: 38 (19 + 19) and 7 (4 + 3).
        fight.Gains().ShouldBe(new Dictionary<EntityId, (long, long)> { [solo] = (15, 3), [a] = (19, 4), [b] = (19, 3) });
        fight.Rig.Of<ExpGained>().Single(g => g.Player == a).Damage.ShouldBe(70);
    }

    // ---- kill credit through the event bus ----

    [Fact]
    public void Party_kill_credit_names_the_members_alive_on_the_map_and_in_range_who_had_none()
    {
        var other = OtherMap();
        var fight = new Fight(moreMaps: [other]);
        var a = fight.Player(9);
        var near = fight.Player(10 + 12);
        var far = fight.Player(10 + 13);
        var dead = fight.Player(11);
        var stranger = fight.Player(12);
        var away = fight.Rig.World.Spawn(EntityKind.Player, "map.other", new GridCell(2, 2), Directions.S, e => e.Stats = TestCombat.PlayerSheet());
        fight.Rig.Ticks();
        fight.Rig.Get(dead).Dead = true;
        fight.Party(a, near, far, dead, away);
        fight.Hit(a);
        fight.Rig.Ticks(2);

        fight.Rig.Of<MonsterKilled>().Single().Credited.ShouldBe([a]);   // combat's own credit is unchanged
        var shared = fight.Rig.Of<PartyKillCredit>().ShouldHaveSingleItem();
        (shared.Monster, shared.MonsterId).ShouldBe((fight.Monster, Mob.Id));
        shared.Members.ShouldBe([near]);
        shared.Members.ShouldNotContain(stranger);
    }

    [Fact]
    public void A_party_members_kill_counts_for_a_members_kill_objective_through_the_quests()
    {
        var roster = new PartyRoster();
        var rig = new QuestRig(roster);
        var hunter = rig.Join(QuestRig.Field);
        var questing = rig.Join();   // in the safe zone, 8 cells from the field: in kill_credit_range
        var bystander = rig.Join();
        foreach (var who in new[] { questing, bystander })
        {
            rig.Talk(who, "npc.guide");
            rig.Choose(who, "help with");
            rig.Choose(who, "hunt some slimes");
            rig.TickUntil(() => rig.Progress(who).ActiveQuests.ContainsKey("quest.first_hunt"), 10, "the start");
            rig.Close(who);
        }
        roster.Set(new PartyView(1, hunter, [hunter, questing]));

        rig.Boost(hunter);
        rig.Kill(hunter, "monster.gel_slime");
        rig.Ticks(3);
        rig.Progress(questing).ActiveQuests["quest.first_hunt"].Counters[0].ShouldBe(1);
        rig.Progress(bystander).ActiveQuests["quest.first_hunt"].Counters[0].ShouldBe(0);
        rig.Of<PartyKillCredit>().ShouldHaveSingleItem().Members.ShouldBe([questing]);
    }
}
