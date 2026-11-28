using System.Net;
using Game.Content.Loading;
using Game.Content.Registry;
using Game.Server.Events;
using Game.Server.Hosting;
using Game.Server.Net;
using Game.Server.World;
using Game.Server.World.Combat;
using Game.Server.World.Inventory;
using Game.Server.World.Reload;
using Game.Shared.Protocol;
using Game.Shared.Vocabulary;
using Game.Shared.World;
using Game.Tests.Network;
using Game.Tests.Server;
using Game.Tests.World;

namespace Game.Tests.Reload;

/// <summary>
/// A copy of the real content and themes in a temporary folder, a world on it ticked by hand, and a reloader over the copy (T-801). Tests edit the copy's files and call
/// <see cref="ContentReloader.TryReload"/> themselves, so no file watcher or timer is involved.
/// </summary>
public sealed class ReloadRig : IDisposable
{
    public const string Meadow = "map.starter_meadow";
    public static readonly GridCell Start = new(12, 12);

    public readonly string Root = Path.Combine(Path.GetTempPath(), "t801-" + Guid.NewGuid().ToString("N"));
    public readonly ManualClock Clock = new();
    public readonly ListLog Log = new();
    public readonly InMemoryProgressionStore Progressions = new();
    public readonly InMemoryInventoryStore Inventories = new();
    public readonly List<(long Tick, IGameEvent Event)> Events = new();
    public readonly GameWorld World;
    public readonly ContentReloader Reloader;
    public readonly ServerContent Boot;

    public ReloadRig()
    {
        Copy(Path.Combine(TestGrids.RepoRoot(), "content"), Content);
        Copy(Path.Combine(TestGrids.RepoRoot(), "themes"), Path.Combine(Root, "themes"));
        var database = ContentBoot.Load(Content, "default", out var issues) ?? throw new InvalidOperationException(string.Join("\n", issues));
        Boot = ServerContent.Build(database);
        World = new GameWorld(database.Grids.Values, ServerPipeline.Create(Boot, Progressions, Inventories, Clock), 801, Clock, Log);
        foreach (var instance in World.Instances) instance.Events.SubscribeAll(e => Events.Add((instance.TickNumber, e)));
        Reloader = new ContentReloader(Content, "default", World, Boot, Game.Content.ClientExport.ClientBundleExporter.Hash(database), next =>
        {
            var start = new NewCharacters(next);
            return player => Progressions.TryGet(player.Id, out var state) ? start.CreateStats(state) : null;
        }, Log);
    }

    public string Content => Path.Combine(Root, "content");

    public MapInstance Instance => World.GetInstance(Meadow);

    public EntityId Join()
    {
        var start = new NewCharacters(Boot.Database);
        var id = World.Spawn(EntityKind.Player, Meadow, Start, Directions.S, e =>
        {
            e.Stats = start.CreateStats();
            e.DefinitionId = start.JobId;
        });
        Ticks(2);
        return id;
    }

    public Entity Get(EntityId id) => Instance.TryGetEntity(id, out var e) ? e : throw new InvalidOperationException($"{id} is gone");

    public void Ticks(int n = 1)
    {
        for (var i = 0; i < n; i++)
            foreach (var instance in World.Instances) instance.Tick();
    }

    public List<Entity> Monsters(string? id = null) =>
        Instance.Entities.Where(e => e.Kind == EntityKind.Monster && (id is null || e.DefinitionId == id)).ToList();

    /// <summary>Rewrites one content file (relative to the content root) through <paramref name="change"/>; fails if nothing changed.</summary>
    public void Edit(string relative, Func<string, string> change)
    {
        var path = Path.Combine(Content, relative);
        var before = File.ReadAllText(path);
        var after = change(before);
        after.ShouldNotBe(before, $"the edit of {relative} changed nothing");
        File.WriteAllText(path, after);
    }

    public void Write(string relative, string text) => File.WriteAllText(Path.Combine(Content, relative), text);

    public void Delete(string relative) => File.Delete(Path.Combine(Content, relative));

    public void Dispose()
    {
        Reloader.Dispose();
        try
        {
            Directory.Delete(Root, recursive: true);
        }
        catch (IOException)
        {
            // a scanner may hold a file for a moment; the folder is in the temp directory
        }
    }

    private static void Copy(string from, string to)
    {
        Directory.CreateDirectory(to);
        foreach (var dir in Directory.GetDirectories(from, "*", SearchOption.AllDirectories))
            Directory.CreateDirectory(Path.Combine(to, Path.GetRelativePath(from, dir)));
        foreach (var file in Directory.GetFiles(from, "*", SearchOption.AllDirectories))
            File.Copy(file, Path.Combine(to, Path.GetRelativePath(from, file)));
    }
}

