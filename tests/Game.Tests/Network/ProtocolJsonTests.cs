using System.Text;
using Game.Server.Net;
using Game.Server.World;
using Game.Server.World.Aoi;
using Game.Shared.Protocol;
using Game.Shared.Vocabulary;
using Game.Shared.World;
using Game.Tests.Server;

namespace Game.Tests.Network;

/// <summary>Golden JSON of every message type (ARCHITECTURE §10) and round trips through <see cref="ProtocolJson"/>.</summary>
public class ProtocolJsonTests
{
    private static readonly EntityRecord Walker = new(7, EntityKinds.Monster, "monster.gel_slime", new GridCell(3, 4), Directions.E, new GridCell(4, 4), 3.514);
    private static readonly EntityRecord Standing = new(42, EntityKinds.Player, null, new GridCell(12, 12), Directions.N, null, 0);
    private static readonly EntityRecord Hurt = new(7, EntityKinds.Monster, "monster.gel_slime", new GridCell(3, 4), Directions.E, null, 0, 0.37);
    private static readonly EntityRecord Corpse = new(8, EntityKinds.Monster, "monster.gel_slime", new GridCell(4, 4), Directions.W, null, 0, 0, Dead: true);
    private static readonly EntityRecord Loot = new(9, EntityKinds.Item, "item.gel_glob", new GridCell(4, 5), Directions.S, null, 0, Quantity: 2, Mine: true);

    public static TheoryData<string, ClientMessage, string> ClientGolden => new()
    {
        { "hello", new HelloMessage(1, "Ada"), """{"type":"hello","version":1,"name":"Ada"}""" },
        { "hello with content hash", new HelloMessage(1, "Ada", "ab12"), """{"type":"hello","version":1,"name":"Ada","content":"ab12"}""" },
        { "hello with a resume token", new HelloMessage(1, null, "ab12", "Qk3x-_9"), """{"type":"hello","version":1,"resume":"Qk3x-_9","content":"ab12"}""" },
        { "move", new MoveMessage(new GridCell(12, 9)), """{"type":"move","cell":[12,9]}""" },
        { "stop", new StopMessage(), """{"type":"stop"}""" },
        { "attack", new AttackMessage(7), """{"type":"attack","target":7}""" },
        { "stop_attack", new StopAttackMessage(), """{"type":"stop_attack"}""" },
        { "respawn", new RespawnMessage(), """{"type":"respawn"}""" },
        { "ping", new PingMessage(7), """{"type":"ping","seq":7}""" },
        { "logout", new LogoutMessage(), """{"type":"logout"}""" },
    };

