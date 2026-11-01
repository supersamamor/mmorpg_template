using System.Buffers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Game.Shared.ClientContent;
using Game.Shared.World;

namespace Game.Content.ClientExport;

/// <summary>The canonical bytes of a client bundle file and its content hash.</summary>
/// <param name="Hash">The content hash: SHA-256, lowercase hex, over <see cref="ClientBundleWriter.HashedBytes"/>.</param>
/// <param name="FileBytes">The file: the same JSON with <c>content_hash</c> added after <c>format_version</c>, then one <c>\n</c>.</param>
public sealed record ClientBundleFile(string Hash, byte[] FileBytes);

/// <summary>
/// Writes a client bundle as canonical JSON and hashes it. Canonical means: the same bytes for the same bundle on any machine. Compact
/// (no whitespace), UTF-8 without a BOM, properties in a fixed order, lists in bundle order (the builder sorts by id), integers only (no
/// floating point anywhere), non-ASCII text escaped as <c>\uXXXX</c>, optional values left out when null, and a single <c>\n</c> at the
/// end of the file. Written by hand: no reflection and no culture-dependent formatting.
/// </summary>
public static class ClientBundleWriter
{
    /// <summary>The bytes the hash covers: the whole bundle JSON without the <c>content_hash</c> property and without the final <c>\n</c>.</summary>
    public static byte[] HashedBytes(ClientBundle bundle) => Write(bundle, null);

    /// <summary>The content hash of <paramref name="bundle"/>: SHA-256 over <see cref="HashedBytes"/>, lowercase hex.</summary>
    public static string ComputeHash(ClientBundle bundle) => Hex(SHA256.HashData(HashedBytes(bundle)));

    /// <summary>The file for <paramref name="bundle"/>: its hash and the bytes to write.</summary>
    public static ClientBundleFile Export(ClientBundle bundle)
    {
        var hash = ComputeHash(bundle);
        var body = Write(bundle, hash);
        var file = new byte[body.Length + 1];
        body.CopyTo(file, 0);
        file[body.Length] = (byte)'\n';
        return new ClientBundleFile(hash, file);
    }

    private static string Hex(byte[] bytes)
    {
        var text = new StringBuilder(bytes.Length * 2);
        foreach (var b in bytes) text.Append(b.ToString("x2", System.Globalization.CultureInfo.InvariantCulture));
        return text.ToString();
    }

    private static byte[] Write(ClientBundle bundle, string? hash)
    {
        var output = new ArrayBufferWriter<byte>(64 * 1024);
        using (var w = new Utf8JsonWriter(output, new JsonWriterOptions { Indented = false }))
        {
            w.WriteStartObject();
            w.WriteNumber("format_version", bundle.FormatVersion);
            if (hash is not null) w.WriteString("content_hash", hash);
            Strings(w, "elements", bundle.Elements);
            Strings(w, "stats", bundle.Stats);
            Array(w, "items", bundle.Items, Item);
            Array(w, "skills", bundle.Skills, Skill);
            Array(w, "jobs", bundle.Jobs, Job);
            Array(w, "statuses", bundle.Statuses, Status);
            Array(w, "monsters", bundle.Monsters, Monster);
            Array(w, "npcs", bundle.Npcs, Npc);
            Array(w, "dialogues", bundle.Dialogues, Dialogue);
            Array(w, "quests", bundle.Quests, Quest);
            Array(w, "maps", bundle.Maps, Map);
            Appearance(w, bundle.Appearance);
            w.WriteEndObject();
        }
        return output.WrittenSpan.ToArray();
    }

    private static void Array<T>(Utf8JsonWriter w, string name, IReadOnlyList<T> list, Action<Utf8JsonWriter, T> write)
    {
        w.WriteStartArray(name);
        foreach (var item in list)
        {
            w.WriteStartObject();
            write(w, item);
            w.WriteEndObject();
        }
        w.WriteEndArray();
    }

    private static void Strings(Utf8JsonWriter w, string name, IReadOnlyList<string> list)
    {
        w.WriteStartArray(name);
        foreach (var s in list) w.WriteStringValue(s);
        w.WriteEndArray();
    }

