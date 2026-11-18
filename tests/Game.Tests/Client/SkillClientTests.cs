using System.Text;
using Game.Client.Core;
using Game.Client.Core.Bundle;
using Game.Client.Core.Net;
using Game.Client.Core.Skills;
using Game.Client.Core.Ui;
using Game.Client.Core.World;
using Game.Shared.Protocol;
using Game.Shared.Random;
using Game.Shared.Vocabulary;
using Game.Shared.World;
using ClientSession = Game.Client.Core.ClientSession;

namespace Game.Tests.Client;

/// <summary>T-502 in the client: the skill book and its tree rows, the skill bar and its cooldowns, targeting, cast bars, and the words.</summary>
public class SkillClientTests
{
    private sealed class Socket : IClientSocket
    {
        public readonly Queue<SocketEvent> Events = new();
        public readonly List<string> Sent = new();

        public void Open(string url)
        {
        }

        public void Send(byte[] utf8) => Sent.Add(Encoding.UTF8.GetString(utf8));

        public void Close()
        {
        }

        public bool TryDequeue(out SocketEvent socketEvent) => Events.TryDequeue(out socketEvent);

        public void Push(ServerMessage message) => Events.Enqueue(new SocketEvent(SocketEventKind.Message, message));
    }

    private static readonly DisplayBundle Bundle = ClientBundleFixture.Bundle();

    private static (ClientSession Session, Socket Socket) Playing()
    {
        var socket = new Socket();
        var session = new ClientSession(new ClientConfig { AllowCleartext = true }, Bundle, socket, new Pcg32Random(1));
        session.Login("Ada", 0);
        socket.Events.Enqueue(new SocketEvent(SocketEventKind.Opened));
        socket.Push(new WelcomeMessage(1, 1, "map.starter_meadow", 10, null, "token"));
        socket.Push(new SnapshotMessage(1, "map.starter_meadow", 1,
        [
            new EntityRecord(1, EntityKinds.Player, "job.wanderer", new GridCell(5, 5), Directions.N, null, 0),
            new EntityRecord(7, EntityKinds.Monster, "monster.gel_slime", new GridCell(6, 6), Directions.S, null, 0, 1),
        ], new VitalsRecord(40, 40, 10, 10)));
        session.Update(0, 0);
        socket.Sent.Clear();
        return (session, socket);
    }

    private static SkillsMessage Wanderer(int points = 0, params (string Skill, int Level)[] levels) =>
        new("job.wanderer", points, levels.Select(l => new SkillLevelRecord(l.Skill, l.Level)).ToList(), [], []);

    // ---- the bundle ----

    [Fact]
    public void The_bundle_carries_skills_and_job_trees_for_the_panel()
    {
        var bolt = Bundle.Skill("skill.ember_bolt")!;
        (bolt.Name, bolt.Range, bolt.Ground, bolt.Friendly, bolt.Passive).ShouldBe(("Ember Bolt", 7, false, false, false));
        Bundle.Skill("skill.mana_font")!.Ground.ShouldBeTrue();
        Bundle.Skill("skill.second_wind")!.OnSelf.ShouldBeTrue();
        Bundle.Skill("skill.tough_body")!.Passive.ShouldBeTrue();
        var spellbinder = Bundle.Job("job.spellbinder")!;
        spellbinder.Parent.ShouldBe("job.wanderer");
        spellbinder.Skills.Single(s => s.SkillId == "skill.mana_font").Requires.ShouldBe([new SkillNeed("skill.ember_bolt", 2)]);
    }

    // ---- the book ----

    [Fact]
    public void Rows_show_the_current_job_and_its_parents_with_what_each_needs()
    {
        var book = new SkillBook(Bundle);
        book.Apply(new SkillsMessage("job.spellbinder", 2, [new SkillLevelRecord("skill.second_wind", 1), new SkillLevelRecord("skill.ember_bolt", 1)], [], []), 0);
        var rows = book.Rows;
        rows.Select(r => r.SkillId).ShouldBe(["skill.second_wind", "skill.return_home", "skill.ember_bolt", "skill.mana_font", "skill.cleanse", "skill.call_seedling"]);   // parents first
        rows.Single(r => r.SkillId == "skill.second_wind").State.ShouldBe(SkillState.Learnable);   // learned, and a point could raise it
        rows.Single(r => r.SkillId == "skill.second_wind").Max.ShouldBe(5);
        var font = rows.Single(r => r.SkillId == "skill.mana_font");
        font.State.ShouldBe(SkillState.Locked);
        font.Missing.ShouldBe([new SkillNeed("skill.ember_bolt", 2)]);
        rows.Single(r => r.SkillId == "skill.cleanse").State.ShouldBe(SkillState.Learnable);

        book.Apply(new SkillsMessage("job.spellbinder", 0, [new SkillLevelRecord("skill.ember_bolt", 1)], [], []), 0);
        book.Rows.Single(r => r.SkillId == "skill.cleanse").State.ShouldBe(SkillState.Locked);   // no point left
        book.Rows.Single(r => r.SkillId == "skill.ember_bolt").State.ShouldBe(SkillState.Learned);
    }

