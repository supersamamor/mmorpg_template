using Game.Server.World;
using Game.Shared.Protocol;
using Game.Shared.Definitions;
using Game.Shared.Vocabulary;
using Game.Shared.World;

namespace Game.Tests.Network;

/// <summary>
/// T-307: combat over the wire, end to end on the real stack (sample content, real pipeline, real sockets). A new character starts in the
/// starter meadow's safe zone, so every fight first walks out of it, south-east toward the gel slimes.
/// </summary>
public class GatewayCombatTests
{
    private const string Slime = "monster.gel_slime";
    private static readonly GridCell OutsideSafeZone = new(17, 15);   // the safe zone is x 8-17, y 7-13; the slimes roam x 16-25, y 16-23

    [Fact]
    public async Task A_player_kills_a_meadow_monster_and_sees_the_hits_the_death_its_exp_and_a_drop()
    {
        await using var rig = await GatewayRig.StartAsync(setupWorld: Veterans);
        var me = await rig.JoinAsync("hunter");
        var welcomeExp = await me.WaitForAsync<ExpMessage>();
        welcomeExp.ShouldBe(new ExpMessage(1, 0, rig.Content.ExpTables.Get(rig.Content.Balance.Progression.BaseExpTableId).Exp[0], 1, 0, welcomeExp.JobNext));
        welcomeExp.JobNext.ShouldBeGreaterThan(0);
        var snapshot = me.Messages.OfType<SnapshotMessage>().First();
        snapshot.Vitals.ShouldNotBeNull();
        snapshot.Vitals!.Hp.ShouldBe(snapshot.Vitals.MaxHp);
        snapshot.Entities.Single(e => e.Id == me.Id).Hp.ShouldBe(1);
        snapshot.Entities.ShouldContain(e => e.Kind == EntityKinds.Monster && e.Hp == 1);

        EntityRecord? drop = null;
        var kills = new List<long>();
        for (var attempt = 0; attempt < 12 && drop is null; attempt++)
        {
            var killed = await KillOneAsync(me);
            if (killed is { } id) kills.Add(id);
            drop = me.View.FirstOrDefault(e => e.Kind == EntityKinds.Item);
        }

        kills.ShouldNotBeEmpty();
        var victim = kills[0];
        var hits = me.Hits;
        hits.ShouldContain(h => h.Attacker == me.Id && h.Target == victim && h.Result != HitResults.Miss && h.Amount > 0);
        hits.ShouldAllBe(h => HitResults.All.Contains(h.Result));
        // The HP bar of the victim went down in 0.01 steps, then it was flagged dead (the spawner removes it a tick later).
        var bars = me.Messages.OfType<DeltaMessage>().SelectMany(d => d.Changed).Where(c => c.Id == victim && (c.Fields & ChangedFields.Hp) != 0).ToList();
        bars.ShouldNotBeEmpty();
        bars.ShouldAllBe(c => c.Hp >= 0 && c.Hp <= 1 && Math.Abs(c.Hp * 100 - Math.Round(c.Hp * 100)) < 1e-9);
        me.Messages.OfType<DeltaMessage>().SelectMany(d => d.Changed).ShouldContain(c => c.Id == victim && c.Dead && c.Hp == 0);
        await me.WaitForAsync<AttackStoppedMessage>(m => m.Target == victim && m.Reason == AttackReasons.TargetDead);
        var exp = await me.WaitForAsync<ExpMessage>(m => m != welcomeExp && (m.BaseExp > 0 || m.BaseLevel > 1));
        exp.JobExp.ShouldBeGreaterThan(0);

        drop.ShouldNotBeNull("no drop in 12 kills");
        drop.Quantity.ShouldBeGreaterThan(0);
        drop.DisplayKey.ShouldStartWith("item.");
        drop.Mine.ShouldBe(true);   // the top damager's drop
    }

    [Fact]
    public async Task Stop_ends_an_auto_attack_even_while_standing_still()
    {
        await using var rig = await GatewayRig.StartAsync(setupWorld: Veterans);
        var me = await rig.JoinAsync("stopper");
        await WalkOutAsync(me);
        var target = await NearestSlimeAsync(me);
        await me.SendAsync(new AttackMessage(target));
        await Poll.UntilAsync(() => me.Hits.Count(h => h.Attacker == me.Id), n => n > 0, "my first hit", 20_000);

        await me.SendAsync(new StopMessage());
        await me.WaitForAsync<AttackStoppedMessage>(m => m.Target == target && m.Reason == AttackReasons.Cancelled);
    }

