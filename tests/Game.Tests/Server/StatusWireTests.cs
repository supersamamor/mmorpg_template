using System.Text;
using Game.Content.Simulation;
using Game.Server.Net;
using Game.Server.World;
using Game.Server.World.Aoi;
using Game.Server.World.Effects;
using Game.Shared.Definitions;
using Game.Shared.Effects;
using Game.Shared.Protocol;
using Game.Shared.Vocabulary;
using Game.Shared.World;
using Game.Tests.World;

namespace Game.Tests.Server;

/// <summary>Statuses on the wire (T-501, ARCHITECTURE §10), the pure stacking rule, and monster skills in the duel (ADR-002 row 118).</summary>
public class StatusWireTests
{
    private sealed class JsonSink : IViewSink
    {
        private readonly ViewMessageWriter _writer = new();
        public readonly List<string> Messages = new();

        public void OnSnapshot(ViewSnapshot snapshot) => Messages.Add(Encoding.UTF8.GetString(_writer.Write(snapshot)));

        public void OnDelta(ViewDelta delta) => Messages.Add(Encoding.UTF8.GetString(_writer.Write(delta)));
    }

    [Fact]
    public void Statuses_reach_observers_with_stacks_time_left_and_the_debuff_flag_and_nothing_else()
    {
        var dummy = TestCombat.Monster("monster.dummy", maxHp: 1000, aspd: 0.001);
        var rig = new CombatRig(TestCombat.Open(12, 8), [dummy], aoi: true, effects: TestEffects.Library.Value, viewRadius: 4);
        var sink = new JsonSink();
        var player = rig.AddPlayer(new GridCell(2, 2));
        var monster = rig.AddMonster(dummy, new GridCell(3, 2));
        rig.Ticks();
        rig.Send(new AttachView(player, sink));
        rig.Ticks();
        sink.Messages.Clear();

        rig.Instance.Events.Publish(new SkillRequested(player, "skill.t_poison", 1, monster, null, rig.Now));
        rig.Instance.Events.Publish(new SkillRequested(player, "skill.t_stack", 1, null, null, rig.Now));
        rig.Ticks(2);
        var delta = sink.Messages.Last();
        delta.ShouldContain("""{"id":""" + monster.Value + ""","statuses":[{"id":"status.t_poison","stacks":1,"left_ms":3000,"debuff":true}]}""");
        delta.ShouldContain("""{"id":""" + player.Value + ""","statuses":[{"id":"status.t_stack","stacks":1,"left_ms":5000}]}""");
        delta.ShouldNotContain("chance");
        delta.ShouldNotContain("tick_interval");

        // The client reads it back; a snapshot carries the time left at its own tick.
        var parsed = (DeltaMessage)ProtocolJson.ReadServer(Encoding.UTF8.GetBytes(delta)).Message!;
        var change = parsed.Changed.Single(c => c.Id == monster.Value);
        (change.Fields & ChangedFields.Statuses).ShouldNotBe(ChangedFields.None);
        change.Statuses!.Single().Id.ShouldBe(TestEffects.Poison);
        change.Statuses![0].Debuff.ShouldBeTrue();

        rig.Ticks(5);
        rig.Send(new RequestSnapshot(player));
        rig.Ticks();
        var snapshot = (SnapshotMessage)ProtocolJson.ReadServer(Encoding.UTF8.GetBytes(sink.Messages.Last(m => m.Contains("\"snapshot\"")))).Message!;
        snapshot.Entities.Single(e => e.Id == monster.Value).Statuses!.Single().LeftMs.ShouldBeInRange(2000, 2600);

        // When it ends, the field comes back empty.
        rig.Ticks(30);
        sink.Messages.ShouldContain(m => m.Contains("""{"id":""" + monster.Value + ""","statuses":[]}"""));
    }

