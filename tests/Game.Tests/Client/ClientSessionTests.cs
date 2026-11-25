using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;
using Game.Client.Core;
using Game.Client.Core.Bundle;
using Game.Client.Core.Net;
using Game.Content.ClientExport;
using Game.Server.Net;
using Game.Shared.Protocol;
using Game.Shared.Random;
using Game.Shared.Vocabulary;
using Game.Shared.World;
using Game.Tests.Network;
using Game.Tests.Server;
using Game.Tests.World;
using ClientSession = Game.Client.Core.ClientSession;

namespace Game.Tests.Client;

/// <summary>The session with a scripted socket (intents, the marker, the death screen), and real server output through the client's parser and view.</summary>
public class ClientSessionTests
{
    /// <summary>A socket whose events are pushed by the test.</summary>
    private sealed class ScriptedSocket : IClientSocket
    {
        public readonly Queue<SocketEvent> Events = new();
        public readonly List<string> Sent = new();
        public int Opened;
        public int Closed;

        public void Open(string url) => Opened++;

        public void Send(byte[] utf8) => Sent.Add(Encoding.UTF8.GetString(utf8));

        public void Close() => Closed++;

        public bool TryDequeue(out SocketEvent socketEvent) => Events.TryDequeue(out socketEvent);

        public void Push(ServerMessage message) => Events.Enqueue(new SocketEvent(SocketEventKind.Message, message));
    }

    private static DisplayBundle Bundle()
    {
        var file = ClientBundleExporter.Export(TestGrids.SampleContent);
        DisplayBundle.TryRead(file.FileBytes, out var bundle, out _).ShouldBe(BundleStatus.Ok);
        return bundle!;
    }

    private static (ClientSession Session, ScriptedSocket Socket) Playing(params EntityRecord[] entities)
    {
        var socket = new ScriptedSocket();
        var session = new ClientSession(new ClientConfig { AllowCleartext = true }, Bundle(), socket, new Pcg32Random(1));
        session.Login("Ada", 0);
        socket.Events.Enqueue(new SocketEvent(SocketEventKind.Opened));
        socket.Push(new WelcomeMessage(1, 1, "map.starter_meadow", 10, null, "secret-token"));
        var all = new List<EntityRecord> { new(1, EntityKinds.Player, "job.wanderer", new GridCell(5, 5), Directions.N, null, 0) };
        all.AddRange(entities);
        socket.Push(new SnapshotMessage(1, "map.starter_meadow", 1, all, new VitalsRecord(40, 40, 10, 10)));
        session.Update(0, 0);
        session.Connection.Phase.ShouldBe(ConnectionPhase.Online);
        socket.Sent.Clear();
        return (session, socket);
    }

    private static EntityRecord Slime(long id = 7) => new(id, EntityKinds.Monster, "monster.gel_slime", new GridCell(6, 6), Directions.S, null, 0, 1);

    // ---- what goes out ----

    [Fact]
    public void Hello_carries_the_bundle_hash_and_the_protocol_version()
    {
        var socket = new ScriptedSocket();
        var bundle = Bundle();
        var session = new ClientSession(new ClientConfig { AllowCleartext = true }, bundle, socket, new Pcg32Random(1));
        session.Login("Ada", 0);
        socket.Events.Enqueue(new SocketEvent(SocketEventKind.Opened));
        session.Update(0, 0);

        var hello = ProtocolJson.ReadClient(Encoding.UTF8.GetBytes(socket.Sent.Single())).Message.ShouldBeOfType<HelloMessage>();
        hello.Version.ShouldBe(ProtocolVersion.Current);
        hello.Name.ShouldBe("Ada");
        hello.Content.ShouldBe(bundle.ContentHash);
        hello.Resume.ShouldBeNull();
    }

    /// <summary>Answers every account request with a fixed session and one character.</summary>
    private sealed class ScriptedAccounts : Game.Client.Core.Accounts.IAccountTransport
    {
        private Game.Client.Core.Accounts.AccountHttpResponse? _answer;
        public readonly List<string> Calls = new();
        public bool SessionEnded;