public sealed class HotReloadTests : IDisposable
{
    private const string Slime = "monster.gel_slime";
    private readonly ReloadRig _rig = new();

    public void Dispose() => _rig.Dispose();

    private static string Hp(string yaml, int hp) => System.Text.RegularExpressions.Regex.Replace(yaml, @"max_hp: \d+", $"max_hp: {hp}");

    [Fact]
    public void A_valid_change_is_swapped_in_between_ticks_all_at_once()
    {
        _rig.Ticks(2);
        var before = _rig.Monsters(Slime);
        before.ShouldNotBeEmpty();
        var oldMaxHp = before[0].Vitals!.MaxHp;
        _rig.Edit("monsters/gel_slime.yaml", t => Hp(t, 400));

        _rig.Reloader.TryReload().ShouldBe(ReloadOutcome.Swapped);

        // Handed over, not yet swapped: nothing changes until the instance's next tick begins.
        _rig.Instance.ContentGeneration.ShouldBe(0);
        _rig.Monsters(Slime).Select(m => m.Id).ShouldBe(before.Select(m => m.Id));
        _rig.Reloader.LastSwap!.Done.ShouldBeFalse();

        _rig.Ticks();

        _rig.Reloader.LastSwap.Done.ShouldBeTrue();
        foreach (var instance in _rig.World.Instances) instance.ContentGeneration.ShouldBe(1);
        // Every old monster is gone in that one tick (removed, never killed); the map's spawns are filled again with the new definition.
        foreach (var old in before) _rig.Instance.TryGetEntity(old.Id, out _).ShouldBeFalse();
        _rig.Events.Select(e => e.Event).OfType<EntityDied>().ShouldBeEmpty();
        _rig.Ticks();
        var after = _rig.Monsters(Slime);
        after.Count.ShouldBe(before.Count);
        after.ShouldAllBe(m => m.Vitals!.MaxHp > oldMaxHp);
        _rig.Log.Lines.ShouldContain(l => l.Contains("[reload] monsters: changed monster.gel_slime", StringComparison.Ordinal));
        _rig.Log.Lines.ShouldContain(l => l.Contains("content 1 swapped in at tick", StringComparison.Ordinal));
    }

    [Fact]
    public void An_invalid_change_is_refused_and_nothing_changes()
    {
        _rig.Ticks(2);
        var ids = _rig.Instance.Entities.Select(e => e.Id).ToList();
        _rig.Edit("shops/meadow_goods.yaml", t => t.Replace("item_id: item.antidote", "item_id: item.no_such_thing", StringComparison.Ordinal));

        _rig.Reloader.TryReload().ShouldBe(ReloadOutcome.Invalid);
        _rig.Ticks(3);

        _rig.Reloader.LastSwap.ShouldBeNull();
        _rig.Reloader.Current.ShouldBeSameAs(_rig.Boot);
        _rig.Instance.ContentGeneration.ShouldBe(0);
        _rig.Instance.Entities.Select(e => e.Id).Take(ids.Count).ShouldBe(ids);
        _rig.Log.Lines.ShouldContain(l => l.Contains("REFUSED: the content has errors", StringComparison.Ordinal) && l.Contains("item.no_such_thing", StringComparison.Ordinal));
    }

    [Fact]
    public void A_monster_whose_definition_vanished_is_removed_and_not_spawned_again()
    {
        // A monster no map spawns (only such a one can vanish: a spawn naming it would be a load error): added, put in the world, then deleted.
        _rig.Write("monsters/test_ghost.yaml", File.ReadAllText(Path.Combine(_rig.Content, "monsters/gel_slime.yaml"))
            .Replace("id: monster.gel_slime", "id: monster.test_ghost", StringComparison.Ordinal));
        _rig.Reloader.TryReload().ShouldBe(ReloadOutcome.Swapped);
        _rig.Ticks(2);
        var catalog = _rig.Reloader.Current.Monsters;
        catalog.TryGet("monster.test_ghost", out var ghost).ShouldBeTrue();
        var id = _rig.World.Spawn(EntityKind.Monster, ReloadRig.Meadow, new GridCell(20, 12), Directions.S, e =>
        {
            e.DefinitionId = ghost!.Id;
            e.Stats = catalog.CreateStats(ghost);
        });
        _rig.Ticks();
        _rig.Instance.TryGetEntity(id, out _).ShouldBeTrue();

        _rig.Delete("monsters/test_ghost.yaml");
        _rig.Reloader.TryReload().ShouldBe(ReloadOutcome.Swapped);
        _rig.Ticks(3);

        _rig.Instance.TryGetEntity(id, out _).ShouldBeFalse();
        _rig.Monsters("monster.test_ghost").ShouldBeEmpty();
        _rig.Monsters(Slime).ShouldNotBeEmpty();   // the rest of the map is spawned again
    }

