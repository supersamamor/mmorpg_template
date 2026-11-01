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

    public static TheoryData<string, ClientMessage, string> ClientGolden => new()
    {
        { "hello", new HelloMessage(1, "Ada"), """{"type":"hello","version":1,"name":"Ada"}""" },
        { "hello with content hash", new HelloMessage(1, "Ada", "ab12"), """{"type":"hello","version":1,"name":"Ada","content":"ab12"}""" },
        { "move", new MoveMessage(new GridCell(12, 9)), """{"type":"move","cell":[12,9]}""" },
        { "stop", new StopMessage(), """{"type":"stop"}""" },
        { "ping", new PingMessage(7), """{"type":"ping","seq":7}""" },
    };

    public static TheoryData<string, ServerMessage, string> ServerGolden => new()
    {
        { "welcome", new WelcomeMessage(1, 42, "map.starter_meadow", 10), """{"type":"welcome","version":1,"id":42,"map":"map.starter_meadow","tick_rate":10}""" },
        { "welcome with content hash", new WelcomeMessage(1, 42, "map.starter_meadow", 10, "ab12"), """{"type":"welcome","version":1,"id":42,"map":"map.starter_meadow","tick_rate":10,"content":"ab12"}""" },
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
        { "rejected", new RejectedMessage("move", "no_path", new GridCell(30, 2)), """{"type":"rejected","intent":"move","reason":"no_path","cell":[30,2]}""" },
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

    /// <summary>The tick-thread fast path writes the same bytes as <see cref="ProtocolJson"/> for the same view, over a real walk.</summary>
    [Fact]
    public void View_writer_matches_the_record_writer_for_real_snapshots_and_deltas()
    {
        var rig = new AoiRig();
        var sink = new DualSink();
        rig.Add(new GridCell(2, 2), kind: EntityKind.Player, sink: sink);
        var walker = rig.Add(new GridCell(4, 2), key: "monster.gel_slime");
        rig.Ticks();
        var leaver = rig.Add(new GridCell(3, 5), kind: EntityKind.Npc, key: "npc.guide");
        rig.Ticks();
        rig.Send(new MoveTo(walker, new GridCell(6, 3))); // stays in view (buckets of 4, radius 1)
        rig.Ticks(6);
        rig.World.Despawn(leaver);
        rig.Ticks(8);

        sink.Pairs.Count.ShouldBeGreaterThan(5);
        sink.Pairs.ShouldContain(p => p.Fast.Contains("\"changed\"") && p.Fast.Contains("\"next\":null"));
        sink.Pairs.ShouldContain(p => p.Fast.Contains("\"disappeared\""));
        foreach (var (fast, records) in sink.Pairs) fast.ShouldBe(records);
    }

    private sealed class DualSink : IViewSink
    {
        private readonly ViewMessageWriter _writer = new();
        public readonly List<(string Fast, string Records)> Pairs = new();

        public void OnSnapshot(ViewSnapshot snapshot)
        {
            var message = new SnapshotMessage(snapshot.Tick, snapshot.MapId, snapshot.Observer.Value, snapshot.Entities.Select(Record).ToList());
            Pairs.Add((Encoding.UTF8.GetString(_writer.Write(snapshot)), Encoding.UTF8.GetString(ProtocolJson.ToBytes(message))));
        }

        public void OnDelta(ViewDelta delta)
        {
            var message = new DeltaMessage(delta.Tick, delta.Appeared.Select(Record).ToList(),
                delta.Changed.Select(c => new ChangeRecord(c.View.Id.Value, ViewMessageWriter.FieldsOf(c.Fields), c.View.Cell, c.View.Facing, c.View.Next,
                    c.View.Speed, c.View.DisplayKey)).ToList(),
                delta.Disappeared.Select(id => id.Value).ToList());
            Pairs.Add((Encoding.UTF8.GetString(_writer.Write(delta)), Encoding.UTF8.GetString(ProtocolJson.ToBytes(message))));
        }

        private static EntityRecord Record(EntityView v) =>
            new(v.Id.Value, ViewMessageWriter.KindTerm(v.Kind), v.DisplayKey, v.Cell, v.Facing, v.Next, v.Speed);
    }
}