    public static TheoryData<string, ServerMessage, string> ServerGolden => new()
    {
        { "welcome", new WelcomeMessage(1, 42, "map.starter_meadow", 10), """{"type":"welcome","version":1,"id":42,"map":"map.starter_meadow","tick_rate":10}""" },
        { "welcome with content hash", new WelcomeMessage(1, 42, "map.starter_meadow", 10, "ab12"), """{"type":"welcome","version":1,"id":42,"map":"map.starter_meadow","tick_rate":10,"content":"ab12"}""" },
        {
            "welcome with a resume token", new WelcomeMessage(1, 42, "map.starter_meadow", 10, "ab12", "Qk3x-_9"),
            """{"type":"welcome","version":1,"id":42,"map":"map.starter_meadow","tick_rate":10,"content":"ab12","token":"Qk3x-_9"}"""
        },
        {
            "snapshot", new SnapshotMessage(120, "map.starter_meadow", 42, [Walker, Standing]),
            """{"type":"snapshot","tick":120,"map":"map.starter_meadow","id":42,"entities":[""" +
            """{"id":7,"kind":"monster","display_key":"monster.gel_slime","cell":[3,4],"facing":"e","next":[4,4],"speed":3.51},""" +
            """{"id":42,"kind":"player","cell":[12,12],"facing":"n"}]}"""
        },
        {
            "delta", new DeltaMessage(121, [Standing],
                [
                    new ChangeRecord(7, ChangedFields.Cell | ChangedFields.Next, new GridCell(4, 4), null, new GridCell(5, 4), 0, null),
                    new ChangeRecord(8, ChangedFields.Cell | ChangedFields.Next | ChangedFields.Speed, new GridCell(5, 4), null, null, 0, null),
                    new ChangeRecord(9, ChangedFields.Facing | ChangedFields.DisplayKey, default, Directions.Sw, null, 0, null),
                ], [3, 5]),
            """{"type":"delta","tick":121,"appeared":[{"id":42,"kind":"player","cell":[12,12],"facing":"n"}],"changed":[{"id":7,"cell":[4,4],"next":[5,4]},{"id":8,"cell":[5,4],"next":null,"speed":0},{"id":9,"facing":"sw","display_key":null}],"disappeared":[3,5]}"""
        },
        { "delta, only gone", new DeltaMessage(5, [], [], [9]), """{"type":"delta","tick":5,"disappeared":[9]}""" },
        {
            "snapshot with combat: HP bar, a corpse, a ground item, own vitals",
            new SnapshotMessage(130, "map.starter_meadow", 42, [Hurt, Corpse, Loot, Standing with { Hp = 0.8 }], new VitalsRecord(40, 50, 9, 11)),
            """{"type":"snapshot","tick":130,"map":"map.starter_meadow","id":42,"entities":[""" +
            """{"id":7,"kind":"monster","display_key":"monster.gel_slime","cell":[3,4],"facing":"e","hp":0.37},""" +
            """{"id":8,"kind":"monster","display_key":"monster.gel_slime","cell":[4,4],"facing":"w","hp":0,"dead":true},""" +
            """{"id":9,"kind":"item","display_key":"item.gel_glob","cell":[4,5],"facing":"s","quantity":2,"mine":true},""" +
            """{"id":42,"kind":"player","cell":[12,12],"facing":"n","hp":0.8}],"vitals":{"hp":40,"max_hp":50,"sp":9,"max_sp":11}}"""
        },
        {
            "delta with combat: hits, HP, death, loot priority over, own vitals",
            new DeltaMessage(131, [],
                [
                    new ChangeRecord(7, ChangedFields.Hp, default, null, null, 0, null, Hp: 0.12),
                    new ChangeRecord(8, ChangedFields.Hp | ChangedFields.Dead, default, null, null, 0, null, Hp: 0, Dead: true),
                    new ChangeRecord(9, ChangedFields.Mine, default, null, null, 0, null, Mine: true),
                ], [],
                [new HitRecord(42, 7, 13, "crit"), new HitRecord(7, 42, 0, "miss")], new VitalsRecord(38, 50, 9, 11)),
            """{"type":"delta","tick":131,"changed":[{"id":7,"hp":0.12},{"id":8,"hp":0,"dead":true},{"id":9,"mine":true}],"hits":""" +
            """[[42,7,13,"crit"],[7,42,0,"miss"]],"vitals":{"hp":38,"max_hp":50,"sp":9,"max_sp":11}}"""
        },
        { "delta, only hits", new DeltaMessage(6, [], [], [], [new HitRecord(3, 4, 5, "hit")]), """{"type":"delta","tick":6,"hits":[[3,4,5,"hit"]]}""" },
        { "rejected", new RejectedMessage("move", "no_path", new GridCell(30, 2)), """{"type":"rejected","intent":"move","reason":"no_path","cell":[30,2]}""" },
        {
            "rejected attack", new RejectedMessage("attack", "target_gone", null, 99),
            """{"type":"rejected","intent":"attack","reason":"target_gone","target":99}"""
        },
        { "attack_stopped", new AttackStoppedMessage(7, "target_dead"), """{"type":"attack_stopped","target":7,"reason":"target_dead"}""" },
        {
            "exp", new ExpMessage(2, 5, 24, 1, 4, 10),
            """{"type":"exp","base_level":2,"base_exp":5,"base_next":24,"job_level":1,"job_exp":4,"job_next":10}"""
        },
        { "level_up", new LevelUpMessage(3, null), """{"type":"level_up","base_level":3}""" },
        { "level_up, job", new LevelUpMessage(null, 2), """{"type":"level_up","job_level":2}""" },
        { "you_died", new YouDiedMessage(7), """{"type":"you_died","killer":7}""" },
        { "you_died, no killer", new YouDiedMessage(null), """{"type":"you_died"}""" },
        { "respawned", new RespawnedMessage("map.starter_meadow", new GridCell(12, 12), 26), """{"type":"respawned","map":"map.starter_meadow","cell":[12,12],"hp":26}""" },
        { "pong", new PongMessage(7), """{"type":"pong","seq":7}""" },
        { "bye", new ByeMessage("timeout"), """{"type":"bye","reason":"timeout"}""" },
    };