    [Fact]
    public void The_skill_bar_lists_learned_active_skills_and_never_a_passive()
    {
        var book = new SkillBook(Bundle);
        book.Apply(new SkillsMessage("job.warden", 0, [new SkillLevelRecord("skill.tough_body", 2), new SkillLevelRecord("skill.twin_strike", 1), new SkillLevelRecord("skill.second_wind", 1)], [], []), 0);
        book.ActiveSkills.ShouldBe(["skill.second_wind", "skill.twin_strike"]);
        ((IHotbarContent)book).Slot(0).DisplayKey.ShouldBe("skill.second_wind");
        ((IHotbarContent)book).Slot(2).IsEmpty.ShouldBeTrue();
    }

    [Fact]
    public void A_cooldown_sweeps_down_on_the_view_clock_and_the_version_moves_while_it_runs()
    {
        var book = new SkillBook(Bundle);
        book.Apply(Wanderer(0, ("skill.second_wind", 1)), 10);
        book.ApplyCooldown(new CooldownMessage("skill.second_wind", 4000), 10);
        book.CooldownFraction("skill.second_wind").ShouldBe(1);
        book.Update(12);
        book.CooldownLeft("skill.second_wind").ShouldBe(2, 1e-9);
        book.CooldownFraction("skill.second_wind").ShouldBe(0.5, 1e-9);
        ((IHotbarContent)book).Slot(0).Usable.ShouldBeFalse();
        var version = book.Version;
        book.Update(12.1);
        book.Version.ShouldBeGreaterThan(version);   // a running cooldown redraws every frame
        book.Update(14.5);
        book.CooldownLeft("skill.second_wind").ShouldBe(0);
        ((IHotbarContent)book).Slot(0).Usable.ShouldBeTrue();
        version = book.Version;
        book.Update(15);
        book.Version.ShouldBe(version);   // and a quiet bar costs nothing
    }

    [Fact]
    public void A_skills_message_brings_the_cooldowns_still_to_run()
    {
        var book = new SkillBook(Bundle);
        book.Apply(new SkillsMessage("job.wanderer", 0, [new SkillLevelRecord("skill.second_wind", 1)], [new SkillCooldownRecord("skill.second_wind", 3000)], []), 5);
        book.Update(6);
        book.CooldownLeft("skill.second_wind").ShouldBe(2, 1e-9);
    }

    // ---- the session ----

    [Fact]
    public void Pressing_a_self_skill_sends_it_at_once_and_a_cooldown_blocks_the_next_press()
    {
        var (session, socket) = Playing();
        socket.Push(Wanderer(0, ("skill.second_wind", 1)));
        session.Update(1, 1);
        session.Hotbar.Press(0).ShouldBe(HotbarPress.Activated);
        socket.Sent.Single().ShouldBe("""{"type":"use_skill","skill":"skill.second_wind"}""");
        socket.Push(new CooldownMessage("skill.second_wind", 7000));
        session.Update(2, 2);
        session.Hotbar.Press(0).ShouldBe(HotbarPress.Unusable);
        socket.Sent.Count.ShouldBe(1);
    }

    [Fact]
    public void A_hostile_skill_waits_for_a_tap_and_goes_out_on_the_tapped_enemy()
    {
        var (session, socket) = Playing();
        socket.Push(new SkillsMessage("job.spellbinder", 0, [new SkillLevelRecord("skill.ember_bolt", 1)], [], []));
        session.Update(1, 1);
        session.Hotbar.Press(0);
        socket.Sent.ShouldBeEmpty();
        session.Ui.PendingSkill.ShouldBe("skill.ember_bolt");
        session.Ui.Flash.ShouldContain("Ember Bolt");
        session.TapEntity(7);
        socket.Sent.Single().ShouldBe("""{"type":"use_skill","skill":"skill.ember_bolt","target":7}""");
        session.Ui.PendingSkill.ShouldBeNull();
        session.Ui.Target.ShouldBe(7);

        // with a target already chosen the next press goes straight out
        socket.Sent.Clear();
        session.Hotbar.Press(0);
        socket.Sent.Single().ShouldBe("""{"type":"use_skill","skill":"skill.ember_bolt","target":7}""");
    }

    [Fact]
    public void A_second_press_cancels_the_wait_and_a_ground_tap_is_a_walk_for_an_enemy_skill()
    {
        var (session, socket) = Playing();
        socket.Push(new SkillsMessage("job.spellbinder", 0, [new SkillLevelRecord("skill.ember_bolt", 1)], [], []));
        session.Update(1, 1);
        session.Hotbar.Press(0);
        session.Hotbar.Press(0);
        session.Ui.PendingSkill.ShouldBeNull();
        session.Hotbar.Press(0);
        session.TapGround(new GridCell(9, 9), 2);
        session.Ui.PendingSkill.ShouldBeNull();
        socket.Sent.Single().ShouldBe("""{"type":"move","cell":[9,9]}""");
    }

