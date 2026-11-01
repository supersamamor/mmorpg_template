using System.Buffers;
using System.Text.Json;
using Game.Server.World;
using Game.Server.World.Aoi;
using Game.Shared.Protocol;

namespace Game.Server.Net;

/// <summary>
/// Writes the AOI's <see cref="ViewSnapshot"/> and <see cref="ViewDelta"/> straight to <c>snapshot</c>/<c>delta</c> JSON, without building
/// <see cref="SnapshotMessage"/>/<see cref="DeltaMessage"/> records first: this runs on the tick thread for every observer, so it reuses
/// one buffer and one writer and allocates only the finished byte array. The bytes are the same as <see cref="ProtocolJson"/> gives for the
/// equivalent records (golden tests). Not thread-safe: one per session (calls for one observer come from one instance at a time).
/// </summary>
public sealed class ViewMessageWriter
{
    private readonly ArrayBufferWriter<byte> _buffer = new(1024);
    private readonly Utf8JsonWriter _writer;

    /// <summary>Creates a writer with its own buffer.</summary>
    public ViewMessageWriter() => _writer = new Utf8JsonWriter(_buffer, ProtocolJson.WriterOptions);

    /// <summary>The <c>kind</c> term of an entity kind.</summary>
    public static string KindTerm(EntityKind kind) => kind switch
    {
        EntityKind.Player => EntityKinds.Player,
        EntityKind.Monster => EntityKinds.Monster,
        EntityKind.Npc => EntityKinds.Npc,
        EntityKind.Item => EntityKinds.Item,
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null),
    };

    /// <summary>The wire field set of an AOI field mask (the bits are the same; this keeps the two enums from drifting silently).</summary>
    public static ChangedFields FieldsOf(ViewFields fields) => (ChangedFields)(int)fields;

    /// <summary>A <c>snapshot</c> message.</summary>
    public byte[] Write(ViewSnapshot snapshot)
    {
        Begin();
        var w = _writer;
        w.WriteStartObject();
        w.WriteString("type", MessageTypes.Snapshot);
        w.WriteNumber("tick", snapshot.Tick);
        w.WriteString("map", snapshot.MapId);
        w.WriteNumber("id", snapshot.Observer.Value);
        w.WriteStartArray("entities");
        var entities = snapshot.Entities;
        var observer = snapshot.Observer;
        for (var i = 0; i < entities.Count; i++) Record(entities[i], observer);
        w.WriteEndArray();
        if (snapshot.Vitals is { } own) ProtocolJson.WriteVitals(w, own.Hp, own.MaxHp, own.Sp, own.MaxSp);
        w.WriteEndObject();
        return End();
    }

    /// <summary>A <c>delta</c> message.</summary>
    public byte[] Write(ViewDelta delta)
    {
        Begin();
        var w = _writer;
        w.WriteStartObject();
        w.WriteString("type", MessageTypes.Delta);
        w.WriteNumber("tick", delta.Tick);
        var observer = delta.Observer;
        var appeared = delta.Appeared;
        if (appeared.Count > 0)
        {
            w.WriteStartArray("appeared");
            for (var i = 0; i < appeared.Count; i++) Record(appeared[i], observer);
            w.WriteEndArray();
        }
        var changed = delta.Changed;
        if (changed.Count > 0)
        {
            w.WriteStartArray("changed");
            for (var i = 0; i < changed.Count; i++)
            {
                var c = changed[i];
                var v = c.View;
                ProtocolJson.WriteChange(w, v.Id.Value, FieldsOf(c.Fields), v.Cell, v.Facing, v.Next, v.Speed, v.DisplayKey, HpFraction(v) ?? 0, v.Dead,
                    Mine(v, observer) ?? false);
            }
            w.WriteEndArray();
        }
        var gone = delta.Disappeared;
        if (gone.Count > 0)
        {
            w.WriteStartArray("disappeared");
            for (var i = 0; i < gone.Count; i++) w.WriteNumberValue(gone[i].Value);
            w.WriteEndArray();
        }
        var hits = delta.Hits;
        if (hits.Count > 0)
        {
            w.WriteStartArray("hits");
            for (var i = 0; i < hits.Count; i++)
            {
                var h = hits[i];
                ProtocolJson.WriteHit(w, h.Attacker.Value, h.Target.Value, h.Amount, h.Result);
            }
            w.WriteEndArray();
        }
        if (delta.Vitals is { } own) ProtocolJson.WriteVitals(w, own.Hp, own.MaxHp, own.Sp, own.MaxSp);
        w.WriteEndObject();
        return End();
    }

    /// <summary>The <c>hp</c> fraction of a view (a whole percentage over 100), or null for an entity without HP.</summary>
    public static double? HpFraction(in EntityView v) => v.HpPercent < 0 ? null : v.HpPercent / 100.0;

    /// <summary>
    /// A ground item's <c>mine</c> for <paramref name="observer"/>: true when nobody holds the loot priority any more or the observer does. Null
    /// for every other kind. This is the only trace of the priority on the wire; the holder's id and the timers stay on the server.
    /// </summary>
    public static bool? Mine(in EntityView v, EntityId observer) =>
        v.Kind == EntityKind.Item ? v.LootOwner is not { } owner || owner == observer : null;

    private void Record(in EntityView v, EntityId observer) =>
        ProtocolJson.WriteRecord(_writer, v.Id.Value, KindTerm(v.Kind), v.DisplayKey, v.Cell, v.Facing, v.Next, v.Speed, HpFraction(v), v.Dead, v.Quantity,
            Mine(v, observer));

    private void Begin()
    {
        _buffer.ResetWrittenCount();
        _writer.Reset(_buffer);
    }

    private byte[] End()
    {
        _writer.Flush();
        return _buffer.WrittenSpan.ToArray();
    }
}
