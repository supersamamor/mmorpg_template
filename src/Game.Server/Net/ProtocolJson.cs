using System.Buffers;
using System.Text.Json;
using Game.Shared.Protocol;
using Game.Shared.World;

namespace Game.Server.Net;

/// <summary>Outcome of reading one message: the message, or the <see cref="ByeReasons"/> term that explains why it could not be read.</summary>
public readonly record struct ReadResult<T>(T? Message, string? Error) where T : class
{
    /// <summary>A message was read.</summary>
    public bool Ok => Message is not null;
}

/// <summary>
/// The JSON mapping of every <see cref="Game.Shared.Protocol"/> message (ARCHITECTURE §10), both directions, written by hand on
/// <see cref="Utf8JsonReader"/>/<see cref="Utf8JsonWriter"/>: no reflection and no source generator, so it is AOT-safe as is. It depends only
/// on <c>Game.Shared</c> and <c>System.Text.Json</c>, so the Godot client (T-206) can compile this same file (linked, with the
/// System.Text.Json package on netstandard2.1) instead of keeping a second copy. Field names are snake_case VOCABULARY terms; cells are
/// <c>[x,y]</c>; speeds are rounded to 0.01. Unknown fields are ignored, so a newer peer can add fields within a protocol version.
/// The gateway writes snapshots and deltas straight from the AOI's objects (<see cref="ViewMessageWriter"/>); the golden tests check
/// that both writers give the same bytes.
/// </summary>
public static class ProtocolJson
{
    /// <summary>Deepest nesting a client message may have (objects or arrays inside unknown fields included).</summary>
    public const int MaxClientDepth = 8;

    internal static readonly JsonWriterOptions WriterOptions = new() { SkipValidation = true };

    // ---- client → server ----