        public void Send(Game.Client.Core.Accounts.AccountHttpRequest request)
        {
            Calls.Add(request.Call.ToString());
            var body = SessionEnded ? "{\"error\":\"invalid_session\"}" : request.Call == Game.Client.Core.Accounts.AccountCall.SignIn
                ? "{\"token\":\"account-session\",\"expires_in\":1}"
                : "{\"slots\":3,\"name_min\":3,\"name_max\":16,\"characters\":[{\"id\":\"c1\",\"slot\":0,\"name\":\"Lyra\",\"job\":\"job.wanderer\"}]}";
            _answer = new Game.Client.Core.Accounts.AccountHttpResponse(SessionEnded ? 401 : 200, Encoding.UTF8.GetBytes(body));
        }

        public bool TryDequeue(out Game.Client.Core.Accounts.AccountHttpResponse response)
        {
            response = _answer ?? default;
            var any = _answer is not null;
            _answer = null;
            return any;
        }
    }

    private static (ClientSession Session, ScriptedSocket Socket, ScriptedAccounts Accounts) SignedIn()
    {
        var socket = new ScriptedSocket();
        var accounts = new ScriptedAccounts();
        var session = new ClientSession(new ClientConfig { AllowCleartext = true }, Bundle(), socket, new Pcg32Random(1), accounts);
        session.Play("c1", 0);
        session.Connection.Phase.ShouldBe(ConnectionPhase.LoginScreen, "nobody is signed in yet");
        session.Accounts.SignIn("ada", "secret-password");
        session.Update(0, 0);
        session.Update(0, 0);
        session.Accounts.Characters.Single().Id.ShouldBe("c1");
        session.Play("c1", 0);
        socket.Events.Enqueue(new SocketEvent(SocketEventKind.Opened));
        session.Update(0, 0);
        return (session, socket, accounts);
    }

    [Fact]
    public void Playing_a_character_says_hello_with_the_session_and_the_character_and_no_name()
    {
        var (_, socket, _) = SignedIn();
        var hello = ProtocolJson.ReadClient(Encoding.UTF8.GetBytes(socket.Sent.Single())).Message.ShouldBeOfType<HelloMessage>();
        (hello.Name, hello.Resume, hello.Session, hello.Character).ShouldBe((null, null, "account-session", "c1"));
        hello.Content.ShouldNotBeNull();
    }

    [Fact]
    public void A_server_restart_signs_the_character_in_again_with_the_same_session()
    {
        var (session, socket, _) = SignedIn();
        socket.Push(new WelcomeMessage(1, 1, "map.starter_meadow", 10, null, "resume-token"));
        socket.Push(new ByeMessage(ByeReasons.Shutdown));
        session.Update(1, 1);
        session.Update(100, 100);
        socket.Events.Enqueue(new SocketEvent(SocketEventKind.Opened));
        session.Update(100, 100);
        var hello = ProtocolJson.ReadClient(Encoding.UTF8.GetBytes(socket.Sent.Last())).Message.ShouldBeOfType<HelloMessage>();
        (hello.Resume, hello.Session, hello.Character).ShouldBe((null, "account-session", "c1"));
    }

    [Fact]
    public void Logging_out_leads_back_to_the_character_list_which_is_asked_for_again()
    {
        var (session, socket, accounts) = SignedIn();
        socket.Push(new WelcomeMessage(1, 1, "map.starter_meadow", 10, null, "resume-token"));
        session.Update(1, 1);
        accounts.Calls.Clear();
        session.Logout(2);
        socket.Push(new ByeMessage(ByeReasons.Logout));
        session.Update(2, 2);
        session.Update(2, 2);
        session.Connection.Phase.ShouldBe(ConnectionPhase.LoginScreen);
        accounts.Calls.ShouldBe(["List"]);
        session.Accounts.Screen.ShouldBe(Game.Client.Core.Accounts.AccountScreen.Characters);
    }

