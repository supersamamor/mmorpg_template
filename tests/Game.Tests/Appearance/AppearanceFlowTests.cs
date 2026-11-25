using System.Text;
using Game.Server.Accounts;
using Game.Shared.Accounts;
using Game.Server.Net;
using Game.Server.World;
using Game.Server.World.Aoi;
using Game.Shared.Protocol;
using Game.Shared.Vocabulary;
using Game.Shared.World;
using Game.Tests.Accounts;
using Game.Tests.Network;
using Game.Tests.Server;
using Game.Tests.World;

namespace Game.Tests.Appearance;

/// <summary>T-705: creation, listing, the entity, and the wire: snapshot and appeared carry the looks, a delta never repeats them.</summary>
public class AppearanceFlowTests
{
    private static readonly AppearanceChoice Wide = new(Genders.Female, "skin.deep", "face.female_sly", "hair.female_curls", "haircolor.ember");

    private static readonly AppearanceRecord WideLook = new(Genders.Female, "skin.deep", "face.female_sly", "hair.female_curls", "haircolor.ember");

    private static readonly AppearanceRecord Maleness = new(Genders.Male, "skin.light", "face.male_calm", "hair.male_crop", "haircolor.chestnut");

    // ---- the account service ----

    [Fact]
    public async Task A_character_is_created_with_its_looks_and_listed_with_them()
    {
        var rig = new ServiceRig();
        var token = await rig.RegisterAsync();
        var made = await rig.Service.CreateAsync(token, "Lyra", Wide, default);
        made.Value!.Appearance.ShouldBe(WideLook);
        var plain = await rig.Service.CreateAsync(token, "Mira", default);
        plain.Value!.Appearance.ShouldBe(Maleness, "a request without any gets the first gender's defaults");
        var half = await rig.Service.CreateAsync(token, "Nora", new AppearanceChoice(Genders.Female), default);
        half.Value!.Appearance!.Face.ShouldBe("face.female_calm");

        var list = (await rig.Service.ListAsync(token, default)).Value!.Characters;
        list.Select(c => c.Appearance).ShouldBe([WideLook, Maleness, half.Value.Appearance]);
        (await rig.Service.ResolveAsync(token, made.Value.Id, "a", default)).Value!.Appearance.ShouldBe(WideLook);
    }

    [Theory]
    [InlineData("other", null, null, null, null)]
    [InlineData(null, "skin.nope", null, null, null)]
    [InlineData(null, null, "face.female_calm", null, null)]
    [InlineData("female", null, "face.male_calm", null, null)]
    [InlineData(null, null, null, "hair.nope", null)]
    [InlineData(null, null, null, null, "haircolor.nope")]
    public async Task A_bad_choice_is_refused_and_nothing_is_created(string? gender, string? skin, string? face, string? hair, string? color)
    {
        var rig = new ServiceRig();
        var token = await rig.RegisterAsync();
        var refused = await rig.Service.CreateAsync(token, "Lyra", new AppearanceChoice(gender, skin, face, hair, color), default);
        refused.Error.ShouldBe(AccountErrors.InvalidAppearance);
        (await rig.Service.ListAsync(token, default)).Value!.Characters.ShouldBeEmpty();
        (await rig.Service.CreateAsync(token, "Lyra", default)).Ok.ShouldBeTrue("the name was not taken by the refused request");
    }

    [Fact]
    public async Task A_character_stored_without_looks_shows_the_defaults()
    {
        var rig = new ServiceRig();
        var token = await rig.RegisterAsync();
        var account = (await rig.Memory.FindSessionAsync(SessionTokens.HashOf(token)!, rig.Time.GetUtcNow().UtcDateTime, default))!.Value;
        await rig.Memory.CreateCharacterAsync(account, Guid.NewGuid(), "Old", CharacterNameRules.Key("Old"), "job.x", 3, null, DateTime.UtcNow, default);
        (await rig.Service.ListAsync(token, default)).Value!.Characters.Single().Appearance.ShouldBe(Maleness);
    }

    // ---- HTTP ----