    private static void Opt(Utf8JsonWriter w, string name, string? value)
    {
        if (value is not null) w.WriteString(name, value);
    }

    private static void Cell(Utf8JsonWriter w, string name, GridCell cell)
    {
        w.WriteStartArray(name);
        w.WriteNumberValue(cell.X);
        w.WriteNumberValue(cell.Y);
        w.WriteEndArray();
    }

    private static void Item(Utf8JsonWriter w, ClientItem i)
    {
        w.WriteString("id", i.Id);
        w.WriteString("name", i.Name);
        Opt(w, "description", i.Description);
        w.WriteString("kind", i.Kind);
        Opt(w, "icon", i.IconAssetKey);
        Opt(w, "ground", i.GroundAssetKey);
        if (i.Slots.Count > 0) Strings(w, "slots", i.Slots);
        Opt(w, "weapon_type", i.WeaponType);
        Opt(w, "visual", i.VisualAssetKey);
        Opt(w, "use_sfx", i.UseSfx);
        Opt(w, "equip_sfx", i.EquipSfx);
    }

    private static void Skill(Utf8JsonWriter w, ClientSkill s)
    {
        w.WriteString("id", s.Id);
        w.WriteString("name", s.Name);
        Opt(w, "description", s.Description);
        w.WriteNumber("max_level", s.MaxLevel);
        w.WriteBoolean("passive", s.Passive);
        w.WriteNumber("range", s.TargetRange);
        w.WriteBoolean("ground", s.TargetGround);
        w.WriteBoolean("friendly", s.TargetFriendly);
        Opt(w, "area_shape", s.AreaShape);
        if (s.AreaShape is not null) w.WriteNumber("area_radius", s.AreaRadius);
        if (s.CastTimeMs is { } cast) w.WriteNumber("cast_time_ms", cast);
        Opt(w, "animation", s.AnimationId);
        Opt(w, "icon", s.IconAssetKey);
        Opt(w, "vfx", s.VfxAssetKey);
        Opt(w, "sfx", s.SfxAssetKey);
    }

    private static void Job(Utf8JsonWriter w, ClientJob j)
    {
        w.WriteString("id", j.Id);
        w.WriteString("name", j.Name);
        Opt(w, "description", j.Description);
        Opt(w, "parent", j.ParentJobId);
        if (j.WeaponTypes.Count > 0) Strings(w, "weapon_types", j.WeaponTypes);
        Opt(w, "model", j.ModelAssetKey);
        Opt(w, "icon", j.IconAssetKey);
        Array(w, "skills", j.Skills, (jw, s) =>
        {
            jw.WriteString("skill", s.SkillId);
            if (s.MaxLevel is { } max) jw.WriteNumber("max_level", max);
            if (s.Requires.Count > 0)
                Array(jw, "requires", s.Requires, (rw, r) =>
                {
                    rw.WriteString("skill", r.SkillId);
                    rw.WriteNumber("level", r.Level);
                });
        });
    }

    private static void Appearance(Utf8JsonWriter w, ClientAppearance a)
    {
        w.WriteStartObject("appearance");
        Array(w, "genders", a.Genders, (gw, g) =>
        {
            gw.WriteString("gender", g.Gender);
            gw.WriteString("name", g.Name);
            gw.WriteString("body", g.BodyModelAssetKey);
            Array(gw, "faces", g.Faces, AppearanceOption);
            Array(gw, "hairstyles", g.Hairstyles, AppearanceOption);
            gw.WriteString("default_face", g.DefaultFaceId);
            gw.WriteString("default_hairstyle", g.DefaultHairstyleId);
        });
        Array(w, "skin_tones", a.SkinTones, ColorOption);
        w.WriteString("default_skin_tone", a.DefaultSkinToneId);
        Array(w, "hair_colors", a.HairColors, ColorOption);
        w.WriteString("default_hair_color", a.DefaultHairColorId);
        w.WriteEndObject();
    }