    [Fact]
    public void A_refused_session_leads_to_the_sign_in_screen()
    {
        var (session, socket, accounts) = SignedIn();
        accounts.SessionEnded = true;
        socket.Push(new ByeMessage(ByeReasons.LoginRefused));
        session.Update(1, 1);
        session.Update(1, 1);
        session.Accounts.SignedIn.ShouldBeFalse();
        session.Accounts.Screen.ShouldBe(Game.Client.Core.Accounts.AccountScreen.SignIn);
    }

    [Fact]
    public void A_replaced_player_goes_back_to_the_login_screen_on_request_only()
    {
        var (session, socket, _) = SignedIn();
        socket.Push(new ByeMessage(ByeReasons.Replaced));
        session.Update(1, 1);
        session.Connection.Phase.ShouldBe(ConnectionPhase.Stopped);
        session.BackToLogin();
        session.Connection.Phase.ShouldBe(ConnectionPhase.LoginScreen);
        session.Accounts.SignedIn.ShouldBeTrue();
    }

    [Fact]
    public void A_resume_says_hello_with_the_token_the_hash_and_no_name()
    {
        var (session, socket) = Playing();
        socket.Events.Enqueue(new SocketEvent(SocketEventKind.Closed));
        session.Update(1, 1);
        session.Update(10, 10);
        socket.Opened.ShouldBe(2);
        socket.Events.Enqueue(new SocketEvent(SocketEventKind.Opened));
        session.Update(10, 10);

        var hello = ProtocolJson.ReadClient(Encoding.UTF8.GetBytes(socket.Sent.Last())).Message.ShouldBeOfType<HelloMessage>();
        hello.Resume.ShouldBe("secret-token");
        hello.Name.ShouldBeNull();
        hello.Content.ShouldNotBeNull();
    }

    [Fact]
    public void A_tap_on_the_ground_is_a_move_with_a_marker_at_once()
    {
        var (session, socket) = Playing();
        session.TapGround(new GridCell(8, 9), 1);
        socket.Sent.Single().ShouldBe("""{"type":"move","cell":[8,9]}""");
        session.Ui.Marker!.Value.X.ShouldBe(8);
    }

    [Fact]
    public void The_client_asks_even_for_a_blocked_cell_and_the_server_answers()
    {
        var (session, socket) = Playing();
        session.TapGround(new GridCell(0, 0), 1);       // a wall on this map: not the client's call
        socket.Sent.Count.ShouldBe(1);
        session.Ui.Marker.ShouldNotBeNull();

        socket.Push(new RejectedMessage(MessageTypes.Move, "not_walkable", new GridCell(0, 0)));
        session.Update(1, 1);
        session.Ui.Marker.ShouldBeNull();
        session.Ui.Flash.ShouldBeEmpty(); // quietly
    }

    [Fact]
    public void A_rejected_older_move_leaves_the_newer_marker()
    {
        var (session, socket) = Playing();
        session.TapGround(new GridCell(1, 1), 1);
        session.TapGround(new GridCell(2, 2), 1);
        socket.Push(new RejectedMessage(MessageTypes.Move, "no_path", new GridCell(1, 1)));
        session.Update(1, 1);
        session.Ui.Marker!.Value.X.ShouldBe(2);
    }

    [Fact]
    public void A_tap_on_a_monster_is_an_attack_and_a_rejection_removes_the_highlight()
    {
        var (session, socket) = Playing(Slime());
        session.TapEntity(7);
        socket.Sent.Single().ShouldBe("""{"type":"attack","target":7}""");
        session.Ui.Target.ShouldBe(7);

        socket.Push(new RejectedMessage(MessageTypes.Attack, AttackReasons.SafeZone, null, 7));
        session.Update(1, 1);
        session.Ui.Target.ShouldBeNull();
    }