    [Fact]
    public void Status_records_round_trip_through_the_protocol_mapping()
    {
        var record = new EntityRecord(7, EntityKinds.Monster, "monster.x", new GridCell(1, 2), Directions.E, null, 0, 0.5,
            Statuses: [new StatusRecord("status.poison", 2, 1500, true), new StatusRecord("status.swift", 1, 40, false)]);
        var json = Encoding.UTF8.GetString(ProtocolJson.ToBytes(new SnapshotMessage(3, "map.x", 7, [record], null)));
        json.ShouldContain("""
            "statuses":[{"id":"status.poison","stacks":2,"left_ms":1500,"debuff":true},{"id":"status.swift","stacks":1,"left_ms":40}]
            """);
        var back = ((SnapshotMessage)ProtocolJson.ReadServer(Encoding.UTF8.GetBytes(json)).Message!).Entities.Single();
        back.Statuses!.Select(s => (s.Id, s.Stacks, s.LeftMs, s.Debuff)).ShouldBe([("status.poison", 2, 1500, true), ("status.swift", 1, 40, false)]);
    }

    [Fact]
    public void The_stacking_rule_adds_stacks_refreshes_at_the_cap_and_never_shortens()
    {
        var status = new CompiledStatus(new StatusDef { Id = "status.x", MaxStacks = 2, TickIntervalMs = 500, OnTick = [new HealEffect { Amount = "1" }] });
        var set = new StatusSet();
        var a = set.Apply(status, default, 1, 0, 1000, out var c1);
        c1.ShouldBe(StatusChange.Applied);
        a.NextTickMs.ShouldBe(500);
        set.Apply(status, default, 1, 200, 3000, out var c2);
        c2.ShouldBe(StatusChange.Stacked);
        a.Stacks.ShouldBe(2);
        a.EndsAtMs.ShouldBe(3200);
        set.Apply(status, default, 1, 300, 100, out var c3);
        c3.ShouldBe(StatusChange.Refreshed);
        a.EndsAtMs.ShouldBe(3200);   // a shorter re-application never shortens it
        a.NextTickMs.ShouldBe(500);  // the tick schedule stays
        set.Revision.ShouldBe(3);
        set.Remove(a).ShouldBeTrue();
        set.IsEmpty.ShouldBeTrue();
    }

    [Fact]
    public void Knockback_paths_stop_at_unwalkable_cells_and_never_cut_a_corner()
    {
        var grid = TestCombat.Grid([
            ".....",
            "..#..",
            ".....",
            ".....",
        ]);
        KnockbackPath.Destination(grid, new GridCell(0, 1), new GridCell(1, 1), Directions.E, 3).ShouldBe(new GridCell(1, 1));   // wall right there
        KnockbackPath.Destination(grid, new GridCell(0, 3), new GridCell(1, 2), Directions.E, 3).ShouldBe(new GridCell(1, 2));   // ne would cut the wall's corner
        KnockbackPath.Destination(grid, new GridCell(0, 2), new GridCell(1, 2), Directions.E, 3).ShouldBe(new GridCell(4, 2));
        KnockbackPath.Destination(grid, new GridCell(2, 2), new GridCell(2, 2), Directions.S, 5).ShouldBe(new GridCell(2, 3));   // same cell: the caster's facing; the map edge
    }

    [Fact]
    public void The_duel_counts_the_wasps_venom_sting_and_its_poison_and_stays_deterministic()
    {
        var sim = new BalanceSimulator(TestGrids.SampleContent);
        var wasp = sim.Db.Monsters.Get("monster.sting_wasp");
        var player = sim.Character("job.wanderer", 5);
        var a = Duel.Run(sim, player, wasp, 500, 3);
        a.MeanSkillCasts.ShouldBeGreaterThan(0);
        a.MeanSkillDamage.ShouldBeGreaterThan(0);
        a.MeanStatusDamage.ShouldBeGreaterThan(0);
        Duel.Run(sim, player, wasp, 500, 3).ShouldBe(a);
        player.Sheet.Modifiers.HasSource("status:status.poison").ShouldBeFalse();   // no poison left on the shared sheet
        var slime = Duel.Run(sim, player, sim.Db.Monsters.Get("monster.gel_slime"), 100, 3);
        slime.MeanSkillCasts.ShouldBe(0);
    }
}
