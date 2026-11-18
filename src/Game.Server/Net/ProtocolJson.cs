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
            string? type = null, name = null, content = null, resume = null, slot = null, skill = null, job = null;
            int? version = null, quantity = null, level = null;
            long? seq = null, target = null, entry = null, card = null;
            GridCell? cell = null;
            while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
            {
                var key = reader.GetString();
                reader.Read();
                switch (key)
                {
                    case "type": type = String(ref reader); break;
                    case "card": card = reader.TokenType == JsonTokenType.Number && reader.TryGetInt64(out var cd) ? cd : throw Bad(); break;
                    case "version": version = reader.TokenType == JsonTokenType.Number && reader.TryGetInt32(out var v) ? v : throw Bad(); break;
                    case "name": name = String(ref reader); break;
                    case "content": content = String(ref reader); break;
                    case "resume": resume = String(ref reader); break;
                    case "seq": seq = reader.TokenType == JsonTokenType.Number && reader.TryGetInt64(out var s) ? s : throw Bad(); break;
                    case "target": target = reader.TokenType == JsonTokenType.Number && reader.TryGetInt64(out var t) ? t : throw Bad(); break;
                    case "cell": cell = Cell(ref reader); break;
                    case "entry": entry = reader.TokenType == JsonTokenType.Number && reader.TryGetInt64(out var en) ? en : throw Bad(); break;
                    case "slot": slot = String(ref reader); break;
                    case "skill": skill = String(ref reader); break;
                    case "job": job = String(ref reader); break;
                    case "level": level = reader.TokenType == JsonTokenType.Number && reader.TryGetInt32(out var lv) ? lv : throw Bad(); break;
                    case "quantity": quantity = reader.TokenType == JsonTokenType.Number && reader.TryGetInt32(out var q) ? q : throw Bad(); break;
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
                MessageTypes.PickUp => target is { } item ? new PickUpMessage(item) : null,
                MessageTypes.UseItem => entry is { } used ? new UseItemMessage(used) : null,
                MessageTypes.Equip => entry is { } worn ? new EquipMessage(worn, slot) : null,
                MessageTypes.Unequip => slot is { } off ? new UnequipMessage(off) : null,
                MessageTypes.DropItem => entry is { } dropped && quantity is { } count ? new DropItemMessage(dropped, count) : null,
                MessageTypes.InsertCard => entry is { } socketed && card is { } inserted ? new InsertCardMessage(socketed, inserted) : null,
                MessageTypes.Refine => entry is { } refined ? new RefineMessage(refined) : null,
                MessageTypes.UseSkill => skill is { } cast ? new UseSkillMessage(cast, level, target, cell) : null,
                MessageTypes.LearnSkill => skill is { } learned ? new LearnSkillMessage(learned) : null,
                MessageTypes.ChangeJob => job is { } changed ? new ChangeJobMessage(changed) : null,
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
            case PickUpMessage pick:
                w.WriteNumber("target", pick.Target);
                break;
            case UseItemMessage use:
                w.WriteNumber("entry", use.Entry);
                break;
            case EquipMessage equip:
                w.WriteNumber("entry", equip.Entry);
                if (equip.Slot is not null) w.WriteString("slot", equip.Slot);
                break;
            case UnequipMessage unequip:
                w.WriteString("slot", unequip.Slot);
                break;
            case DropItemMessage drop:
                w.WriteNumber("entry", drop.Entry);
                w.WriteNumber("quantity", drop.Quantity);
                break;
            case InsertCardMessage insert:
                w.WriteNumber("entry", insert.Entry);
                w.WriteNumber("card", insert.Card);
                break;
            case RefineMessage refine:
                w.WriteNumber("entry", refine.Entry);
                break;
            case UseSkillMessage use:
                w.WriteString("skill", use.Skill);
                if (use.Level is { } level) w.WriteNumber("level", level);
                if (use.Target is { } target) w.WriteNumber("target", target);
                if (use.Cell is { } cell) WriteCell(w, "cell", cell);
                break;
            case LearnSkillMessage learn:
                w.WriteString("skill", learn.Skill);
                break;
            case ChangeJobMessage change:
                w.WriteString("job", change.Job);
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
                    foreach (var c in delta.Changed) WriteChange(w, c.Id, c.Fields, c.Cell, c.Facing, c.Next, c.Speed, c.DisplayKey, c.Hp, c.Dead, c.Mine, c.Weapon, c.Worn, c.Statuses);
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
                if (delta.Casts is { Count: > 0 } casts)
                {
                    w.WriteStartArray("casts");
                    foreach (var c in casts) WriteCast(w, c.Caster, c.Skill, c.Ms);
                    w.WriteEndArray();
                }
                if (delta.CastEnds is { Count: > 0 } ends)
                {
                    w.WriteStartArray("cast_ends");
                    foreach (var c in ends) WriteCastEnd(w, c.Caster, c.Skill, c.Result);
                    w.WriteEndArray();
                }
                if (delta.Vitals is { } vitals) WriteVitals(w, vitals.Hp, vitals.MaxHp, vitals.Sp, vitals.MaxSp);
                break;
            case SkillsMessage skills:
                w.WriteString("job", skills.Job);
                w.WriteNumber("points", skills.Points);
                w.WriteStartArray("levels");
                foreach (var l in skills.Levels)
                {
                    w.WriteStartArray();
                    w.WriteStringValue(l.Skill);
                    w.WriteNumberValue(l.Level);
                    w.WriteEndArray();
                }
                w.WriteEndArray();
                w.WriteStartArray("cooldowns");
                foreach (var c in skills.Cooldowns)
                {
                    w.WriteStartArray();
                    w.WriteStringValue(c.Skill);
                    w.WriteNumberValue(c.Ms);
                    w.WriteEndArray();
                }
                w.WriteEndArray();
                w.WriteStartArray("jobs");
                foreach (var j in skills.Jobs) w.WriteStringValue(j);
                w.WriteEndArray();
                break;
            case CooldownMessage cooldown:
                w.WriteString("skill", cooldown.Skill);
                w.WriteNumber("ms", cooldown.Ms);
                break;
            case RejectedMessage rejected:
                w.WriteString("intent", rejected.Intent);
                w.WriteString("reason", rejected.Reason);
                if (rejected.Cell is { } cell) WriteCell(w, "cell", cell);
                if (rejected.Target is { } target) w.WriteNumber("target", target);
                if (rejected.Entry is { } entry) w.WriteNumber("entry", entry);
                if (rejected.Slot is not null) w.WriteString("slot", rejected.Slot);
                if (rejected.Skill is not null) w.WriteString("skill", rejected.Skill);
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
            case InventoryMessage inventory:
                w.WriteNumber("rev", inventory.Rev);
                w.WriteNumber("slots", inventory.Slots);
                w.WriteNumber("weight", inventory.Weight);
                w.WriteNumber("weight_limit", inventory.WeightLimit);
                w.WriteString("load", inventory.Load);
                w.WriteStartArray("entries");
                foreach (var e in inventory.Entries) WriteEntry(w, e);
                w.WriteEndArray();
                break;
            case InventoryChangedMessage change:
                w.WriteNumber("rev", change.Rev);
                w.WriteString("cause", change.Cause);
                if (change.Item is not null) w.WriteString("item", change.Item);
                w.WriteNumber("weight", change.Weight);
                w.WriteNumber("weight_limit", change.WeightLimit);
                w.WriteString("load", change.Load);
                if (change.Entries.Count > 0)
                {
                    w.WriteStartArray("entries");
                    foreach (var e in change.Entries) WriteEntry(w, e);
                    w.WriteEndArray();
                }
                if (change.Removed.Count > 0)
                {
                    w.WriteStartArray("removed");
                    foreach (var id in change.Removed) w.WriteNumberValue(id);
                    w.WriteEndArray();
                }
                break;
            case RefineResultMessage refined:
                w.WriteNumber("entry", refined.Entry);
                w.WriteString("item", refined.Item);
                w.WriteString("result", refined.Result);
                w.WriteNumber("refine", refined.Refine);
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
            string? type = null, map = null, intent = null, reason = null, content = null, token = null, slot = null, cause = null, item = null, load = null, result = null, skill = null, job = null;
            long tick = 0, id = 0, seq = 0, baseExp = 0, baseNext = 0, jobExp = 0, jobNext = 0, rev = 0;
            long? target = null, killer = null, entry = null;
            int version = 0, tickRate = 0, hp = 0, slots = 0, weight = 0, weightLimit = 0, refine = 0;
            List<InventoryEntryRecord>? inventoryEntries = null;
            List<long>? removed = null;
            int? baseLevel = null, jobLevel = null;
            GridCell? cell = null;
            VitalsRecord? vitals = null;
            var entities = new List<EntityRecord>();
            var changed = new List<ChangeRecord>();
            var gone = new List<long>();
            List<HitRecord>? hits = null;
            List<CastRecord>? casts = null;
            List<CastEndRecord>? castEnds = null;
            List<(string, int)>? levels = null, cooldowns = null;
            List<string>? jobs = null;
            int points = 0, ms = 0;
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
                    case "casts": casts = ReadCasts(ref reader); break;
                    case "cast_ends": castEnds = ReadCastEnds(ref reader); break;
                    case "skill": skill = String(ref reader); break;
                    case "job": job = String(ref reader); break;
                    case "points": points = reader.GetInt32(); break;
                    case "ms": ms = reader.GetInt32(); break;
                    case "levels": levels = ReadPairs(ref reader); break;
                    case "cooldowns": cooldowns = ReadPairs(ref reader); break;
                    case "jobs": jobs = ReadStrings(ref reader); break;
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
                    case "entry": entry = reader.GetInt64(); break;
                    case "slot": slot = String(ref reader); break;
                    case "rev": rev = reader.GetInt64(); break;
                    case "slots": slots = reader.GetInt32(); break;
                    case "weight": weight = reader.GetInt32(); break;
                    case "weight_limit": weightLimit = reader.GetInt32(); break;
                    case "load": load = String(ref reader); break;
                    case "cause": cause = String(ref reader); break;
                    case "item": item = String(ref reader); break;
                    case "entries": inventoryEntries = ReadEntries(ref reader); break;
                    case "result": result = String(ref reader); break;
                    case "refine": refine = reader.GetInt32(); break;
                    case "removed":
                        if (reader.TokenType != JsonTokenType.StartArray) throw Bad();
                        removed = new List<long>();
                        while (reader.Read() && reader.TokenType != JsonTokenType.EndArray) removed.Add(reader.GetInt64());
                        break;
                    default: reader.Skip(); break;
                }
            }
            ServerMessage? message = type switch
            {
                MessageTypes.Welcome => map is null ? null : new WelcomeMessage(version, id, map, tickRate, content, token),
                MessageTypes.Snapshot => map is null ? null : new SnapshotMessage(tick, map, id, entities, vitals),
                MessageTypes.Delta => new DeltaMessage(tick, entities, changed, gone, hits, vitals, casts, castEnds),
                MessageTypes.Rejected => intent is null || reason is null ? null : new RejectedMessage(intent, reason, cell, target, entry, slot, skill),
                MessageTypes.AttackStopped => target is null || reason is null ? null : new AttackStoppedMessage(target.Value, reason),
                MessageTypes.Exp => baseLevel is null || jobLevel is null ? null : new ExpMessage(baseLevel.Value, baseExp, baseNext, jobLevel.Value, jobExp, jobNext),
                MessageTypes.LevelUp => (baseLevel is null) == (jobLevel is null) ? null : new LevelUpMessage(baseLevel, jobLevel),
                MessageTypes.YouDied => new YouDiedMessage(killer),
                MessageTypes.Respawned => map is null || cell is null ? null : new RespawnedMessage(map, cell.Value, hp),
                MessageTypes.Inventory => load is null ? null
                    : new InventoryMessage(rev, slots, weight, weightLimit, load, (IReadOnlyList<InventoryEntryRecord>?)inventoryEntries ?? Array.Empty<InventoryEntryRecord>()),
                MessageTypes.InventoryChanged => load is null || cause is null ? null
                    : new InventoryChangedMessage(rev, cause, item, weight, weightLimit, load,
                        (IReadOnlyList<InventoryEntryRecord>?)inventoryEntries ?? Array.Empty<InventoryEntryRecord>(), (IReadOnlyList<long>?)removed ?? Array.Empty<long>()),
                MessageTypes.RefineResult => entry is null || item is null || result is null ? null : new RefineResultMessage(entry.Value, item, result, refine),
                MessageTypes.Skills => job is null ? null
                    : new SkillsMessage(job, points, (levels ?? []).ConvertAll(l => new SkillLevelRecord(l.Item1, l.Item2)),
                        (cooldowns ?? []).ConvertAll(c => new SkillCooldownRecord(c.Item1, c.Item2)), (IReadOnlyList<string>?)jobs ?? Array.Empty<string>()),
                MessageTypes.Cooldown => skill is null ? null : new CooldownMessage(skill, ms),
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
        WriteRecord(w, e.Id, e.Kind, e.DisplayKey, e.Cell, e.Facing, e.Next, e.Speed, e.Hp, e.Dead, e.Quantity, e.Mine, e.Weapon, e.Worn, e.Statuses);

    internal static void WriteRecord(Utf8JsonWriter w, long id, string kind, string? displayKey, GridCell cell, string facing, GridCell? next, double speed,
        double? hp, bool dead, int quantity, bool? mine, string? weapon = null, IReadOnlyList<string>? worn = null, IReadOnlyList<StatusRecord>? statuses = null)
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
        if (weapon is not null) w.WriteString("weapon", weapon);
        if (worn is { Count: > 0 }) WriteStrings(w, "worn", worn);
        if (statuses is { Count: > 0 }) WriteStatuses(w, statuses);
        w.WriteEndObject();
    }

    /// <summary><c>"statuses":[{"id":..,"stacks":..,"left_ms":..,"debuff":true},...]</c> (T-501); <c>debuff</c> only when true.</summary>
    internal static void WriteStatuses(Utf8JsonWriter w, IReadOnlyList<StatusRecord> statuses)
    {
        w.WriteStartArray("statuses");
        for (var i = 0; i < statuses.Count; i++)
        {
            var s = statuses[i];
            w.WriteStartObject();
            w.WriteString("id", s.Id);
            w.WriteNumber("stacks", s.Stacks);
            w.WriteNumber("left_ms", s.LeftMs);
            if (s.Debuff) w.WriteBoolean("debuff", true);
            w.WriteEndObject();
        }
        w.WriteEndArray();
    }

    private static List<StatusRecord> ReadStatuses(ref Utf8JsonReader reader)
    {
        if (reader.TokenType != JsonTokenType.StartArray) throw Bad();
        var list = new List<StatusRecord>();
        while (reader.Read() && reader.TokenType == JsonTokenType.StartObject)
        {
            string? id = null;
            int stacks = 1, left = 0;
            var debuff = false;
            while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
            {
                var name = reader.GetString();
                reader.Read();
                switch (name)
                {
                    case "id": id = String(ref reader); break;
                    case "stacks": stacks = reader.GetInt32(); break;
                    case "left_ms": left = reader.GetInt32(); break;
                    case "debuff": debuff = reader.GetBoolean(); break;
                    default: reader.Skip(); break;
                }
            }
            list.Add(new StatusRecord(id ?? throw Bad(), stacks, left, debuff));
        }
        return list;
    }

    private static void WriteStrings(Utf8JsonWriter w, string name, IReadOnlyList<string> values)
    {
        w.WriteStartArray(name);
        for (var i = 0; i < values.Count; i++) w.WriteStringValue(values[i]);
        w.WriteEndArray();
    }

    private static void WriteEntry(Utf8JsonWriter w, InventoryEntryRecord e)
    {
        w.WriteStartObject();
        w.WriteNumber("id", e.Id);
        w.WriteString("item", e.Item);
        w.WriteNumber("quantity", e.Quantity);
        if (e.Slot is not null) w.WriteString("slot", e.Slot);
        if (e.Cards is { Count: > 0 } cards) WriteStrings(w, "cards", cards);
        if (e.Refine != 0) w.WriteNumber("refine", e.Refine);
        w.WriteEndObject();
    }

    private static List<InventoryEntryRecord> ReadEntries(ref Utf8JsonReader reader)
    {
        if (reader.TokenType != JsonTokenType.StartArray) throw Bad();
        var list = new List<InventoryEntryRecord>();
        while (reader.Read() && reader.TokenType == JsonTokenType.StartObject)
        {
            long id = 0;
            string? item = null, slot = null;
            var quantity = 0;
            var refine = 0;
            List<string>? cards = null;
            while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
            {
                var name = reader.GetString();
                reader.Read();
                switch (name)
                {
                    case "id": id = reader.GetInt64(); break;
                    case "item": item = String(ref reader); break;
                    case "quantity": quantity = reader.GetInt32(); break;
                    case "slot": slot = String(ref reader); break;
                    case "cards": cards = ReadStrings(ref reader); break;
                    case "refine": refine = reader.GetInt32(); break;
                    default: reader.Skip(); break;
                }
            }
            list.Add(new InventoryEntryRecord(id, item ?? throw Bad(), quantity, slot, cards, refine));
        }
        return list;
    }

    private static List<string> ReadStrings(ref Utf8JsonReader reader)
    {
        if (reader.TokenType != JsonTokenType.StartArray) throw Bad();
        var list = new List<string>();
        while (reader.Read() && reader.TokenType != JsonTokenType.EndArray) list.Add(String(ref reader) ?? throw Bad());
        return list;
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

    internal static void WriteCast(Utf8JsonWriter w, long caster, string skill, int ms)
    {
        w.WriteStartArray();
        w.WriteNumberValue(caster);
        w.WriteStringValue(skill);
        w.WriteNumberValue(ms);
        w.WriteEndArray();
    }

    internal static void WriteCastEnd(Utf8JsonWriter w, long caster, string skill, string result)
    {
        w.WriteStartArray();
        w.WriteNumberValue(caster);
        w.WriteStringValue(skill);
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
        double hp, bool dead, bool mine, string? weapon = null, IReadOnlyList<string>? worn = null, IReadOnlyList<StatusRecord>? statuses = null)
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
        if ((fields & ChangedFields.Look) != 0)
        {
            w.WriteString("weapon", weapon);
            WriteStrings(w, "worn", worn ?? Array.Empty<string>());
        }
        if ((fields & ChangedFields.Statuses) != 0) WriteStatuses(w, statuses ?? Array.Empty<StatusRecord>());
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
            string? weapon = null;
            List<string>? worn = null;
            List<StatusRecord>? statuses = null;
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
                    case "weapon": weapon = String(ref reader); break;
                    case "worn": worn = ReadStrings(ref reader); break;
                    case "statuses": statuses = ReadStatuses(ref reader); break;
                    default: reader.Skip(); break;
                }
            }
            into.Add(new EntityRecord(id, kind ?? throw Bad(), key, cell, facing ?? throw Bad(), next, speed, hp, dead, quantity, mine, weapon, worn, statuses));
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
            string? weapon = null;
            List<string>? worn = null;
            List<StatusRecord>? statuses = null;
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
                    case "weapon": weapon = String(ref reader); fields |= ChangedFields.Look; break;
                    case "worn": worn = ReadStrings(ref reader); fields |= ChangedFields.Look; break;
                    case "statuses": statuses = ReadStatuses(ref reader); fields |= ChangedFields.Statuses; break;
                    default: reader.Skip(); break;
                }
            }
            into.Add(new ChangeRecord(id, fields, cell, facing, next, speed, key, hp, dead, mine, weapon, worn, statuses));
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

    /// <summary><c>[[caster,"skill",ms],...]</c>.</summary>
    private static List<CastRecord> ReadCasts(ref Utf8JsonReader reader)
    {
        if (reader.TokenType != JsonTokenType.StartArray) throw Bad();
        var list = new List<CastRecord>();
        while (reader.Read() && reader.TokenType == JsonTokenType.StartArray)
        {
            reader.Read();
            var caster = reader.GetInt64();
            reader.Read();
            var skill = String(ref reader) ?? throw Bad();
            reader.Read();
            var ms = reader.GetInt32();
            if (!reader.Read() || reader.TokenType != JsonTokenType.EndArray) throw Bad();
            list.Add(new CastRecord(caster, skill, ms));
        }
        return list;
    }

    /// <summary><c>[[caster,"skill","result"],...]</c>.</summary>
    private static List<CastEndRecord> ReadCastEnds(ref Utf8JsonReader reader)
    {
        if (reader.TokenType != JsonTokenType.StartArray) throw Bad();
        var list = new List<CastEndRecord>();
        while (reader.Read() && reader.TokenType == JsonTokenType.StartArray)
        {
            reader.Read();
            var caster = reader.GetInt64();
            reader.Read();
            var skill = String(ref reader) ?? throw Bad();
            reader.Read();
            var result = String(ref reader) ?? throw Bad();
            if (!reader.Read() || reader.TokenType != JsonTokenType.EndArray) throw Bad();
            list.Add(new CastEndRecord(caster, skill, result));
        }
        return list;
    }

    /// <summary><c>[["name",n],...]</c> as (name, number) pairs.</summary>
    private static List<(string, int)> ReadPairs(ref Utf8JsonReader reader)
    {
        if (reader.TokenType != JsonTokenType.StartArray) throw Bad();
        var list = new List<(string, int)>();
        while (reader.Read() && reader.TokenType == JsonTokenType.StartArray)
        {
            reader.Read();
            var name = String(ref reader) ?? throw Bad();
            reader.Read();
            var n = reader.GetInt32();
            if (!reader.Read() || reader.TokenType != JsonTokenType.EndArray) throw Bad();
            list.Add((name, n));
        }
        return list;
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