    [Fact]
    public void Attack_stopped_and_leaving_view_remove_the_highlight()
    {
        var (session, socket) = Playing(Slime(), Slime(8));
        session.TapEntity(7);
        socket.Push(new AttackStoppedMessage(7, AttackReasons.TargetDead));
        session.Update(1, 1);
        session.Ui.Target.ShouldBeNull();

        session.TapEntity(8);
        socket.Push(new DeltaMessage(2, [], [], [8]));
        session.Update(1, 1);
        session.Ui.Target.ShouldBeNull();
    }

    [Fact]
    public void The_marker_ends_when_the_walk_arrives()
    {
        var (session, socket) = Playing();
        session.TapGround(new GridCell(6, 5), 1);
        socket.Push(new DeltaMessage(2, [], [new ChangeRecord(1, ChangedFields.Next | ChangedFields.Speed, default, null, new GridCell(6, 5), 4, null)], []));
        session.Update(1, 1);
        session.Ui.Marker.ShouldNotBeNull();
        socket.Push(new DeltaMessage(3, [], [new ChangeRecord(1, ChangedFields.Cell | ChangedFields.Next | ChangedFields.Speed, new GridCell(6, 5), null, null, 0, null)], []));
        session.Update(5, 5);
        session.Ui.Marker.ShouldBeNull();
    }

    [Fact]
    public void Nothing_is_sent_while_offline_or_dead()
    {
        var socket = new ScriptedSocket();
        var session = new ClientSession(new ClientConfig { AllowCleartext = true }, Bundle(), socket, new Pcg32Random(1));
        session.TapGround(new GridCell(1, 1), 0);
        session.TapEntity(7);
        session.Respawn();
        socket.Sent.ShouldBeEmpty();

        var (playing, playingSocket) = Playing(Slime());
        playingSocket.Push(new YouDiedMessage(7));
        playing.Update(1, 1);
        playing.TapGround(new GridCell(1, 1), 1);
        playing.TapEntity(7);
        playingSocket.Sent.ShouldBeEmpty();
    }

    // ---- the death screen ----

    [Fact]
    public void You_died_shows_the_death_screen_and_respawned_hides_it()
    {
        var (session, socket) = Playing();
        socket.Push(new YouDiedMessage(7));
        session.Update(1, 1);
        session.Ui.Dead.ShouldBeTrue();

        session.Respawn();
        socket.Sent.Single().ShouldBe("""{"type":"respawn"}""");
        socket.Push(new RespawnedMessage("map.starter_meadow", new GridCell(12, 12), 20));
        session.Update(2, 2);
        session.Ui.Dead.ShouldBeFalse();
    }

    [Fact]
    public void A_snapshot_after_a_resume_that_shows_the_player_dead_shows_the_death_screen()
    {
        var (session, socket) = Playing();
        session.Ui.Dead.ShouldBeFalse();
        // No you_died: that went to the old socket (ADR-002 row 102).
        socket.Push(new SnapshotMessage(9, "map.starter_meadow", 1,
            [new EntityRecord(1, EntityKinds.Player, "job.wanderer", new GridCell(5, 5), Directions.N, null, 0, 0, Dead: true)], new VitalsRecord(0, 40, 3, 10)));
        session.Update(1, 1);
        session.Ui.Dead.ShouldBeTrue();
    }

    [Fact]
    public void Vitals_above_zero_end_the_death_screen()
    {
        var (session, socket) = Playing();
        socket.Push(new YouDiedMessage(null));
        session.Update(1, 1);
        socket.Push(new SnapshotMessage(9, "map.starter_meadow", 1, [], new VitalsRecord(20, 40, 3, 10)));
        session.Update(1, 1);
        session.Ui.Dead.ShouldBeFalse();
    }

    // ---- the flash ----

    [Fact]
    public void Hits_name_things_from_the_bundle()
    {
        var bundle = Bundle();
        var (session, socket) = Playing(Slime());
        socket.Push(new DeltaMessage(2, [], [], [], [new HitRecord(1, 7, 13, HitResults.Hit)]));
        session.Update(1, 1);
        session.Ui.Flash.ShouldContain(bundle.NameOf("monster.gel_slime"));
        session.Ui.Flash.ShouldContain("13");

        socket.Push(new DeltaMessage(3, [], [], [], [new HitRecord(7, 1, 5, HitResults.Miss)]));
        session.Update(1, 1);
        session.Ui.Flash.ShouldContain("misses");

        session.Update(100, 100);
        session.Ui.Flash.ShouldBeEmpty();
    }