    [Fact]
    public void A_ground_skill_waits_for_the_ground_and_a_tap_on_an_enemy_aims_at_its_cell()
    {
        var (session, socket) = Playing();
        socket.Push(new SkillsMessage("job.spellbinder", 0, [new SkillLevelRecord("skill.mana_font", 1)], [], []));
        session.Update(1, 1);
        session.Hotbar.Press(0);
        session.TapGround(new GridCell(9, 9), 2);
        socket.Sent.Single().ShouldBe("""{"type":"use_skill","skill":"skill.mana_font","cell":[9,9]}""");
        socket.Sent.Clear();
        session.Hotbar.Press(0);
        session.TapEntity(7);
        socket.Sent.Single().ShouldBe("""{"type":"use_skill","skill":"skill.mana_font","cell":[6,6]}""");
    }

    [Fact]
    public void Learning_and_changing_job_send_their_intents_and_refusals_become_words()
    {
        var (session, socket) = Playing();
        session.LearnSkill("skill.second_wind");
        session.ChangeJob("job.spellbinder");
        socket.Sent.ShouldBe(["""{"type":"learn_skill","skill":"skill.second_wind"}""", """{"type":"change_job","job":"job.spellbinder"}"""]);
        socket.Push(new RejectedMessage(MessageTypes.LearnSkill, SkillReasons.NotEnoughPoints, null, Skill: "skill.second_wind"));
        session.Update(1, 1);
        session.Ui.Flash.ShouldBe(UiText.SkillRefused(SkillReasons.NotEnoughPoints));
        socket.Push(new RejectedMessage(MessageTypes.UseSkill, SkillReasons.OnCooldown, null, Skill: "skill.second_wind"));
        session.Update(2, 2);
        session.Ui.Flash.ShouldBe(UiText.SkillRefused(SkillReasons.OnCooldown));
        foreach (var term in SkillReasons.All) UiText.SkillRefused(term).ShouldNotBeNull();
        UiText.SkillRefused(SkillReasons.OutOfRange).ShouldNotBeEmpty();
    }

    // ---- cast bars ----

    [Fact]
    public void A_cast_bar_opens_for_any_caster_in_view_fills_with_time_and_closes_on_its_end_death_or_disappearance()
    {
        var world = new WorldView(new ClientConfig());
        world.ApplySnapshot(new SnapshotMessage(0, "map.a", 1,
        [
            new EntityRecord(1, EntityKinds.Player, "job.wanderer", new GridCell(1, 1), Directions.S, null, 0),
            new EntityRecord(2, EntityKinds.Monster, "monster.a", new GridCell(2, 1), Directions.S, null, 0, 1),
            new EntityRecord(3, EntityKinds.Monster, "monster.a", new GridCell(3, 1), Directions.S, null, 0, 1),
        ]), 0);
        world.ApplyDelta(new DeltaMessage(1, [], [], [], null, null, [new CastRecord(1, "skill.ember_bolt", 1000), new CastRecord(2, "skill.venom_sting", 300), new CastRecord(3, "skill.venom_sting", 300)]), 10);
        world.Casts.Count.ShouldBe(3);
        var own = world.Casts.Single(c => c.Caster == 1);
        own.Fraction(10).ShouldBe(0);
        own.Fraction(10.25).ShouldBe(0.25, 1e-9);
        own.Fraction(11.5).ShouldBe(1);

        var version = world.CastVersion;
        world.ApplyDelta(new DeltaMessage(2, [], [], [], null, null, null, [new CastEndRecord(1, "skill.ember_bolt", CastResults.Done)]), 11);
        world.Casts.Select(c => c.Caster).ShouldBe([2L, 3L]);
        world.CastVersion.ShouldBeGreaterThan(version);

        world.ApplyDelta(new DeltaMessage(3, [], [new ChangeRecord(2, ChangedFields.Hp | ChangedFields.Dead, default, null, null, 0, null, Hp: 0, Dead: true)], []), 12);
        world.Casts.Select(c => c.Caster).ShouldBe([3L]);
        world.ApplyDelta(new DeltaMessage(4, [], [], [3]), 13);
        world.Casts.ShouldBeEmpty();

        world.ApplyDelta(new DeltaMessage(5, [], [], [], null, null, [new CastRecord(1, "skill.ember_bolt", 500)]), 14);
        world.ApplySnapshot(new SnapshotMessage(6, "map.a", 1, [new EntityRecord(1, EntityKinds.Player, "job.wanderer", new GridCell(1, 1), Directions.S, null, 0)]), 15);
        world.Casts.ShouldBeEmpty();   // a snapshot is state, not history
    }
}
