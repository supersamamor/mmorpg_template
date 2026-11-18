using Game.Client.Core;
using Game.Client.Core.Combat;
using Game.Client.Core.World;
using Game.Shared.Protocol;
using Game.Shared.Vocabulary;
using Game.Shared.World;

namespace Game.Tests.Client;

/// <summary>T-501: statuses in the client's view model, status damage without a swing, and the HUD's words.</summary>
public class StatusPresentationTests
{
    private static EntityRecord Standing(long id, string kind = EntityKinds.Monster, IReadOnlyList<StatusRecord>? statuses = null) =>
        new(id, kind, "monster.a", new GridCell((int)id, 1), Directions.S, null, 0, 1, Statuses: statuses);

    [Fact]
    public void Statuses_count_down_on_the_view_clock_and_new_ones_are_announced_once()
    {
        var world = new WorldView(new ClientConfig());
        world.ApplySnapshot(new SnapshotMessage(0, "map.a", 1, [Standing(1, EntityKinds.Player), Standing(2, statuses: [new StatusRecord("status.b", 1, 4000, false)])]), 10);
        var monster = world.Find(2)!;
        monster.Statuses.Single().SecondsLeft(10).ShouldBe(4);
        monster.Statuses.Single().SecondsLeft(12.5).ShouldBe(2);
        monster.StartedStatuses.ShouldBeEmpty();   // a snapshot is state, not news
        monster.HasDebuff.ShouldBeFalse();

        var version = monster.StatusVersion;
        world.ApplyDelta(new DeltaMessage(1, [], [new ChangeRecord(2, ChangedFields.Statuses, default, null, null, 0, null,
            Statuses: [new StatusRecord("status.b", 2, 3000, false), new StatusRecord("status.p", 1, 8000, true)])], []), 11);
        monster.StatusVersion.ShouldNotBe(version);
        monster.StartedStatuses.ShouldBe(["status.p"]);
        monster.HasDebuff.ShouldBeTrue();
        monster.Statuses[0].Stacks.ShouldBe(2);

        world.ApplyDelta(new DeltaMessage(2, [], [new ChangeRecord(2, ChangedFields.Statuses, default, null, null, 0, null, Statuses: [])], []), 12);
        monster.Statuses.ShouldBeEmpty();
        monster.HasDebuff.ShouldBeFalse();
    }

    [Fact]
    public void Status_damage_shows_its_number_without_a_swing_or_a_flinch()
    {
        var world = new WorldView(new ClientConfig());
        world.ApplySnapshot(new SnapshotMessage(0, "map.a", 1, [Standing(1, EntityKinds.Player), Standing(2)]), 0);
        var seen = new List<HitImpact>();
        world.Combat.Impact += (in HitImpact impact) => seen.Add(impact);
        world.ApplyHits([new HitRecord(2, 1, 3, HitResults.Status)], now: 0, tick: 1);
        world.Advance(0.21);   // just after the render delay (later than stale_hit_seconds would drop it)
        seen.Single().Kind.ShouldBe(HitKind.Status);
        seen[0].Amount.ShouldBe(3);
        world.Find(2)!.AttackCount.ShouldBe(0);
        world.Own!.HitCount.ShouldBe(0);
    }

    [Fact]
    public void The_hud_words_for_statuses_and_held_intents()
    {
        UiText.StatusLeft(1, 7).ShouldBe("7s");
        UiText.StatusLeft(3, 12).ShouldBe("x3 12s");
        UiText.StatusLetter("Poison").ShouldBe("P");
        UiText.Held(AttackReasons.CannotAttack).ShouldNotBeEmpty();
        UiText.Held("cannot_move").ShouldNotBeEmpty();
        UiText.Held(AttackReasons.OutOfRange).ShouldBeEmpty();
    }
}