    [Fact]
    public void A_playing_character_comes_back_on_its_cell_with_its_hp_and_a_sheet_from_the_new_content()
    {
        var player = _rig.Join();
        var entity = _rig.Get(player);
        entity.Vitals!.Hp = entity.Vitals.MaxHp - 5;
        var hp = entity.Vitals.Hp;
        var oldSheet = entity.Stats;
        _rig.Events.Clear();
        _rig.Edit("monsters/gel_slime.yaml", t => Hp(t, 300));

        _rig.Reloader.TryReload().ShouldBe(ReloadOutcome.Swapped);
        _rig.Ticks();

        var back = _rig.Get(player);
        back.ShouldBeSameAs(entity);
        back.Cell.ShouldBe(ReloadRig.Start);
        back.Stats.ShouldNotBeSameAs(oldSheet);
        back.Vitals!.Hp.ShouldBe(hp);
        var moves = _rig.Events.Select(e => e.Event).Where(e => e is EntityLeftMap { Kind: EntityKind.Player } or EntityEnteredMap { Kind: EntityKind.Player }).ToList();
        moves.Count.ShouldBe(2);
        moves[0].ShouldBeOfType<EntityLeftMap>().Reason.ShouldBe(MapChangeReason.Warp);
        moves[1].ShouldBeOfType<EntityEnteredMap>().Reason.ShouldBe(MapChangeReason.Warp);
        _rig.World.LocationOf(player).ShouldBe(ReloadRig.Meadow);   // never out of the world: its progression and bag stay
        _rig.Progressions.TryGet(player, out _).ShouldBeTrue();
        _rig.Inventories.TryGet(player, out _).ShouldBeTrue();
    }

    [Fact]
    public void A_change_to_a_map_grid_needs_a_restart_and_reloads_nothing()
    {
        _rig.Edit("maps/starter_meadow.grid.json", t => System.Text.RegularExpressions.Regex.Replace(t, "\"count\": ?(\\d+)", m => $"\"count\":{int.Parse(m.Groups[1].Value) + 1}", System.Text.RegularExpressions.RegexOptions.None, TimeSpan.FromSeconds(1)));

        _rig.Reloader.TryReload().ShouldBe(ReloadOutcome.NeedsRestart);

        _rig.Reloader.LastSwap.ShouldBeNull();
        _rig.Log.Lines.ShouldContain(l => l.Contains("NEEDS A RESTART: map grids cannot hot-reload", StringComparison.Ordinal) && l.Contains(ReloadRig.Meadow, StringComparison.Ordinal));
        _rig.Log.Lines.ShouldContain(l => l.Contains("nothing was reloaded", StringComparison.Ordinal));
    }

    [Fact]
    public void Removing_an_item_needs_a_restart_since_a_character_may_hold_it()
    {
        // An item nothing refers to: the set is valid without it.
        _rig.Write("items/test_pebble.yaml", "id: item.test_pebble\nname: Test Pebble\nkind: material\nweight: 1\nsell_price: 1\n");
        _rig.Reloader.TryReload().ShouldBe(ReloadOutcome.Swapped);
        _rig.Delete("items/test_pebble.yaml");

        _rig.Reloader.TryReload().ShouldBe(ReloadOutcome.NeedsRestart);

        _rig.Log.Lines.ShouldContain(l => l.Contains("removing item.test_pebble needs a restart", StringComparison.Ordinal));
    }

    [Fact]
    public void A_balance_section_read_at_start_needs_a_restart()
    {
        _rig.Edit("balance.yaml", t => System.Text.RegularExpressions.Regex.Replace(t, @"(?m)^(\s+view_radius:\s*)(\d+)", m => m.Groups[1].Value + (int.Parse(m.Groups[2].Value) + 1)));

        _rig.Reloader.TryReload().ShouldBe(ReloadOutcome.NeedsRestart);

        _rig.Log.Lines.ShouldContain(l => l.Contains("balance.yaml aoi cannot hot-reload", StringComparison.Ordinal));
    }

    [Fact]
    public void Files_that_change_but_make_the_same_content_reload_nothing()
    {
        _rig.Edit("monsters/gel_slime.yaml", t => t + "\n# a comment only\n");

        _rig.Reloader.TryReload().ShouldBe(ReloadOutcome.Unchanged);
        _rig.Reloader.LastSwap.ShouldBeNull();
    }