    [Theory]
    [MemberData(nameof(ClientGolden))]
    public void Client_messages_match_golden_json_and_round_trip(string what, ClientMessage message, string json)
    {
        Encoding.UTF8.GetString(ProtocolJson.ToBytes(message)).ShouldBe(json, what);
        var read = ProtocolJson.ReadClient(Encoding.UTF8.GetBytes(json));
        read.Error.ShouldBeNull(what);
        read.Message.ShouldBe(message, what);
    }

    [Theory]
    [MemberData(nameof(ServerGolden))]
    public void Server_messages_match_golden_json_and_round_trip(string what, ServerMessage message, string json)
    {
        Encoding.UTF8.GetString(ProtocolJson.ToBytes(message)).ShouldBe(json, what);
        var read = ProtocolJson.ReadServer(Encoding.UTF8.GetBytes(json));
        read.Error.ShouldBeNull(what);
        Encoding.UTF8.GetString(ProtocolJson.ToBytes(read.Message!)).ShouldBe(json, what); // records hold lists, so compare re-encoded
    }

    [Fact]
    public void Every_message_type_has_a_golden_case()
    {
        ClientGolden.Select(r => ((ClientMessage)r[1]).Type).Concat(ServerGolden.Select(r => ((ServerMessage)r[1]).Type)).Distinct()
            .ShouldBe(MessageTypes.All.Ordered, ignoreOrder: true);
    }

    [Theory]
    [InlineData("", ByeReasons.Malformed)]
    [InlineData("null", ByeReasons.Malformed)]
    [InlineData("{}", ByeReasons.Malformed)]
    [InlineData("""{"type":7}""", ByeReasons.Malformed)]
    [InlineData("""{"type":"move"}""", ByeReasons.Malformed)]
    [InlineData("""{"type":"move","cell":[1]}""", ByeReasons.Malformed)]
    [InlineData("""{"type":"move","cell":[1,2,3]}""", ByeReasons.Malformed)]
    [InlineData("""{"type":"move","cell":[1.5,2]}""", ByeReasons.Malformed)]
    [InlineData("""{"type":"move","cell":[99999999999,2]}""", ByeReasons.Malformed)]
    [InlineData("""{"type":"ping","seq":1} trailing""", ByeReasons.Malformed)]
    [InlineData("""{"type":"ping","seq":1}{"type":"ping","seq":2}""", ByeReasons.Malformed)]
    [InlineData("""{"type":"ping","seq":1,"x":[[[[[[[[[[1]]]]]]]]]]}""", ByeReasons.Malformed)] // nested too deep
    [InlineData("""{"type":"teleport","cell":[1,1]}""", ByeReasons.UnknownType)]
    [InlineData("""{"type":"bye","reason":"x"}""", ByeReasons.UnknownType)]
    [InlineData("""{"type":"hello","version":1}""", ByeReasons.Malformed)]                                  // neither name nor resume
    [InlineData("""{"type":"hello","version":1,"name":"Ada","resume":"Qk3x"}""", ByeReasons.Malformed)]   // both
    [InlineData("""{"type":"hello","version":1,"resume":7}""", ByeReasons.Malformed)]
    [InlineData("""{"type":"attack"}""", ByeReasons.Malformed)]
    [InlineData("""{"type":"attack","target":"7"}""", ByeReasons.Malformed)]
    [InlineData("""{"type":"attack","target":7.5}""", ByeReasons.Malformed)]
    [InlineData("""{"type":"attack","target":null}""", ByeReasons.Malformed)]
    [InlineData("""{"type":"attack","target":[7]}""", ByeReasons.Malformed)]
    [InlineData("""{"type":"attack","target":99999999999999999999}""", ByeReasons.Malformed)]
    [InlineData("""{"type":"attack","target":7""", ByeReasons.Malformed)]
    [InlineData("""{"type":"you_died"}""", ByeReasons.UnknownType)]   // server-to-client types are not accepted from clients
    public void Bad_client_messages_name_their_reason(string json, string reason)
    {
        var read = ProtocolJson.ReadClient(Encoding.UTF8.GetBytes(json));
        read.Message.ShouldBeNull();
        read.Error.ShouldBe(reason);
    }