    [Fact]
    public void A_hit_by_someone_else_flashes_nothing()
    {
        var (session, socket) = Playing(Slime(), Slime(8));
        socket.Push(new DeltaMessage(2, [], [], [], [new HitRecord(7, 8, 1, HitResults.Hit)]));
        session.Update(1, 1);
        session.Ui.Flash.ShouldBeEmpty();
    }

    [Fact]
    public void A_drop_this_client_may_take_flashes_its_name()
    {
        var (session, socket) = Playing();
        socket.Push(new DeltaMessage(2, [new EntityRecord(9, EntityKinds.Item, "item.gel_glob", new GridCell(5, 6), Directions.S, null, 0, Quantity: 2, Mine: true)], [], []));
        session.Update(1, 1);
        session.Ui.Flash.ShouldContain(Bundle().NameOf("item.gel_glob"));
    }

    // ---- bad input ----

    [Fact]
    public void A_message_of_an_unknown_type_is_ignored_and_a_malformed_one_counts_as_a_drop()
    {
        InboundParser.Parse("""{"type":"from_the_future","x":1}"""u8).Kind.ShouldBe(SocketEventKind.UnknownType);
        InboundParser.Parse("""{"type":"snapshot"}"""u8).Kind.ShouldBe(SocketEventKind.Malformed);
        InboundParser.Parse("not json"u8).Kind.ShouldBe(SocketEventKind.Malformed);

        var (session, socket) = Playing();
        socket.Events.Enqueue(InboundParser.Parse("""{"type":"from_the_future"}"""u8));
        session.Update(1, 1);
        session.Connection.Phase.ShouldBe(ConnectionPhase.Online);

        socket.Events.Enqueue(InboundParser.Parse("{"u8));
        session.Update(2, 2);
        session.Connection.Phase.ShouldBe(ConnectionPhase.Backoff);
        session.Connection.HasToken.ShouldBeTrue();
    }

    [Fact]
    public void A_close_frame_stands_in_for_a_bye_the_socket_dropped()
    {
        foreach (var reason in ByeReasons.All.Ordered) InboundParser.ByeFromClose(reason)!.Reason.ShouldBe(reason);
        InboundParser.ByeFromClose(null).ShouldBeNull();
        InboundParser.ByeFromClose("").ShouldBeNull();
        InboundParser.ByeFromClose("going away").ShouldBeNull();

        // The text bye and then the close frame's copy of it change nothing twice.
        var (session, socket) = Playing();
        socket.Push(new ByeMessage(ByeReasons.Replaced));
        socket.Push(new ByeMessage(ByeReasons.Replaced));
        socket.Events.Enqueue(new SocketEvent(SocketEventKind.Closed));
        session.Update(1, 1);
        session.Connection.Phase.ShouldBe(ConnectionPhase.Stopped);
        session.Connection.Notice.ShouldBe(ConnectionNotice.Replaced);
    }

    // ---- golden parse of real server messages ----