    [Fact]
    public void A_display_change_gives_a_new_bundle_hash_and_a_server_only_change_keeps_it()
    {
        var hash = _rig.Reloader.CurrentHash;
        var heard = new List<string>();
        _rig.Reloader.Reloaded += (_, h) => heard.Add(h);

        _rig.Edit("monsters/gel_slime.yaml", t => Hp(t, 222));
        _rig.Reloader.TryReload().ShouldBe(ReloadOutcome.Swapped);
        _rig.Reloader.CurrentHash.ShouldBe(hash);   // monster stats are not in the bundle

        _rig.Edit("monsters/gel_slime.yaml", t => t.Replace("name: Gel Slime", "name: Gel Slime Prime", StringComparison.Ordinal));
        _rig.Reloader.TryReload().ShouldBe(ReloadOutcome.Swapped);
        _rig.Reloader.CurrentHash.ShouldNotBe(hash);

        heard.ShouldBe([hash, _rig.Reloader.CurrentHash]);
        _rig.Reloader.Current.Generation.ShouldBe(2);
    }
}

public sealed class HotReloadGateTests
{
    [Fact]
    public void Only_a_debug_development_server_on_loopback_may_hot_reload()
    {
        HotReloadGate.Problems(debugBuild: true, development: true, IPAddress.Loopback).ShouldBeEmpty();
        HotReloadGate.Problems(debugBuild: true, development: true, IPAddress.IPv6Loopback).ShouldBeEmpty();
        HotReloadGate.Problems(debugBuild: false, development: true, IPAddress.Loopback).ShouldHaveSingleItem().ShouldContain("release build");
        HotReloadGate.Problems(debugBuild: true, development: false, IPAddress.Loopback).ShouldHaveSingleItem().ShouldContain("development servers only");
        HotReloadGate.Problems(debugBuild: true, development: true, IPAddress.Any).ShouldHaveSingleItem().ShouldContain("not on a loopback address");
        HotReloadGate.Problems(debugBuild: true, development: true, IPAddress.Parse("192.168.1.20")).ShouldHaveSingleItem().ShouldContain("192.168.1.20");
    }

    [Fact]
    public void The_tests_run_a_debug_build()
    {
        // The release refusal is the same function with debugBuild false (above); a release build of the server passes false (HotReloadGate.DebugBuild).
#if DEBUG
        HotReloadGate.DebugBuild.ShouldBeTrue();
#else
        HotReloadGate.DebugBuild.ShouldBeFalse();
#endif
    }

    [Fact]
    public void The_option_is_parsed_and_off_by_default()
    {
        ServerOptions.Parse([], out _)!.HotReload.ShouldBeFalse();
        ServerOptions.Parse(["--hot-reload"], out _)!.HotReload.ShouldBeTrue();
    }
}

public sealed class ContentChangedMessageTests
{
    [Fact]
    public void Content_changed_round_trips_and_is_a_server_type()
    {
        var bytes = ProtocolJson.ToBytes(new ContentChangedMessage("abc123"));
        System.Text.Encoding.UTF8.GetString(bytes).ShouldBe("{\"type\":\"content_changed\",\"content\":\"abc123\"}");
        ProtocolJson.ReadServer(bytes).Message.ShouldBe(new ContentChangedMessage("abc123"));
        MessageTypes.ServerToClient.Contains(MessageTypes.ContentChanged).ShouldBeTrue();
    }

    [Fact]
    public async Task A_development_gateway_tells_every_player_and_wants_the_new_hash_from_then_on()
    {
        await using var rig = await GatewayRig.StartAsync();
        using var client = await rig.JoinAsync();

        rig.Gateway.ContentChanged(rig.ContentHash).ShouldBe(0);   // the same hash: nothing to say
        rig.Gateway.ContentChanged("f00d").ShouldBe(1);

        (await client.WaitForAsync<ContentChangedMessage>()).Content.ShouldBe("f00d");
        rig.Gateway.ContentHash.ShouldBe("f00d");
        using var late = await rig.ConnectAsync();
        await late.SendAsync(new HelloMessage(ProtocolVersion.Current, "late", rig.ContentHash));
        (await late.WaitForByeAsync()).ShouldBe(ByeReasons.UpdateRequired);
    }

    [Fact]
    public async Task A_gateway_that_is_not_a_development_one_refuses_to_say_it()
    {
        await using var rig = await GatewayRig.StartAsync(development: false, tls: true);

        Should.Throw<InvalidOperationException>(() => rig.Gateway.ContentChanged("f00d"));
    }
}