    /// <summary>Reads a client message. Errors: <see cref="ByeReasons.Malformed"/> or <see cref="ByeReasons.UnknownType"/>.</summary>
    public static ReadResult<ClientMessage> ReadClient(ReadOnlySpan<byte> utf8)
    {
        try
        {
            var reader = new Utf8JsonReader(utf8, new JsonReaderOptions { MaxDepth = MaxClientDepth });
            if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject) return Fail<ClientMessage>(ByeReasons.Malformed);
            string? type = null, name = null, content = null, resume = null;
            int? version = null;
            long? seq = null, target = null;
            GridCell? cell = null;
            while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
            {
                var key = reader.GetString();
                reader.Read();
                switch (key)
                {
                    case "type": type = String(ref reader); break;
                    case "version": version = reader.TokenType == JsonTokenType.Number && reader.TryGetInt32(out var v) ? v : throw Bad(); break;
                    case "name": name = String(ref reader); break;
                    case "content": content = String(ref reader); break;
                    case "resume": resume = String(ref reader); break;
                    case "seq": seq = reader.TokenType == JsonTokenType.Number && reader.TryGetInt64(out var s) ? s : throw Bad(); break;
                    case "target": target = reader.TokenType == JsonTokenType.Number && reader.TryGetInt64(out var t) ? t : throw Bad(); break;
                    case "cell": cell = Cell(ref reader); break;
                    default: reader.Skip(); break;
                }
            }
            if (reader.TokenType != JsonTokenType.EndObject || reader.Read()) return Fail<ClientMessage>(ByeReasons.Malformed);

            ClientMessage? message = type switch
            {
                // exactly one of name (a new login) and resume (a resume token)
                MessageTypes.Hello => version is { } ver && (name is null) != (resume is null) ? new HelloMessage(ver, name, content, resume) : null,
                MessageTypes.Move => cell is { } c ? new MoveMessage(c) : null,
                MessageTypes.Stop => new StopMessage(),
                MessageTypes.Attack => target is { } id ? new AttackMessage(id) : null,
                MessageTypes.StopAttack => new StopAttackMessage(),
                MessageTypes.Respawn => new RespawnMessage(),
                MessageTypes.Logout => new LogoutMessage(),
                MessageTypes.Ping => seq is { } n ? new PingMessage(n) : null,
                null => null,
                _ => throw new UnknownTypeException(),
            };
            return message is null ? Fail<ClientMessage>(ByeReasons.Malformed) : new ReadResult<ClientMessage>(message, null);
        }
        catch (UnknownTypeException)
        {
            return Fail<ClientMessage>(ByeReasons.UnknownType);
        }
        catch (JsonException)
        {
            return Fail<ClientMessage>(ByeReasons.Malformed);
        }
        catch (InvalidOperationException)
        {
            return Fail<ClientMessage>(ByeReasons.Malformed); // a token of the wrong kind for its field
        }
    }

    /// <summary>Writes a client message (what the client sends).</summary>
    public static void Write(IBufferWriter<byte> output, ClientMessage message)
    {
        using var w = new Utf8JsonWriter(output, WriterOptions);
        w.WriteStartObject();
        w.WriteString("type", message.Type);
        switch (message)
        {
            case HelloMessage hello:
                w.WriteNumber("version", hello.Version);
                if (hello.Name is not null) w.WriteString("name", hello.Name);
                if (hello.Resume is not null) w.WriteString("resume", hello.Resume);
                if (hello.Content is not null) w.WriteString("content", hello.Content);
                break;
            case MoveMessage move:
                WriteCell(w, "cell", move.Cell);
                break;
            case AttackMessage attack:
                w.WriteNumber("target", attack.Target);
                break;
            case PingMessage ping:
                w.WriteNumber("seq", ping.Seq);
                break;
        }
        w.WriteEndObject();
    }

    // ---- server → client ----

    /// <summary>Writes a server message.</summary>
    public static void Write(IBufferWriter<byte> output, ServerMessage message)
    {
        using var w = new Utf8JsonWriter(output, WriterOptions);
        w.WriteStartObject();
        w.WriteString("type", message.Type);
        switch (message)
        {
            case WelcomeMessage welcome:
                w.WriteNumber("version", welcome.Version);
                w.WriteNumber("id", welcome.Id);
                w.WriteString("map", welcome.MapId);
                w.WriteNumber("tick_rate", welcome.TickRate);
                if (welcome.Content is not null) w.WriteString("content", welcome.Content);
                if (welcome.Token is not null) w.WriteString("token", welcome.Token);
                break;
            case SnapshotMessage snapshot:
                w.WriteNumber("tick", snapshot.Tick);
                w.WriteString("map", snapshot.MapId);
                w.WriteNumber("id", snapshot.Id);
                w.WriteStartArray("entities");
                foreach (var e in snapshot.Entities) WriteRecord(w, e);
                w.WriteEndArray();
                if (snapshot.Vitals is { } own) WriteVitals(w, own.Hp, own.MaxHp, own.Sp, own.MaxSp);
                break;
            case DeltaMessage delta:
                w.WriteNumber("tick", delta.Tick);
                if (delta.Appeared.Count > 0)
                {
                    w.WriteStartArray("appeared");
                    foreach (var e in delta.Appeared) WriteRecord(w, e);
                    w.WriteEndArray();
                }
                if (delta.Changed.Count > 0)
                {
                    w.WriteStartArray("changed");
                    foreach (var c in delta.Changed) WriteChange(w, c.Id, c.Fields, c.Cell, c.Facing, c.Next, c.Speed, c.DisplayKey, c.Hp, c.Dead, c.Mine);
                    w.WriteEndArray();
                }
                if (delta.Disappeared.Count > 0)
                {
                    w.WriteStartArray("disappeared");
                    foreach (var id in delta.Disappeared) w.WriteNumberValue(id);
                    w.WriteEndArray();
                }
                if (delta.Hits is { Count: > 0 } hits)
                {
                    w.WriteStartArray("hits");
                    foreach (var h in hits) WriteHit(w, h.Attacker, h.Target, h.Amount, h.Result);
                    w.WriteEndArray();
                }
                if (delta.Vitals is { } vitals) WriteVitals(w, vitals.Hp, vitals.MaxHp, vitals.Sp, vitals.MaxSp);
                break;
            case RejectedMessage rejected:
                w.WriteString("intent", rejected.Intent);
                w.WriteString("reason", rejected.Reason);
                if (rejected.Cell is { } cell) WriteCell(w, "cell", cell);
                if (rejected.Target is { } target) w.WriteNumber("target", target);
                break;
            case AttackStoppedMessage stopped:
                w.WriteNumber("target", stopped.Target);
                w.WriteString("reason", stopped.Reason);
                break;
            case ExpMessage exp:
                w.WriteNumber("base_level", exp.BaseLevel);
                w.WriteNumber("base_exp", exp.BaseExp);
                w.WriteNumber("base_next", exp.BaseNext);
                w.WriteNumber("job_level", exp.JobLevel);
                w.WriteNumber("job_exp", exp.JobExp);
                w.WriteNumber("job_next", exp.JobNext);
                break;
            case LevelUpMessage level:
                if (level.BaseLevel is { } baseLevel) w.WriteNumber("base_level", baseLevel);
                if (level.JobLevel is { } jobLevel) w.WriteNumber("job_level", jobLevel);
                break;
            case YouDiedMessage died:
                if (died.Killer is { } killer) w.WriteNumber("killer", killer);
                break;
            case RespawnedMessage respawned:
                w.WriteString("map", respawned.MapId);
                WriteCell(w, "cell", respawned.Cell);
                w.WriteNumber("hp", respawned.Hp);
                break;
            case PongMessage pong:
                w.WriteNumber("seq", pong.Seq);
                break;
            case ByeMessage bye:
                w.WriteString("reason", bye.Reason);
                break;
        }
        w.WriteEndObject();
    }

    /// <summary>A server message as a new byte array.</summary>
    public static byte[] ToBytes(ServerMessage message)
    {
        var buffer = new ArrayBufferWriter<byte>(128);
        Write(buffer, message);
        return buffer.WrittenSpan.ToArray();
    }

    /// <summary>A client message as a new byte array.</summary>
    public static byte[] ToBytes(ClientMessage message)
    {
        var buffer = new ArrayBufferWriter<byte>(64);
        Write(buffer, message);
        return buffer.WrittenSpan.ToArray();
    }

    /// <summary>Reads a server message (what the client receives). Error: <see cref="ByeReasons.Malformed"/> or <see cref="ByeReasons.UnknownType"/>.</summary>
    public static ReadResult<ServerMessage> ReadServer(ReadOnlySpan<byte> utf8)
    {
        try
        {
            var reader = new Utf8JsonReader(utf8);
            if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject) return Fail<ServerMessage>(ByeReasons.Malformed);
            string? type = null, map = null, intent = null, reason = null, content = null, token = null;
            long tick = 0, id = 0, seq = 0, baseExp = 0, baseNext = 0, jobExp = 0, jobNext = 0;
            long? target = null, killer = null;
            int version = 0, tickRate = 0, hp = 0;
            int? baseLevel = null, jobLevel = null;
            GridCell? cell = null;
            VitalsRecord? vitals = null;
            var entities = new List<EntityRecord>();
            var changed = new List<ChangeRecord>();
            var gone = new List<long>();
            List<HitRecord>? hits = null;
            while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
            {
                var key = reader.GetString();
                reader.Read();
                switch (key)
                {
                    case "type": type = String(ref reader); break;
                    case "version": version = reader.GetInt32(); break;
                    case "id": id = reader.GetInt64(); break;
                    case "map": map = String(ref reader); break;
                    case "tick_rate": tickRate = reader.GetInt32(); break;
                    case "content": content = String(ref reader); break;
                    case "token": token = String(ref reader); break;
                    case "tick": tick = reader.GetInt64(); break;
                    case "seq": seq = reader.GetInt64(); break;
                    case "intent": intent = String(ref reader); break;
                    case "reason": reason = String(ref reader); break;
                    case "cell": cell = Cell(ref reader); break;
                    case "entities":
                    case "appeared":
                        ReadArray(ref reader, entities);
                        break;
                    case "changed": ReadChanges(ref reader, changed); break;
                    case "disappeared":
                        if (reader.TokenType != JsonTokenType.StartArray) throw Bad();
                        while (reader.Read() && reader.TokenType != JsonTokenType.EndArray) gone.Add(reader.GetInt64());
                        break;
                    case "hits": hits = ReadHits(ref reader); break;
                    case "vitals": vitals = ReadVitals(ref reader); break;
                    case "target": target = reader.GetInt64(); break;
                    case "killer": killer = reader.GetInt64(); break;
                    case "hp": hp = reader.GetInt32(); break;
                    case "base_level": baseLevel = reader.GetInt32(); break;
                    case "base_exp": baseExp = reader.GetInt64(); break;
                    case "base_next": baseNext = reader.GetInt64(); break;
                    case "job_level": jobLevel = reader.GetInt32(); break;
                    case "job_exp": jobExp = reader.GetInt64(); break;
                    case "job_next": jobNext = reader.GetInt64(); break;
                    default: reader.Skip(); break;
                }
            }
            ServerMessage? message = type switch
            {
                MessageTypes.Welcome => map is null ? null : new WelcomeMessage(version, id, map, tickRate, content, token),
                MessageTypes.Snapshot => map is null ? null : new SnapshotMessage(tick, map, id, entities, vitals),
                MessageTypes.Delta => new DeltaMessage(tick, entities, changed, gone, hits, vitals),
                MessageTypes.Rejected => intent is null || reason is null ? null : new RejectedMessage(intent, reason, cell, target),
                MessageTypes.AttackStopped => target is null || reason is null ? null : new AttackStoppedMessage(target.Value, reason),
                MessageTypes.Exp => baseLevel is null || jobLevel is null ? null : new ExpMessage(baseLevel.Value, baseExp, baseNext, jobLevel.Value, jobExp, jobNext),
                MessageTypes.LevelUp => (baseLevel is null) == (jobLevel is null) ? null : new LevelUpMessage(baseLevel, jobLevel),
                MessageTypes.YouDied => new YouDiedMessage(killer),
                MessageTypes.Respawned => map is null || cell is null ? null : new RespawnedMessage(map, cell.Value, hp),
                MessageTypes.Pong => new PongMessage(seq),
                MessageTypes.Bye => reason is null ? null : new ByeMessage(reason),
                null => null,
                _ => throw new UnknownTypeException(),
            };
            return message is null ? Fail<ServerMessage>(ByeReasons.Malformed) : new ReadResult<ServerMessage>(message, null);
        }
        catch (UnknownTypeException)
        {
            return Fail<ServerMessage>(ByeReasons.UnknownType);
        }
        catch (Exception e) when (e is JsonException or InvalidOperationException or FormatException)
        {
            return Fail<ServerMessage>(ByeReasons.Malformed);
        }
    }

    // ---- shared pieces (also used by ViewMessageWriter) ----

    private static void WriteRecord(Utf8JsonWriter w, EntityRecord e) =>
        WriteRecord(w, e.Id, e.Kind, e.DisplayKey, e.Cell, e.Facing, e.Next, e.Speed, e.Hp, e.Dead, e.Quantity, e.Mine);

    internal static void WriteRecord(Utf8JsonWriter w, long id, string kind, string? displayKey, GridCell cell, string facing, GridCell? next, double speed,
        double? hp, bool dead, int quantity, bool? mine)
    {
        w.WriteStartObject();
        w.WriteNumber("id", id);
        w.WriteString("kind", kind);
        if (displayKey is not null) w.WriteString("display_key", displayKey);
        WriteCell(w, "cell", cell);
        w.WriteString("facing", facing);
        if (next is { } n) WriteCell(w, "next", n);
        if (speed != 0) w.WriteNumber("speed", Round(speed));
        if (hp is { } fraction) w.WriteNumber("hp", Round(fraction));
        if (dead) w.WriteBoolean("dead", true);
        if (quantity != 0) w.WriteNumber("quantity", quantity);
        if (mine is { } m) w.WriteBoolean("mine", m);
        w.WriteEndObject();
    }

    internal static void WriteHit(Utf8JsonWriter w, long attacker, long target, int amount, string result)
    {
        w.WriteStartArray();
        w.WriteNumberValue(attacker);
        w.WriteNumberValue(target);
        w.WriteNumberValue(amount);
        w.WriteStringValue(result);
        w.WriteEndArray();
    }

    internal static void WriteVitals(Utf8JsonWriter w, int hp, int maxHp, int sp, int maxSp)
    {
        w.WriteStartObject("vitals");
        w.WriteNumber("hp", hp);
        w.WriteNumber("max_hp", maxHp);
        w.WriteNumber("sp", sp);
        w.WriteNumber("max_sp", maxSp);
        w.WriteEndObject();
    }

    internal static void WriteChange(Utf8JsonWriter w, long id, ChangedFields fields, GridCell cell, string? facing, GridCell? next, double speed, string? displayKey,
        double hp, bool dead, bool mine)
    {
        w.WriteStartObject();
        w.WriteNumber("id", id);
        if ((fields & ChangedFields.Cell) != 0) WriteCell(w, "cell", cell);
        if ((fields & ChangedFields.Facing) != 0) w.WriteString("facing", facing);
        if ((fields & ChangedFields.Next) != 0)
        {
            if (next is { } n) WriteCell(w, "next", n);
            else w.WriteNull("next");
        }
        if ((fields & ChangedFields.Speed) != 0) w.WriteNumber("speed", Round(speed));
        if ((fields & ChangedFields.DisplayKey) != 0)
        {
            if (displayKey is null) w.WriteNull("display_key");
            else w.WriteString("display_key", displayKey);
        }
        if ((fields & ChangedFields.Hp) != 0) w.WriteNumber("hp", Round(hp));
        if ((fields & ChangedFields.Dead) != 0) w.WriteBoolean("dead", dead);
        if ((fields & ChangedFields.Mine) != 0) w.WriteBoolean("mine", mine);
        w.WriteEndObject();
    }

    internal static void WriteCell(Utf8JsonWriter w, string name, GridCell cell)
    {
        w.WriteStartArray(name);
        w.WriteNumberValue(cell.X);
        w.WriteNumberValue(cell.Y);
        w.WriteEndArray();
    }

    /// <summary>Speeds and HP fractions go out with two decimals: enough to time a step or draw a bar, and short.</summary>
    internal static double Round(double speed) => Math.Round(speed, 2, MidpointRounding.AwayFromZero);

    private static void ReadArray(ref Utf8JsonReader reader, List<EntityRecord> into)
    {
        if (reader.TokenType != JsonTokenType.StartArray) throw Bad();
        while (reader.Read() && reader.TokenType == JsonTokenType.StartObject)
        {
            long id = 0;
            string? kind = null, key = null, facing = null;
            GridCell cell = default;
            GridCell? next = null;
            double speed = 0;
            double? hp = null;
            var dead = false;
            var quantity = 0;
            bool? mine = null;
            while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
            {
                var name = reader.GetString();
                reader.Read();
                switch (name)
                {
                    case "id": id = reader.GetInt64(); break;
                    case "kind": kind = String(ref reader); break;
                    case "display_key": key = String(ref reader); break;
                    case "cell": cell = Cell(ref reader) ?? throw Bad(); break;
                    case "facing": facing = String(ref reader); break;
                    case "next": next = Cell(ref reader); break;
                    case "speed": speed = reader.GetDouble(); break;
                    case "hp": hp = reader.GetDouble(); break;
                    case "dead": dead = reader.GetBoolean(); break;
                    case "quantity": quantity = reader.GetInt32(); break;
                    case "mine": mine = reader.GetBoolean(); break;
                    default: reader.Skip(); break;
                }
            }
            into.Add(new EntityRecord(id, kind ?? throw Bad(), key, cell, facing ?? throw Bad(), next, speed, hp, dead, quantity, mine));
        }
    }

    private static void ReadChanges(ref Utf8JsonReader reader, List<ChangeRecord> into)
    {
        if (reader.TokenType != JsonTokenType.StartArray) throw Bad();
        while (reader.Read() && reader.TokenType == JsonTokenType.StartObject)
        {
            long id = 0;
            var fields = ChangedFields.None;
            string? facing = null, key = null;
            GridCell cell = default;
            GridCell? next = null;
            double speed = 0, hp = 0;
            bool dead = false, mine = false;
            while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
            {
                var name = reader.GetString();
                reader.Read();
                switch (name)
                {
                    case "id": id = reader.GetInt64(); break;
                    case "cell": cell = Cell(ref reader) ?? throw Bad(); fields |= ChangedFields.Cell; break;
                    case "facing": facing = String(ref reader); fields |= ChangedFields.Facing; break;
                    case "next": next = Cell(ref reader); fields |= ChangedFields.Next; break;
                    case "speed": speed = reader.GetDouble(); fields |= ChangedFields.Speed; break;
                    case "display_key": key = String(ref reader); fields |= ChangedFields.DisplayKey; break;
                    case "hp": hp = reader.GetDouble(); fields |= ChangedFields.Hp; break;
                    case "dead": dead = reader.GetBoolean(); fields |= ChangedFields.Dead; break;
                    case "mine": mine = reader.GetBoolean(); fields |= ChangedFields.Mine; break;
                    default: reader.Skip(); break;
                }
            }
            into.Add(new ChangeRecord(id, fields, cell, facing, next, speed, key, hp, dead, mine));
        }
    }

    /// <summary><c>[[attacker,target,amount,"result"],...]</c>.</summary>
    private static List<HitRecord> ReadHits(ref Utf8JsonReader reader)
    {
        if (reader.TokenType != JsonTokenType.StartArray) throw Bad();
        var hits = new List<HitRecord>();
        while (reader.Read() && reader.TokenType == JsonTokenType.StartArray)
        {
            reader.Read();
            var attacker = reader.GetInt64();
            reader.Read();
            var target = reader.GetInt64();
            reader.Read();
            var amount = reader.GetInt32();
            reader.Read();
            var result = String(ref reader) ?? throw Bad();
            if (!reader.Read() || reader.TokenType != JsonTokenType.EndArray) throw Bad();
            hits.Add(new HitRecord(attacker, target, amount, result));
        }
        return hits;
    }

    private static VitalsRecord ReadVitals(ref Utf8JsonReader reader)
    {
        if (reader.TokenType != JsonTokenType.StartObject) throw Bad();
        int hp = 0, maxHp = 0, sp = 0, maxSp = 0;
        while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
        {
            var name = reader.GetString();
            reader.Read();
            switch (name)
            {
                case "hp": hp = reader.GetInt32(); break;
                case "max_hp": maxHp = reader.GetInt32(); break;
                case "sp": sp = reader.GetInt32(); break;
                case "max_sp": maxSp = reader.GetInt32(); break;
                default: reader.Skip(); break;
            }
        }
        return new VitalsRecord(hp, maxHp, sp, maxSp);
    }

    /// <summary>A string, or null for a JSON null; anything else is malformed.</summary>
    private static string? String(ref Utf8JsonReader reader) =>
        reader.TokenType switch
        {
            JsonTokenType.String => reader.GetString(),
            JsonTokenType.Null => null,
            _ => throw Bad(),
        };

    /// <summary><c>[x,y]</c> as a cell, or null for a JSON null; anything else (other lengths, non-integers) is malformed.</summary>
    private static GridCell? Cell(ref Utf8JsonReader reader)
    {
        if (reader.TokenType == JsonTokenType.Null) return null;
        if (reader.TokenType != JsonTokenType.StartArray) throw Bad();
        if (!reader.Read() || reader.TokenType != JsonTokenType.Number || !reader.TryGetInt32(out var x)) throw Bad();
        if (!reader.Read() || reader.TokenType != JsonTokenType.Number || !reader.TryGetInt32(out var y)) throw Bad();
        if (!reader.Read() || reader.TokenType != JsonTokenType.EndArray) throw Bad();
        return new GridCell(x, y);
    }

    private static JsonException Bad() => new("malformed");

    private static ReadResult<T> Fail<T>(string reason) where T : class => new(null, reason);

    private sealed class UnknownTypeException : Exception;
}