    [Fact]
    public async Task Hostile_attack_messages_are_refused_without_harm()
    {
        await using var rig = await GatewayRig.StartAsync();
        var me = await rig.JoinAsync("fuzzer");
        var other = await rig.JoinAsync("bystander");
        await Poll.UntilAsync(() => me.SeenIds.Contains(other.Id), b => b, "the other player in view");

        // An id nobody has, and a real entity on the same map out of view (combat would happily chase that one).
        await me.SendAsync(new AttackMessage(987_654_321));
        await me.WaitForAsync<RejectedMessage>(r => r is { Intent: MessageTypes.Attack, Reason: AttackReasons.TargetGone, Target: 987_654_321 });
        var unseen = Enumerable.Range(1, 400).Select(i => (long)i)
            .First(i => rig.World.LocationOf(new EntityId(i)) == rig.Start.MapId && !me.SeenIds.Contains(i));
        await me.SendAsync(new AttackMessage(unseen));
        await me.WaitForAsync<RejectedMessage>(r => r.Reason == AttackReasons.TargetGone && r.Target == unseen);

        // Itself and another player: no PvP in v1.
        await me.SendAsync(new AttackMessage(me.Id));
        await me.WaitForAsync<RejectedMessage>(r => r.Reason == AttackReasons.InvalidTarget && r.Target == me.Id);
        await me.SendAsync(new AttackMessage(other.Id));
        await me.WaitForAsync<RejectedMessage>(r => r.Reason == AttackReasons.InvalidTarget && r.Target == other.Id);

        // Respawning while alive and stopping an attack that never started do nothing.
        await me.SendAsync(new RespawnMessage());
        await me.SendAsync(new StopAttackMessage());
        await me.SendAsync(new PingMessage(1));
        await me.WaitForAsync<PongMessage>(p => p.Seq == 1);
        me.Messages.OfType<RespawnedMessage>().ShouldBeEmpty();

        // Malformed attacks end the session (the character waits out its grace period); the server goes on.
        var bad = await rig.JoinAsync("malformed");
        await bad.SendRawAsync("""{"type":"attack","target":"7"}""");
        (await bad.WaitForByeAsync()).ShouldBe(ByeReasons.Malformed);
        rig.Gateway.LinkOf(new EntityId(bad.Id))!.InGrace.ShouldBeTrue();

        // A flood of attacks hits the rate limit; nobody else notices.
        var flooder = await rig.JoinAsync("flooder");
        var slime = await NearestSlimeAsync(flooder);
        for (var i = 0; i < 300 && !flooder.Closed; i++)
        {
            try
            {
                await flooder.SendAsync(new AttackMessage(slime));
            }
            catch (Exception e) when (e is System.Net.WebSockets.WebSocketException or OperationCanceledException)
            {
                break;
            }
        }
        (await flooder.WaitForByeAsync()).ShouldBe(ByeReasons.RateLimit);

        await other.SendAsync(new PingMessage(2));
        await other.WaitForAsync<PongMessage>(p => p.Seq == 2);
        var tick = rig.World.GetInstance(rig.Start.MapId).TickNumber;
        await Poll.UntilAsync(() => rig.World.GetInstance(rig.Start.MapId).TickNumber, t => t > tick + 2, "the instance keeps ticking");
        rig.Log.Lines.Where(l => l.StartsWith("ERROR")).ShouldBeEmpty();
    }