    [Fact]
    public async Task Over_HTTP_the_looks_go_in_and_come_out_and_a_bad_one_is_a_400_with_the_term()
    {
        var store = new MemoryAccountStore();
        await using var rig = await GatewayRig.StartAsync(accounts: new AccountRigOptions(store, new FakeHasher(), new AccountLimits { RequestBurst = 10_000, RegisterBurst = 1_000 }, new FakeTime()));
        using var http = new AccountHttp(rig.Endpoint);
        var token = await http.RegisterAsync("ada");

        var (status, body, _) = await http.SendAsync(HttpMethod.Post, AccountPaths.Characters,
            """{"name":"Lyra","appearance":{"gender":"female","skin":"skin.deep","face":"face.female_sly","hair":"hair.female_curls","hair_color":"haircolor.ember"}}""", token);
        status.ShouldBe(201);
        body.GetProperty("appearance").GetProperty("face").GetString().ShouldBe("face.female_sly");
        body.GetProperty("appearance").GetProperty("hair_color").GetString().ShouldBe("haircolor.ember");

        (status, body, _) = await http.SendAsync(HttpMethod.Post, AccountPaths.Characters, """{"name":"Mira"}""", token);
        status.ShouldBe(201);
        body.GetProperty("appearance").GetProperty("gender").GetString().ShouldBe("male");

        (status, body, _) = await http.SendAsync(HttpMethod.Post, AccountPaths.Characters, """{"name":"Nora","appearance":{"face":"face.nope"}}""", token);
        status.ShouldBe(400);
        body.GetProperty("error").GetString().ShouldBe(AccountErrors.InvalidAppearance);
        foreach (var hostile in new[] { """{"name":"Nora","appearance":"x"}""", """{"name":"Nora","appearance":{"face":5}}""", """{"name":"Nora","appearance":{"face":"a","face":"b"}}""" })
        {
            (status, body, _) = await http.SendAsync(HttpMethod.Post, AccountPaths.Characters, hostile, token);
            (status, body.GetProperty("error").GetString()).ShouldBe((400, AccountErrors.InvalidRequest));
        }

        (status, body, _) = await http.SendAsync(HttpMethod.Get, AccountPaths.Characters, token: token);
        var list = body.GetProperty("characters");
        list.GetArrayLength().ShouldBe(2, "only the two good ones were created");
        list[0].GetProperty("appearance").GetProperty("skin").GetString().ShouldBe("skin.deep");
    }

    // ---- in the world ----

    private static async Task<(GatewayRig Rig, string Token)> StartAsync()
    {
        var rig = await GatewayRig.StartAsync(accounts: new AccountRigOptions(new MemoryAccountStore(), new FakeHasher(), new AccountLimits(), new FakeTime()));
        return (rig, (await rig.Accounts!.RegisterAsync("ada", ServiceRig.Password, "a", default)).Value!.Token);
    }

    [Fact]
    public async Task The_entity_has_the_looks_the_character_was_created_with_and_the_dev_login_gets_the_defaults()
    {
        var (rig, token) = await StartAsync();
        await using var _ = rig;
        var lyra = (await rig.Accounts!.CreateAsync(token, "Lyra", Wide, default)).Value!.Id;
        var mira = (await rig.Accounts.CreateAsync(token, "Mira", default)).Value!.Id;

        var first = await rig.PlayAsync(token, lyra);
        var snapshot = await first.WaitForAsync<SnapshotMessage>();
        snapshot.Entities.Single(e => e.Id == first.Id).Appearance.ShouldBe(WideLook);
        rig.World.Instances.SelectMany(i => i.Players).Single(p => p.Id.Value == first.Id).Appearance.ShouldBe(WideLook);

        // A second player arrives in the first one's view: an appeared entry carries it.
        var second = await rig.PlayAsync(token, mira);   // replaces the first session of the account
        await second.WaitForAsync<SnapshotMessage>();
        (await first.WaitForByeAsync()).ShouldBe(ByeReasons.Replaced);

        var dev = await rig.JoinAsync("dev");
        var seen = await dev.WaitForAsync<SnapshotMessage>();
        seen.Entities.Single(e => e.Id == dev.Id).Appearance.ShouldBe(Maleness);
        seen.Entities.Where(e => e.Kind == EntityKinds.Player && e.Id != dev.Id).ShouldAllBe(e => e.Appearance == Maleness);
        seen.Entities.Where(e => e.Kind != EntityKinds.Player).ShouldAllBe(e => e.Appearance == null);
    }

    [Fact]
    public async Task Another_player_is_told_by_appeared_and_a_warp_keeps_the_looks()
    {
        var (rig, token) = await StartAsync();
        await using var _ = rig;
        var token2 = (await rig.Accounts!.RegisterAsync("bob", ServiceRig.Password, "a", default)).Value!.Token;
        var lyra = (await rig.Accounts.CreateAsync(token, "Lyra", Wide, default)).Value!.Id;
        var bron = (await rig.Accounts.CreateAsync(token2, "Bron", new AppearanceChoice(Hair: "hair.male_topknot"), default)).Value!.Id;

        var watcher = await rig.PlayAsync(token, lyra);
        await watcher.WaitForAsync<SnapshotMessage>();
        var arriving = await rig.PlayAsync(token2, bron);
        var delta = await watcher.WaitForAsync<DeltaMessage>(d => d.Appeared.Any(e => e.Id == arriving.Id && e.Kind == EntityKinds.Player));
        delta.Appeared.Single(e => e.Id == arriving.Id).Appearance.ShouldBe(Maleness with { Hair = "hair.male_topknot" });

        // Warped away and back: the new snapshot has the same looks, on the same entity.
        var other = rig.World.Instances.Select(i => i.MapId).First(m => m != rig.Start.MapId);
        var grid = rig.Content.GetGrid(other);
        var cell = Enumerable.Range(0, grid.Width * grid.Height).Select(i => new GridCell(i % grid.Width, i / grid.Width)).First(c => grid.IsWalkable(c.X, c.Y));
        rig.World.Warp(new EntityId(watcher.Id), other, cell).ShouldBeTrue();
        var warped = await watcher.WaitForAsync<SnapshotMessage>(s => s.MapId == other);
        warped.Entities.Single(e => e.Id == watcher.Id).Appearance.ShouldBe(WideLook);
    }

