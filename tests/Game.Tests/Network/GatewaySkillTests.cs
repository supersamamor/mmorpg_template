using Game.Server.World;
using Game.Shared.Definitions;
using Game.Shared.Protocol;
using Game.Shared.Vocabulary;
using Game.Shared.World;

namespace Game.Tests.Network;

/// <summary>
/// T-502: player skills over the wire, end to end on the real stack. A character is set up through the progression store (levels and a skill point
/// or two: the way a player earns them is covered elsewhere), then learns a skill, changes job, casts at a gel slime and sees the cast bar events and the cooldown.
/// </summary>
public class GatewaySkillTests
{
    private static readonly GridCell OutsideSafeZone = new(17, 15);

    [Fact]
    public async Task A_player_learns_changes_job_casts_and_sees_the_cast_and_the_cooldown()
    {
        await using var rig = await GatewayRig.StartAsync();
        var me = await rig.JoinAsync("caster");
        var first = await me.WaitForAsync<SkillsMessage>();
        (first.Job, first.Points).ShouldBe(("job.wanderer", 0));
        first.Levels.ShouldBeEmpty();
        first.Jobs.ShouldBeEmpty();

        var id = new EntityId(me.Id);
        rig.Progressions.TryGet(id, out var state).ShouldBeTrue();

        // learn: refused without a point, then done with one
        await me.SendAsync(new LearnSkillMessage("skill.second_wind"));
        (await me.WaitForAsync<RejectedMessage>(r => r.Intent == MessageTypes.LearnSkill)).ShouldBe(
            new RejectedMessage(MessageTypes.LearnSkill, SkillReasons.NotEnoughPoints, null, Skill: "skill.second_wind"));
        state.SkillPoints = 6;
        await me.SendAsync(new LearnSkillMessage("skill.second_wind"));
        var learned = await me.WaitForAsync<SkillsMessage>(m => m.Levels.Count == 1);
        learned.Points.ShouldBe(5);
        learned.Levels.Single().ShouldBe(new SkillLevelRecord("skill.second_wind", 1));

        // job change: refused below the levels, then done
        await me.SendAsync(new ChangeJobMessage("job.spellbinder"));
        (await me.WaitForAsync<RejectedMessage>(r => r.Intent == MessageTypes.ChangeJob)).Reason.ShouldBe(SkillReasons.BaseLevelTooLow);
        state.BaseLevel = 5;
        state.JobLevel = 5;
        await me.SendAsync(new ChangeJobMessage("job.spellbinder"));
        var changed = await me.WaitForAsync<SkillsMessage>(m => m.Job == "job.spellbinder");
        changed.Points.ShouldBe(5);   // unspent points and learned skills stay
        changed.Levels.Single().Skill.ShouldBe("skill.second_wind");
        (await me.WaitForAsync<ExpMessage>(m => m.JobLevel == 1)).JobExp.ShouldBe(0);

        await me.SendAsync(new LearnSkillMessage("skill.ember_bolt"));
        await me.WaitForAsync<SkillsMessage>(m => m.Levels.Any(l => l.Skill == "skill.ember_bolt"));

        // cast it on a gel slime
        await me.SendAsync(new MoveMessage(OutsideSafeZone));
        await Poll.UntilAsync(() => me.Seen(me.Id), r => r is { Next: null } && r.Cell.Equals(OutsideSafeZone), "out of the safe zone", 20_000);
        var slime = await Poll.UntilAsync(() => me.View.FirstOrDefault(e => e is { Kind: EntityKinds.Monster, DisplayKey: "monster.gel_slime", Dead: false }),
            e => e is not null, "a gel slime in view", 20_000);
        await me.SendAsync(new UseSkillMessage("skill.ember_bolt", null, slime!.Id));
        var cooldown = await me.WaitForAsync<CooldownMessage>(m => m.Skill == "skill.ember_bolt", 20_000);
        cooldown.Ms.ShouldBe(500);

        var deltas = me.Messages.OfType<DeltaMessage>().ToList();
        deltas.SelectMany(d => d.Casts ?? []).ShouldContain(c => c.Caster == me.Id && c.Skill == "skill.ember_bolt" && c.Ms > 800 && c.Ms <= 1000);
        deltas.SelectMany(d => d.CastEnds ?? []).ShouldContain(c => c.Caster == me.Id && c.Skill == "skill.ember_bolt" && c.Result == CastResults.Done);
        await Poll.UntilAsync(() => me.Hits.Where(h => h.Attacker == me.Id && h.Target == slime.Id).ToList(), h => h.Count > 0, "the bolt hits", 10_000);
        await Poll.UntilAsync(() => me.Messages.OfType<SnapshotMessage>().Last().Vitals, v => v is not null, "vitals");
        rig.Log.Lines.Where(l => l.StartsWith("ERROR")).ShouldBeEmpty();
    }

    [Fact]
    public async Task Bad_skill_messages_are_refused_without_harm()
    {
        await using var rig = await GatewayRig.StartAsync();
        var me = await rig.JoinAsync("fuzzer");
        await me.WaitForAsync<SkillsMessage>();

        await me.SendAsync(new UseSkillMessage("skill.second_wind"));
        (await me.WaitForAsync<RejectedMessage>(r => r.Intent == MessageTypes.UseSkill)).Reason.ShouldBe(SkillReasons.NotLearned);
        await me.SendAsync(new UseSkillMessage("skill.nobody"));
        await me.WaitForAsync<RejectedMessage>(r => r is { Intent: MessageTypes.UseSkill, Reason: SkillReasons.UnknownSkill });
        await me.SendAsync(new UseSkillMessage("skill.second_wind", null, 987_654_321));
        await me.WaitForAsync<RejectedMessage>(r => r is { Intent: MessageTypes.UseSkill, Reason: SkillReasons.TargetGone, Target: 987_654_321 });
        await me.SendAsync(new ChangeJobMessage("job.nobody"));
        await me.WaitForAsync<RejectedMessage>(r => r is { Intent: MessageTypes.ChangeJob, Reason: SkillReasons.UnknownJob });

        await me.SendRawAsync("""{"type":"use_skill"}""");
        (await me.WaitForByeAsync()).ShouldBe(ByeReasons.Malformed);
        rig.Log.Lines.Where(l => l.StartsWith("ERROR")).ShouldBeEmpty();
    }
}
