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
        { "pick_up", new PickUpMessage(9), """{"type":"pick_up","target":9}""" },
        { "use_item", new UseItemMessage(12), """{"type":"use_item","entry":12}""" },
        { "equip", new EquipMessage(12), """{"type":"equip","entry":12}""" },
        { "equip into a slot", new EquipMessage(12, "accessory_2"), """{"type":"equip","entry":12,"slot":"accessory_2"}""" },
        { "unequip", new UnequipMessage("weapon"), """{"type":"unequip","slot":"weapon"}""" },
        { "drop_item", new DropItemMessage(12, 2), """{"type":"drop_item","entry":12,"quantity":2}""" },
        { "insert_card", new InsertCardMessage(7, 12), """{"type":"insert_card","entry":7,"card":12}""" },
        { "refine", new RefineMessage(7), """{"type":"refine","entry":7}""" },
        { "use_skill on an entity", new UseSkillMessage("skill.twin_strike", null, 7), """{"type":"use_skill","skill":"skill.twin_strike","target":7}""" },
        { "use_skill on the ground at a level", new UseSkillMessage("skill.mana_font", 2, null, new GridCell(5, 6)), """{"type":"use_skill","skill":"skill.mana_font","level":2,"cell":[5,6]}""" },
        { "learn_skill", new LearnSkillMessage("skill.twin_strike"), """{"type":"learn_skill","skill":"skill.twin_strike"}""" },
        { "change_job", new ChangeJobMessage("job.spellbinder"), """{"type":"change_job","job":"job.spellbinder"}""" },
        { "raise_stat", new RaiseStatMessage("str", 2), """{"type":"raise_stat","stat":"str","amount":2}""" },
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
        {
            "delta with casts: a cast bar opens and one closes",
            new DeltaMessage(7, [], [], [], null, null, [new CastRecord(7, "skill.ember_bolt", 993)], [new CastEndRecord(9, "skill.venom_sting", "done")]),
            """{"type":"delta","tick":7,"casts":[[7,"skill.ember_bolt",993]],"cast_ends":[[9,"skill.venom_sting","done"]]}"""
        },
        {
            "skills", new SkillsMessage("job.wanderer", 3, [new SkillLevelRecord("skill.second_wind", 2)], [new SkillCooldownRecord("skill.second_wind", 4000)], ["job.spellbinder"]),
            """{"type":"skills","job":"job.wanderer","points":3,"levels":[["skill.second_wind",2]],"cooldowns":[["skill.second_wind",4000]],"jobs":["job.spellbinder"]}"""
        },
        { "skills, nothing yet", new SkillsMessage("job.wanderer", 0, [], [], []), """{"type":"skills","job":"job.wanderer","points":0,"levels":[],"cooldowns":[],"jobs":[]}""" },
        { "cooldown", new CooldownMessage("skill.twin_strike", 1500), """{"type":"cooldown","skill":"skill.twin_strike","ms":1500}""" },
        {
            "stats", new StatsMessage(4, [new StatRecord("str", 3, 2), new StatRecord("luk", 99, 0)], [new DerivedRecord("atk", 12), new DerivedRecord("max_hp", 55)]),
            """{"type":"stats","points":4,"stats":[["str",3,2],["luk",99,0]],"derived":[["atk",12],["max_hp",55]]}"""
        },
        { "stats, nothing yet", new StatsMessage(0, [], []), """{"type":"stats","points":0,"stats":[],"derived":[]}""" },
        {
            "rejected raise_stat", new RejectedMessage(MessageTypes.RaiseStat, StatReasons.NotEnoughPoints, null, Stat: "str"),
            """{"type":"rejected","intent":"raise_stat","reason":"not_enough_points","stat":"str"}"""
        },
        {
            "rejected skill intent", new RejectedMessage("use_skill", "on_cooldown", null, 7, Skill: "skill.twin_strike"),
            """{"type":"rejected","intent":"use_skill","reason":"on_cooldown","target":7,"skill":"skill.twin_strike"}"""
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
        {
            "rejected item intent", new RejectedMessage("equip", "level_too_low", null, null, 12, "weapon"),
            """{"type":"rejected","intent":"equip","reason":"level_too_low","entry":12,"slot":"weapon"}"""
        },
        {
            "players with a weapon and worn items",
            new SnapshotMessage(140, "map.starter_meadow", 42, [Standing with { Weapon = "dagger", Worn = ["item.bone_dirk", "item.leather_cap"] }]),
            """{"type":"snapshot","tick":140,"map":"map.starter_meadow","id":42,"entities":[""" +
            """{"id":42,"kind":"player","cell":[12,12],"facing":"n","weapon":"dagger","worn":["item.bone_dirk","item.leather_cap"]}]}"""
        },
        {
            "delta: a player took its weapon off",
            new DeltaMessage(141, [], [new ChangeRecord(42, ChangedFields.Look, default, null, null, 0, null, Weapon: "unarmed", Worn: [])], []),
            """{"type":"delta","tick":141,"changed":[{"id":42,"weapon":"unarmed","worn":[]}]}"""
        },
        {
            "inventory", new InventoryMessage(4, 100, 45, 2040, "normal", [new InventoryEntryRecord(1, "item.bone_dirk", 1, "weapon"), new InventoryEntryRecord(2, "item.healing_draught", 3)]),
            """{"type":"inventory","rev":4,"slots":100,"weight":45,"weight_limit":2040,"load":"normal","entries":[""" +
            """{"id":1,"item":"item.bone_dirk","quantity":1,"slot":"weapon"},{"id":2,"item":"item.healing_draught","quantity":3}]}"""
        },
        {
            "inventory_changed", new InventoryChangedMessage(5, "used", "item.healing_draught", 40, 2040, "normal", [new InventoryEntryRecord(2, "item.healing_draught", 2)], [7]),
            """{"type":"inventory_changed","rev":5,"cause":"used","item":"item.healing_draught","weight":40,"weight_limit":2040,"load":"normal",""" +
            "\"entries\":[{\"id\":2,\"item\":\"item.healing_draught\",\"quantity\":2}],\"removed\":[7]}"
        },
        {
            "inventory_changed, weight only", new InventoryChangedMessage(6, "weight", null, 40, 2070, "normal", [], []),
            """{"type":"inventory_changed","rev":6,"cause":"weight","weight":40,"weight_limit":2070,"load":"normal"}"""
        },
        {
            "inventory with a socketed, refined weapon (owner only)",
            new InventoryMessage(8, 100, 45, 2040, "normal", [new InventoryEntryRecord(1, "item.iron_blade", 1, "weapon", ["item.gelcap_card"], 3)]),
            """{"type":"inventory","rev":8,"slots":100,"weight":45,"weight_limit":2040,"load":"normal","entries":[""" +
            """{"id":1,"item":"item.iron_blade","quantity":1,"slot":"weapon","cards":["item.gelcap_card"],"refine":3}]}"""
        },
        {
            "inventory_changed after a refine", new InventoryChangedMessage(9, "refined", "item.padded_vest", 43, 2040, "normal",
                [new InventoryEntryRecord(3, "item.padded_vest", 1, null, null, 2)], [5]),
            """{"type":"inventory_changed","rev":9,"cause":"refined","item":"item.padded_vest","weight":43,"weight_limit":2040,"load":"normal",""" +
            "\"entries\":[{\"id\":3,\"item\":\"item.padded_vest\",\"quantity\":1,\"refine\":2}],\"removed\":[5]}"
        },
        {
            "refine_result", new RefineResultMessage(3, "item.padded_vest", "success", 2),
            """{"type":"refine_result","entry":3,"item":"item.padded_vest","result":"success","refine":2}"""
        },
        {
            "rejected insert_card", new RejectedMessage("insert_card", "card_does_not_fit", null, null, 3),
            """{"type":"rejected","intent":"insert_card","reason":"card_does_not_fit","entry":3}"""
        },
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
    [InlineData("""{"type":"insert_card","entry":7}""", ByeReasons.Malformed)]            // no card
    [InlineData("""{"type":"insert_card","card":12}""", ByeReasons.Malformed)]            // no equipment
    [InlineData("""{"type":"insert_card","entry":7,"card":"12"}""", ByeReasons.Malformed)]
    [InlineData("""{"type":"refine"}""", ByeReasons.Malformed)]
    [InlineData("""{"type":"refine","entry":1.5}""", ByeReasons.Malformed)]
    [InlineData("""{"type":"refine_result","entry":1,"item":"item.x","result":"success","refine":1}""", ByeReasons.UnknownType)]
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