    // ---- the view records: snapshot and appeared, never a change ----

    private sealed class WireSink : IViewSink
    {
        private readonly ViewMessageWriter _writer = new();
        public readonly List<string> Snapshots = [];
        public readonly List<string> Deltas = [];

        public void OnSnapshot(ViewSnapshot snapshot) => Snapshots.Add(Encoding.UTF8.GetString(_writer.Write(snapshot)));

        public void OnDelta(ViewDelta delta) => Deltas.Add(Encoding.UTF8.GetString(_writer.Write(delta)));
    }

    [Fact]
    public void A_snapshot_and_an_appeared_entry_carry_the_looks_and_a_change_never_does()
    {
        var rig = new AoiRig();
        var sink = new WireSink();
        var me = rig.Add(new GridCell(2, 2), kind: EntityKind.Player, sink: sink);
        var other = rig.Add(new GridCell(14, 2), kind: EntityKind.Player);
        var monster = rig.Add(new GridCell(3, 2), key: "monster.gel_slime");
        rig.Ticks();
        rig.Get(me).Appearance = WideLook;
        rig.Get(other).Appearance = Maleness;
        rig.Ticks();   // joined before the looks were set: what a snapshot is asked for again says it

        rig.Send(new RequestSnapshot(me));
        rig.Ticks();
        var snapshot = sink.Snapshots.Last();
        snapshot.ShouldContain("\"appearance\":{\"gender\":\"female\",\"skin\":\"skin.deep\",\"face\":\"face.female_sly\",\"hair\":\"hair.female_curls\",\"hair_color\":\"haircolor.ember\"}");
        snapshot.Split("\"appearance\"").Length.ShouldBe(2, "the monster has none");

        var before = sink.Deltas.Count;
        rig.Send(new MoveTo(other, new GridCell(6, 2)));   // into view: appeared
        rig.Send(new MoveTo(me, new GridCell(2, 6)));      // and a change of the observer itself
        rig.Send(new MoveTo(monster, new GridCell(3, 6)));
        rig.Ticks(30);
        var deltas = sink.Deltas.Skip(before).ToList();
        deltas.Where(d => d.Contains("\"appeared\"")).ShouldContain(d => d.Contains("\"appearance\":{\"gender\":\"male\""));
        deltas.Where(d => d.Contains("\"changed\"")).ShouldNotBeEmpty();
        foreach (var delta in deltas)
        {
            var changed = delta.Contains("\"changed\"") ? delta[delta.IndexOf("\"changed\"", StringComparison.Ordinal)..] : string.Empty;
            var cut = changed.IndexOf("\"disappeared\"", StringComparison.Ordinal);
            (cut < 0 ? changed : changed[..cut]).ShouldNotContain("appearance", customMessage: delta);
        }
    }

    // ---- bytes ----

    private static int Deflated(string text)
    {
        using var output = new MemoryStream();
        using (var deflate = new System.IO.Compression.DeflateStream(output, System.IO.Compression.CompressionLevel.Fastest, true)) deflate.Write(Encoding.UTF8.GetBytes(text));
        return (int)output.Length;
    }

    [Fact]
    public void What_the_looks_cost_on_the_wire()
    {
        var rig = new AoiRig();
        var with = new WireSink();
        var observer = rig.Add(new GridCell(2, 2), kind: EntityKind.Player, sink: with);
        var ids = new List<EntityId>();
        for (var i = 0; i < 20; i++) ids.Add(rig.Add(new GridCell(3 + i % 5, 2 + i / 5), kind: EntityKind.Player));
        rig.Ticks();
        foreach (var id in ids.Append(observer)) rig.Get(id).Appearance = WideLook;
        rig.Send(new RequestSnapshot(observer));
        rig.Ticks();
        var after = Encoding.UTF8.GetByteCount(with.Snapshots.Last());
        var afterDeflated = Deflated(with.Snapshots.Last());
        foreach (var id in ids.Append(observer)) rig.Get(id).Appearance = null;
        rig.Send(new RequestSnapshot(observer));
        rig.Ticks();
        var before = Encoding.UTF8.GetByteCount(with.Snapshots.Last());
        var perPlayer = (after - before) / 21.0;
        File.AppendAllText(Path.Combine(Path.GetTempPath(), "t705-bytes.txt"), $"snapshot of 21 players: {before} -> {after} bytes, {perPlayer:0.0} per player\n");
        perPlayer.ShouldBeInRange(100, 200, "five short ids: about 135 bytes a player as JSON (T-705 measured 1145 -> 3980 bytes for 21 players)");
        (afterDeflated - Deflated(with.Snapshots.Last())).ShouldBeLessThan(21 * 12, "and a few bytes a player once compressed (234 -> 349 measured)");
    }
}