    [Fact]
    public async Task A_resume_in_the_middle_of_a_fight_gets_the_fight_back()
    {
        await using var rig = await GatewayRig.StartAsync(o => o with { IdleTimeoutMs = 600_000 }, setupWorld: Veterans);
        var first = await rig.JoinAsync("resumer");
        await WalkOutAsync(first);
        var target = await NearestSlimeAsync(first);
        await first.SendAsync(new AttackMessage(target));
        await Poll.UntilAsync(() => first.Hits.Count(h => h.Target == target), n => n > 0, "the fight started", 20_000);

        first.Dispose();   // the connection vanishes mid-fight
        await Poll.UntilAsync(() => rig.Gateway.LinkOf(new EntityId(first.Id)), l => l is { InGrace: true }, "grace period");

        var second = await rig.ResumeAsync(first.Token);
        var welcome = await second.WaitForAsync<WelcomeMessage>();
        welcome.Id.ShouldBe(first.Id);
        var snapshot = await second.WaitForAsync<SnapshotMessage>();
        snapshot.Vitals.ShouldNotBeNull();
        snapshot.Entities.Single(e => e.Id == first.Id).Hp.ShouldNotBeNull();
        await second.WaitForAsync<ExpMessage>();

        // The grace period stopped the attack; the resumed client picks it up again and the hits flow to the new socket.
        var again = second.SeenIds.Contains(target) && second.Seen(target) is { Dead: false } ? target : await NearestSlimeAsync(second);
        await second.SendAsync(new AttackMessage(again));
        await Poll.UntilAsync(() => second.Hits.Count(h => h.Attacker == second.Id || h.Target == second.Id), n => n > 0, "hits after the resume", 20_000);
    }

    // ---- helpers ----

    /// <summary>
    /// New characters hit like veterans (atk 12, always on target, 3 attacks a second), so a 35 HP slime takes about a second instead of half a
    /// minute. A test-only modifier added when the character enters its map (phase 4, before combat first sees it); the rules are unchanged.
    /// </summary>
    private static void Veterans(GameWorld world)
    {
        foreach (var instance in world.Instances)
        {
            var captured = instance;
            instance.Events.Subscribe<EntityEnteredMap>(e =>
            {
                if (e.Kind != EntityKind.Player || !captured.TryGetEntity(e.Entity, out var player) || player.Stats is not { } stats) return;
                if (e.Reason != MapChangeReason.Spawn) return;
                stats.Modifiers.Add("test", DerivedStats.Atk, ModifierOp.Override, 12);
                stats.Modifiers.Add("test", DerivedStats.Hit, ModifierOp.Override, 1000);
                stats.Modifiers.Add("test", DerivedStats.Aspd, ModifierOp.Override, 3);
            });
        }
    }

    private static async Task WalkOutAsync(TestClient client)
    {
        await client.SendAsync(new MoveMessage(OutsideSafeZone));
        await Poll.UntilAsync(() => client.Seen(client.Id), r => r is { Next: null } && r.Cell.Equals(OutsideSafeZone), "out of the safe zone", 20_000);
    }

    /// <summary>The id of the nearest living gel slime the client sees (waits for one).</summary>
    private static async Task<long> NearestSlimeAsync(TestClient client)
    {
        var slime = await Poll.UntilAsync(() =>
        {
            var at = client.Seen(client.Id)?.Cell ?? OutsideSafeZone;
            return client.View.Where(e => e is { Kind: EntityKinds.Monster, DisplayKey: Slime, Dead: false })
                .OrderBy(e => Math.Max(Math.Abs(e.Cell.X - at.X), Math.Abs(e.Cell.Y - at.Y))).FirstOrDefault();
        }, s => s is not null, "a gel slime in view", 20_000);
        return slime!.Id;
    }

    /// <summary>
    /// Walks out of the safe zone (after a respawn too), attacks the nearest slime and waits for the auto-attack to end. Returns the slime's
    /// id when the client saw it die, else null (it died or the attack stopped for another reason).
    /// </summary>
    private static async Task<long?> KillOneAsync(TestClient client)
    {
        if (client.Seen(client.Id) is { Dead: true })
        {
            await client.SendAsync(new RespawnMessage());
            await Poll.UntilAsync(() => client.Seen(client.Id), r => r is { Dead: false }, "respawned", 20_000);
        }
        await WalkOutAsync(client);
        var target = await NearestSlimeAsync(client);
        var stops = client.Messages.OfType<AttackStoppedMessage>().Count();
        await client.SendAsync(new AttackMessage(target));
        var stop = await Poll.UntilAsync(() => client.Messages.OfType<AttackStoppedMessage>().Skip(stops).FirstOrDefault(),
            m => m is not null || client.Messages.OfType<RejectedMessage>().Any(r => r.Target == target), "the attack to end", 60_000);
        if (stop?.Reason != AttackReasons.TargetDead) return null;
        // The item entities appear in the same tick as the death; give the delta a moment to arrive.
        await Poll.UntilAsync(() => client.Messages.OfType<DeltaMessage>().SelectMany(d => d.Changed).Any(c => c.Id == target && c.Dead), b => b, "dead flag");
        await Task.Delay(300);
        return target;
    }
}