    [Fact]
    public void Unknown_fields_are_ignored_and_field_order_does_not_matter()
    {
        ProtocolJson.ReadClient("""{"name":"Ada","extra":{"a":[1,2]},"version":1,"type":"hello"}"""u8).Message
            .ShouldBe(new HelloMessage(1, "Ada"));
    }

    [Fact]
    public void Wire_field_bits_match_the_aoi_view_fields()
    {
        foreach (var field in Enum.GetValues<ViewFields>())
            ViewMessageWriter.FieldsOf(field).ToString().ShouldBe(field.ToString());
        Enum.GetValues<EntityKind>().Select(ViewMessageWriter.KindTerm).ShouldBe(EntityKinds.All.Ordered);
    }

    [Fact]
    public void An_item_is_mine_without_an_owner_or_as_its_owner_and_nothing_else_has_the_flag()
    {
        var me = new EntityId(42);
        var item = new EntityView(new EntityId(9), EntityKind.Item, "item.x", new GridCell(1, 1), Directions.S, null, 0, Quantity: 1);
        ViewMessageWriter.Mine(item, me).ShouldBe(true);
        ViewMessageWriter.Mine(item with { LootOwner = me }, me).ShouldBe(true);
        ViewMessageWriter.Mine(item with { LootOwner = new EntityId(43) }, me).ShouldBe(false);
        ViewMessageWriter.Mine(item with { Kind = EntityKind.Monster }, me).ShouldBeNull();
    }