    public static TheoryData<string, string> RealFrames => new()
    {
        // Frames as the running server wrote them (T-206's live run), plus the §10 examples.
        { "welcome", """{"type":"welcome","version":1,"id":31,"map":"map.starter_meadow","tick_rate":10,"content":"444fd1f0","token":"Qk3x-_9"}""" },
        { "snapshot", """{"type":"snapshot","tick":120,"map":"map.starter_meadow","id":31,"entities":[{"id":7,"kind":"monster","display_key":"monster.gel_slime","cell":[3,4],"facing":"e","next":[4,4],"speed":3.51,"hp":0.37},{"id":9,"kind":"item","display_key":"item.gel_glob","cell":[4,5],"facing":"s","quantity":2,"mine":true},{"id":31,"kind":"player","display_key":"job.wanderer","cell":[12,12],"facing":"n","hp":1}],"vitals":{"hp":48,"max_hp":48,"sp":11,"max_sp":11}}""" },
        { "delta", """{"type":"delta","tick":121,"appeared":[{"id":8,"kind":"monster","display_key":"monster.gel_slime","cell":[4,4],"facing":"w","hp":0,"dead":true}],"changed":[{"id":7,"cell":[4,4],"facing":"e","next":[5,4]},{"id":31,"next":null,"speed":0,"hp":0.5,"dead":false,"mine":false}],"disappeared":[9],"hits":[[31,7,13,"crit"]],"vitals":{"hp":40,"max_hp":48,"sp":11,"max_sp":11}}""" },
        { "rejected", """{"type":"rejected","intent":"move","reason":"no_path","cell":[30,2]}""" },
        { "attack_stopped", """{"type":"attack_stopped","target":7,"reason":"target_dead"}""" },
        { "exp", """{"type":"exp","base_level":1,"base_exp":6,"base_next":65,"job_level":1,"job_exp":4,"job_next":10}""" },
        { "level_up", """{"type":"level_up","base_level":2}""" },
        { "you_died", """{"type":"you_died","killer":7}""" },
        { "respawned", """{"type":"respawned","map":"map.starter_meadow","cell":[12,12],"hp":24}""" },
        { "pong", """{"type":"pong","seq":7}""" },
        { "bye", """{"type":"bye","reason":"logout"}""" },
    };

    [Theory]
    [MemberData(nameof(RealFrames))]
    public void A_real_frame_parses_and_says_the_same_when_written_again(string type, string frame)
    {
        var parsed = InboundParser.Parse(Encoding.UTF8.GetBytes(frame));
        parsed.Kind.ShouldBe(SocketEventKind.Message);
        parsed.Message!.Type.ShouldBe(type);
    }

    [Fact]
    public void Unknown_fields_are_ignored_at_every_level()
    {
        var parsed = InboundParser.Parse("""{"type":"delta","new":{"a":[1,2]},"tick":5,"changed":[{"id":7,"glow":"red","cell":[1,2]}],"appeared":[{"id":8,"kind":"npc","cell":[1,1],"facing":"n","aura":[1]}]}"""u8);
        var delta = parsed.Message.ShouldBeOfType<DeltaMessage>();
        delta.Tick.ShouldBe(5);
        delta.Changed.Single().Cell.Y.ShouldBe(2);
        delta.Appeared.Single().Kind.ShouldBe(EntityKinds.Npc);
    }

    [Fact]
    public void Next_null_parses_as_stopped_not_as_absent()
    {
        var parsed = InboundParser.Parse("""{"type":"delta","tick":1,"changed":[{"id":7,"next":null}]}"""u8);
        var change = parsed.Message.ShouldBeOfType<DeltaMessage>().Changed.Single();
        (change.Fields & ChangedFields.Next).ShouldBe(ChangedFields.Next);
        change.Next.ShouldBeNull();
    }

    /// <summary>A socket on <see cref="ClientWebSocket"/>, reading frames as the Godot one does: parsed off the main thread.</summary>
    private sealed class LoopbackSocket : IClientSocket, IDisposable
    {
        private readonly ConcurrentQueue<SocketEvent> _events = new();
        private readonly ConcurrentQueue<string> _frames = new();
        private ClientWebSocket? _socket;

        public IEnumerable<string> Frames => _frames;

        public void Open(string url)
        {
            var socket = new ClientWebSocket();
            _socket = socket;
            _ = Task.Run(async () =>
            {
                try
                {
                    await socket.ConnectAsync(new Uri(url), CancellationToken.None);
                    _events.Enqueue(new SocketEvent(SocketEventKind.Opened));
                    var buffer = new byte[1 << 20];
                    while (true)
                    {
                        var count = 0;
                        ValueWebSocketReceiveResult result;
                        do
                        {
                            result = await socket.ReceiveAsync(buffer.AsMemory(count), CancellationToken.None);
                            count += result.Count;
                        } while (!result.EndOfMessage);
                        if (result.MessageType == WebSocketMessageType.Close) break;
                        _frames.Enqueue(Encoding.UTF8.GetString(buffer, 0, count));
                        _events.Enqueue(InboundParser.Parse(buffer.AsSpan(0, count)));
                    }
                }
                catch (Exception e) when (e is WebSocketException or ObjectDisposedException or OperationCanceledException)
                {
                }
                _events.Enqueue(new SocketEvent(SocketEventKind.Closed));
            });
        }