    private static void AppearanceOption(Utf8JsonWriter w, ClientAppearanceOption o)
    {
        w.WriteString("id", o.Id);
        w.WriteString("name", o.Name);
        w.WriteString("model", o.ModelAssetKey);
    }

    private static void ColorOption(Utf8JsonWriter w, ClientColorOption o)
    {
        w.WriteString("id", o.Id);
        w.WriteString("name", o.Name);
        w.WriteString("color", o.Color);
    }

    private static void Status(Utf8JsonWriter w, ClientStatus s)
    {
        w.WriteString("id", s.Id);
        w.WriteString("name", s.Name);
        Opt(w, "description", s.Description);
        w.WriteBoolean("debuff", s.Debuff);
        Opt(w, "icon", s.IconAssetKey);
        Opt(w, "vfx", s.VfxAssetKey);
    }

    private static void Monster(Utf8JsonWriter w, ClientMonster m)
    {
        w.WriteString("id", m.Id);
        w.WriteString("name", m.Name);
        Opt(w, "model", m.ModelAssetKey);
        if (m.Sounds is { } sounds)
        {
            w.WriteStartObject("sounds");
            Opt(w, "aggro", sounds.Aggro);
            Opt(w, "hit", sounds.Hit);
            Opt(w, "die", sounds.Die);
            w.WriteEndObject();
        }
    }

    private static void Npc(Utf8JsonWriter w, ClientNpc n)
    {
        w.WriteString("id", n.Id);
        w.WriteString("name", n.Name);
        Opt(w, "title", n.Title);
        Opt(w, "dialogue", n.DialogueId);
        Opt(w, "model", n.ModelAssetKey);
        Opt(w, "portrait", n.PortraitAssetKey);
    }

    private static void Dialogue(Utf8JsonWriter w, ClientDialogue d)
    {
        w.WriteString("id", d.Id);
        w.WriteString("start", d.Start);
        Array(w, "nodes", d.Nodes, (nw, n) =>
        {
            nw.WriteString("key", n.Key);
            Opt(nw, "speaker", n.SpeakerNpcId);
            Opt(nw, "portrait", n.PortraitAssetKey);
            nw.WriteString("text", n.Text);
            Opt(nw, "next", n.Next);
            if (n.Choices.Count > 0)
                Array(nw, "choices", n.Choices, (cw, c) =>
                {
                    cw.WriteString("text", c.Text);
                    Opt(cw, "next", c.Next);
                    Opt(cw, "quest", c.StartQuestId);
                });
        });
    }

    private static void Quest(Utf8JsonWriter w, ClientQuest q)
    {
        w.WriteString("id", q.Id);
        w.WriteString("name", q.Name);
        Opt(w, "description", q.Description);
        Opt(w, "giver", q.GiverNpcId);
        Strings(w, "steps", q.StepTexts);
    }

    private static void Map(Utf8JsonWriter w, ClientMap m)
    {
        w.WriteString("id", m.Id);
        w.WriteString("name", m.Name);
        w.WriteString("scene", m.SceneAssetKey);
        Opt(w, "music", m.MusicAssetKey);
        Opt(w, "ambience", m.AmbienceAssetKey);
        w.WriteNumber("width", m.Width);
        w.WriteNumber("height", m.Height);
        w.WriteString("cells", m.Cells);
        Array(w, "warps", m.Warps, (ww, p) =>
        {
            Cell(ww, "cell", p.Cell);
            ww.WriteString("map", p.TargetMapId);
            Cell(ww, "target", p.TargetCell);
        });
        Array(w, "npcs", m.Npcs, (nw, n) =>
        {
            nw.WriteString("npc", n.NpcId);
            Cell(nw, "cell", n.Cell);
            nw.WriteString("facing", n.Facing);
        });
        Array(w, "zones", m.Zones, (zw, z) =>
        {
            zw.WriteString("kind", z.Kind);
            zw.WriteStartArray("area");
            zw.WriteNumberValue(z.Area.X);
            zw.WriteNumberValue(z.Area.Y);
            zw.WriteNumberValue(z.Area.W);
            zw.WriteNumberValue(z.Area.H);
            zw.WriteEndArray();
        });
    }
}