    /// <summary>The tick-thread fast path writes the same bytes as <see cref="ProtocolJson"/> for the same view, over a real walk.</summary>
    [Fact]
    public void View_writer_matches_the_record_writer_for_real_snapshots_and_deltas()
    {
        var rig = new AoiRig();
        var sink = new DualSink();
        var observer = rig.Add(new GridCell(2, 2), kind: EntityKind.Player, sink: sink);
        var walker = rig.Add(new GridCell(4, 2), key: "monster.gel_slime");
        rig.Ticks();
        var leaver = rig.Add(new GridCell(3, 5), kind: EntityKind.Npc, key: "npc.guide");
        rig.Ticks();
        rig.Send(new MoveTo(walker, new GridCell(6, 3))); // stays in view (buckets of 4, radius 1)
        rig.Ticks(6);
        rig.World.Despawn(leaver);
        rig.Ticks(8);

        // Combat on the wire: HP bars, own vitals, hits, a corpse, a ground item whose loot priority runs out.
        var me = rig.Get(observer);
        me.Vitals = new Game.Server.World.Combat.Vitals(me.Stats!);
        var slime = rig.Get(walker);
        var vitals = slime.Vitals = new Game.Server.World.Combat.Vitals(slime.Stats!);
        rig.Ticks();
        vitals.Hp = vitals.MaxHp / 3;
        rig.A.Events.Publish(new DamageDealt(observer, walker, 7, HitResults.Crit, vitals.Hp));
        rig.Ticks();
        vitals.Hp = 0;
        slime.Dead = true;
        rig.A.Events.Publish(new DamageDealt(observer, walker, 3, HitResults.Hit, 0));
        rig.World.Spawn(EntityKind.Item, "map.a", new GridCell(3, 3), Directions.S, e =>
        {
            e.DefinitionId = "item.gel_glob";
            e.GroundItem = new Game.Server.World.Combat.GroundItem("item.gel_glob", 2, walker, new EntityId(77), 0, rig.A.TickNumber * 100 + 300, 60_000);
        });
        rig.Ticks(6);

        sink.Pairs.Count.ShouldBeGreaterThan(9);
        sink.Pairs.ShouldContain(p => p.Fast.Contains("\"changed\"") && p.Fast.Contains("\"next\":null"));
        sink.Pairs.ShouldContain(p => p.Fast.Contains("\"disappeared\""));
        sink.Pairs.ShouldContain(p => p.Fast.Contains("\"hits\":[[1,2,7,\"crit\"]]") && p.Fast.Contains("\"vitals\"") == false);
        sink.Pairs.ShouldContain(p => p.Fast.Contains("\"dead\":true"));
        sink.Pairs.ShouldContain(p => p.Fast.Contains("\"kind\":\"item\"") && p.Fast.Contains("\"mine\":false"));
        sink.Pairs.ShouldContain(p => p.Fast.Contains("\"mine\":true"));
        sink.Pairs.ShouldContain(p => p.Fast.Contains("\"vitals\":{"));
        foreach (var (fast, records) in sink.Pairs) fast.ShouldBe(records);
    }

    private sealed class DualSink : IViewSink
    {
        private readonly ViewMessageWriter _writer = new();
        public readonly List<(string Fast, string Records)> Pairs = new();

        public void OnSnapshot(ViewSnapshot snapshot)
        {
            var message = new SnapshotMessage(snapshot.Tick, snapshot.MapId, snapshot.Observer.Value, snapshot.Entities.Select(v => Record(v, snapshot.Observer)).ToList(),
                Vitals(snapshot.Vitals));
            Pairs.Add((Encoding.UTF8.GetString(_writer.Write(snapshot)), Encoding.UTF8.GetString(ProtocolJson.ToBytes(message))));
        }

        public void OnDelta(ViewDelta delta)
        {
            var message = new DeltaMessage(delta.Tick, delta.Appeared.Select(v => Record(v, delta.Observer)).ToList(),
                delta.Changed.Select(c => new ChangeRecord(c.View.Id.Value, ViewMessageWriter.FieldsOf(c.Fields), c.View.Cell, c.View.Facing, c.View.Next,
                    c.View.Speed, c.View.DisplayKey, ViewMessageWriter.HpFraction(c.View) ?? 0, c.View.Dead, ViewMessageWriter.Mine(c.View, delta.Observer) ?? false)).ToList(),
                delta.Disappeared.Select(id => id.Value).ToList(),
                delta.Hits.Select(h => new HitRecord(h.Attacker.Value, h.Target.Value, h.Amount, h.Result)).ToList(), Vitals(delta.Vitals));
            Pairs.Add((Encoding.UTF8.GetString(_writer.Write(delta)), Encoding.UTF8.GetString(ProtocolJson.ToBytes(message))));
        }

        private static EntityRecord Record(EntityView v, EntityId observer) =>
            new(v.Id.Value, ViewMessageWriter.KindTerm(v.Kind), v.DisplayKey, v.Cell, v.Facing, v.Next, v.Speed, ViewMessageWriter.HpFraction(v), v.Dead,
                v.Quantity, ViewMessageWriter.Mine(v, observer));

        private static VitalsRecord? Vitals(OwnVitals? v) => v is { } own ? new VitalsRecord(own.Hp, own.MaxHp, own.Sp, own.MaxSp) : null;
    }
}