        public void Send(byte[] utf8) => _socket?.SendAsync(utf8, WebSocketMessageType.Text, true, CancellationToken.None).GetAwaiter().GetResult();

        public void Close()
        {
            _socket?.Abort();
            _socket = null;
        }

        public void Kill() => _socket?.Abort();

        public bool TryDequeue(out SocketEvent socketEvent) => _events.TryDequeue(out socketEvent);

        public void Dispose() => _socket?.Dispose();
    }

    [Fact]
    public async Task The_client_core_plays_against_the_real_gateway_and_resumes()
    {
        await using var rig = await GatewayRig.StartAsync();
        using var socket = new LoopbackSocket();
        var config = new ClientConfig { ServerUrl = rig.Endpoint.ToString(), AllowCleartext = true };
        DisplayBundle.TryRead(ClientBundleExporter.Export(rig.Content).FileBytes, out var bundle, out _).ShouldBe(BundleStatus.Ok);
        var session = new ClientSession(config, bundle!, socket, new Pcg32Random(1));
        var clock = System.Diagnostics.Stopwatch.StartNew();
        double Now() => clock.Elapsed.TotalSeconds;

        async Task Until(Func<bool> condition, string what)
        {
            await Poll.UntilAsync(() =>
            {
                session.Update(Now(), Now());
                return condition();
            }, done => done, what, 15_000);
        }

        session.Login("Tester", Now());
        await Until(() => session.Connection.Phase == ConnectionPhase.Online && session.World.Own is not null, "welcome and snapshot");
        var id = session.World.OwnId;
        var firstToken = session.Connection.ResumeToken;
        firstToken.ShouldNotBeNull();
        session.World.MapId.ShouldBe(rig.Start.MapId);
        session.World.Vitals.ShouldNotBeNull();
        bundle!.TryGetMap(session.World.MapId!, out var map).ShouldBeTrue();
        await Until(() => session.World.Exp is not null, "the exp message (it follows the snapshot)");

        var start = session.World.Own!.Cell;
        var target = rig.NearStart((3, 0), (0, 3), (-3, 0));
        session.TapGround(target, Now());
        await Until(() => session.World.Own!.Cell.X == target.X && session.World.Own!.Cell.Y == target.Y && !session.World.Own!.IsWalking, "the walk");
        session.World.Own!.X.ShouldBe(target.X + 0.5);
        (start.X != target.X || start.Y != target.Y).ShouldBeTrue();

        session.TapGround(new GridCell(0, 0), Now()); // a wall
        await Until(() => session.Ui.Marker is null, "the rejection");

        socket.Kill();
        await Until(() => session.Connection.Phase == ConnectionPhase.Backoff, "noticing the drop");
        await Until(() => session.Connection.Phase == ConnectionPhase.Online && session.World.Own is not null, "the resume");
        session.World.OwnId.ShouldBe(id);
        session.Connection.ResumeToken.ShouldNotBe(firstToken);

        session.Logout(Now());
        await Until(() => session.Connection.Phase == ConnectionPhase.LoginScreen, "bye logout");
        session.Connection.Notice.ShouldBe(ConnectionNotice.LoggedOut);

        // Every frame the real server sent parsed: nothing on this path was unknown or malformed.
        socket.Frames.ShouldNotBeEmpty();
        foreach (var frame in socket.Frames)
            InboundParser.Parse(Encoding.UTF8.GetBytes(frame)).Kind.ShouldBe(SocketEventKind.Message, frame);
    }
}
