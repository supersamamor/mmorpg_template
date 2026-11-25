using System;
using System.Collections.Generic;
using Game.Client.Core.Bundle;
using Game.Shared.ClientContent;
using Game.Shared.Protocol;
using Game.Shared.Vocabulary;

namespace Game.Client.Core.Theme;

/// <summary>What a merged part is, which decides the colours it takes (T-706).</summary>
public enum PartRole
{
    /// <summary>Worn equipment: keeps its own colours.</summary>
    Gear,

    /// <summary>A piece of the job's look: keeps its own colours.</summary>
    Job,

    /// <summary>The face: takes the skin tone and the hair colour where it is painted with them.</summary>
    Face,

    /// <summary>The hairstyle: takes the hair colour as a whole.</summary>
    Hair,
}

/// <summary>One part merged into a character's body.</summary>
/// <param name="Piece">Its model and where it goes.</param>
/// <param name="Role">What it is.</param>
public readonly record struct DressPart(WornPiece Piece, PartRole Role);

/// <summary>A piece of the job's look and the equipment slot it stands for (null: always drawn).</summary>
public sealed record JobPart(WornPiece Piece, string? Slot);

/// <summary>
/// How one character looks under its equipment (T-706, ARCHITECTURE §7.5.1): the gender's body, the face and hairstyle, its colours and what its job adds. One object
/// per set of ids and job, shared by every character with them (<see cref="AppearanceCatalog"/>).
/// </summary>
public sealed class CharacterLook
{
    private static readonly IReadOnlyList<IReadOnlyList<ThemeColor>> s_skin = new IReadOnlyList<ThemeColor>[] { RecolorPaint.Parse(AppearancePaint.Skin) };
    private static readonly IReadOnlyList<IReadOnlyList<ThemeColor>> s_cloth = new IReadOnlyList<ThemeColor>[] { RecolorPaint.Parse(AppearancePaint.Cloth) };
    private static readonly IReadOnlyList<IReadOnlyList<ThemeColor>> s_hair = EachAFamily(AppearancePaint.Hair);

    internal CharacterLook(AppearanceRecord ids, ModelResolution body, WornPiece? face, WornPiece? hair, ThemeColor? skin, ThemeColor? hairColor, ThemeColor? cloth, IReadOnlyList<JobPart> jobParts)
    {
        Ids = ids;
        Body = body;
        Face = face;
        Hair = hair;
        Skin = skin;
        HairColor = hairColor;
        Cloth = cloth;
        JobParts = jobParts;
        ColorKey = (skin?.ToString() ?? "-") + (hairColor?.ToString() ?? "-") + (cloth?.ToString() ?? "-");
        BodyColors = Painted(body.Asset, true);
        FaceColors = Painted(face?.Model.Asset, false);
        HairColors = hairColor is not { } color ? Recolor.None
            : hair?.Model.Asset is { } asset && asset.Recolor.Names(AppearanceColorRoles.Hair) ? new Recolor.Builder().Add(asset.Recolor.Families(AppearanceColorRoles.Hair, s_hair), color).Build()
            : Recolor.WholeMesh(color);
    }

    /// <summary>The ids drawn: the server's, with a default in the place of each the bundle does not have.</summary>
    public AppearanceRecord Ids { get; }

    /// <summary>The body's model.</summary>
    public ModelResolution Body { get; }

    /// <summary>The face, or null for a body without one.</summary>
    public WornPiece? Face { get; }

    /// <summary>The hairstyle, or null.</summary>
    public WornPiece? Hair { get; }

    /// <summary>The skin tone.</summary>
    public ThemeColor? Skin { get; }

    /// <summary>The hair colour.</summary>
    public ThemeColor? HairColor { get; }

    /// <summary>The job's cloth colour, or null to leave the cloth as painted.</summary>
    public ThemeColor? Cloth { get; }

    /// <summary>The job's pieces for this gender.</summary>
    public IReadOnlyList<JobPart> JobParts { get; }

    /// <summary>The colours as text, part of the merged mesh's cache key.</summary>
    public string ColorKey { get; }

    /// <summary>What the body's painted colours become.</summary>
    public Recolor BodyColors { get; }

    /// <summary>What the face's painted colours become.</summary>
    public Recolor FaceColors { get; }

    /// <summary>What the hairstyle's colours become.</summary>
    public Recolor HairColors { get; }

    /// <summary>The colours a part takes.</summary>
    public Recolor ColorsOf(PartRole role) => role == PartRole.Face ? FaceColors : role == PartRole.Hair ? HairColors : Recolor.None;

    private Recolor Painted(ThemeAsset? asset, bool body)
    {
        var paint = asset?.Recolor ?? RecolorPaint.Contract;
        var builder = new Recolor.Builder();
        if (Skin is { } skin) builder.Add(paint.Families(AppearanceColorRoles.Skin, s_skin), skin);
        if (HairColor is { } hair) builder.Add(paint.Families(AppearanceColorRoles.Hair, s_hair), hair);
        if (body && Cloth is { } cloth) builder.Add(paint.Families(AppearanceColorRoles.Cloth, s_cloth), cloth);
        return builder.Build();
    }

    private static IReadOnlyList<IReadOnlyList<ThemeColor>> EachAFamily(IReadOnlyList<string> colors)
    {
        var families = new List<IReadOnlyList<ThemeColor>>();
        foreach (var color in RecolorPaint.Parse(colors)) families.Add(new[] { color });
        return families;
    }
}

/// <summary>
/// Turns the appearance ids of the server (T-705) and a job into a <see cref="CharacterLook"/> through the bundle and the theme (T-706, §7.5.1, §7.7). It never throws:
/// an id the bundle does not have is drawn as the gender's default; a model no theme has is drawn as the gender's default face or hairstyle, and when that is missing
/// too as the missing model; a body no theme has means the character is drawn by its job's model, as before T-706 (<see cref="Resolve"/> returns null). Each problem
/// is logged once. Looks are cached, so characters with the same ids and job share one object.
/// </summary>
public sealed class AppearanceCatalog
{
    private readonly ThemeCatalog _catalog;
    private readonly EquipmentCatalog _equipment;
    private readonly DisplayBundle _bundle;
    private readonly int _limit;
    private readonly Dictionary<(AppearanceRecord Ids, string Job), CharacterLook?> _looks = new Dictionary<(AppearanceRecord, string), CharacterLook?>();

    /// <summary>Creates a catalog that remembers at most <paramref name="limit"/> looks (it starts over when it is full: a look is cheap to make again).</summary>
    public AppearanceCatalog(ThemeCatalog catalog, EquipmentCatalog equipment, DisplayBundle bundle, int limit = 256)
    {
        _catalog = catalog;
        _equipment = equipment;
        _bundle = bundle;
        _limit = Math.Max(1, limit);
    }

    /// <summary>Looks remembered now (for measuring).</summary>
    public int Count => _looks.Count;

    /// <summary>The look for appearance ids and a job, or null when the character is drawn by its job's model: no ids, no options in the bundle, or no body in any theme.</summary>
    public CharacterLook? Resolve(AppearanceRecord? ids, string? jobId)
    {
        if (ids is null || _bundle.Appearance is null) return null;
        var key = (ids, jobId ?? string.Empty);
        if (_looks.TryGetValue(key, out var known)) return known;
        if (_looks.Count >= _limit) _looks.Clear();
        return _looks[key] = Make(_bundle.Appearance, ids, jobId);
    }

    private CharacterLook? Make(ClientAppearance options, AppearanceRecord ids, string? jobId)
    {
        var gender = Pick(options.Genders, g => g.Gender, ids.Gender, options.Genders.Count > 0 ? options.Genders[0].Gender : null, "gender");
        if (gender is null) return null;
        if (!_catalog.TryFind(gender.BodyModelAssetKey, out var path, out var asset))
        {
            _catalog.WarnOnce(gender.BodyModelAssetKey, "[theme] body '" + gender.BodyModelAssetKey + "': " + _catalog.WhyMissing(gender.BodyModelAssetKey) + "; drawing the job's model");
            return null;
        }
        var body = new ModelResolution(gender.BodyModelAssetKey, path, asset, ResolutionLevel.Exact);
        var face = Pick(gender.Faces, o => o.Id, ids.Face, gender.DefaultFaceId, "face");
        var hair = Pick(gender.Hairstyles, o => o.Id, ids.Hair, gender.DefaultHairstyleId, "hairstyle");
        var skin = Pick(options.SkinTones, o => o.Id, ids.Skin, options.DefaultSkinToneId, "skin tone");
        var hairColor = Pick(options.HairColors, o => o.Id, ids.HairColor, options.DefaultHairColorId, "hair colour");
        var job = _bundle.JobLookOf(jobId);
        var pieces = new List<JobPart>();
        foreach (var piece in job.Pieces)
            if (piece.Gender is null || piece.Gender == gender.Gender) pieces.Add(new JobPart(_equipment.ResolveKey(piece.ModelAssetKey, asset.Rig), piece.Slot));
        return new CharacterLook(
            new AppearanceRecord(gender.Gender, skin?.Id ?? ids.Skin, face?.Id ?? ids.Face, hair?.Id ?? ids.Hair, hairColor?.Id ?? ids.HairColor),
            body, Part(face, gender.Faces, gender.DefaultFaceId, asset.Rig), Part(hair, gender.Hairstyles, gender.DefaultHairstyleId, asset.Rig),
            Color(skin?.Color), Color(hairColor?.Color), Color(job.ClothColor), pieces);
    }

    /// <summary>The option with the id, else the default one (one warning), else the first; null only when there is none.</summary>
    private T? Pick<T>(IReadOnlyList<T> options, Func<T, string> idOf, string id, string? fallback, string what) where T : class
    {
        T? standIn = null;
        for (var i = 0; i < options.Count; i++)
        {
            if (idOf(options[i]) == id) return options[i];
            if (idOf(options[i]) == fallback) standIn = options[i];
        }
        if (standIn is null && options.Count > 0) standIn = options[0];
        _catalog.WarnOnce("look:" + id, "[look] the bundle has no " + what + " '" + id + "' here; drawing " + (standIn is null ? "none" : "'" + idOf(standIn) + "'"));
        return standIn;
    }

    private WornPiece? Part(ClientAppearanceOption? option, IReadOnlyList<ClientAppearanceOption> options, string defaultId, string? rig)
    {
        if (option is null) return null;
        if (_equipment.FindPart(option.ModelAssetKey, rig, out var why) is { } found) return found;
        foreach (var other in options)
        {
            if (other.Id != defaultId || other.Id == option.Id) continue;
            if (_equipment.FindPart(other.ModelAssetKey, rig, out _) is not { } standIn) break;
            _catalog.WarnOnce(option.ModelAssetKey, "[theme] '" + option.ModelAssetKey + "' of " + option.Id + ": " + why + "; drawing " + other.Id);
            return standIn;
        }
        return _equipment.MissingPart(option.ModelAssetKey, why);
    }

    private static ThemeColor? Color(string? hex) => hex is not null && ThemeColor.TryParse(hex, out var color) ? color : (ThemeColor?)null;
}

/// <summary>One worn item: its piece and the equipment slots the bundle says it takes.</summary>
public readonly record struct WornItem(WornPiece Piece, IReadOnlyList<string> Slots);

/// <summary>
/// What is merged into a character's body, in order (T-706): the face, the hairstyle unless something worn hides it, the job's pieces unless equipment stands in their
/// slot, and the worn equipment. Outfits (T-708) join here, as parts that replace job pieces and equipment by slot.
/// </summary>
public static class DressPlan
{
    /// <summary>Fills <paramref name="into"/> (cleared first). Without a look only the equipment is merged, as before T-706.</summary>
    public static void Fill(CharacterLook? look, IReadOnlyList<WornItem> worn, List<DressPart> into)
    {
        into.Clear();
        if (look is not null)
        {
            if (look.Face is not null) into.Add(new DressPart(look.Face, PartRole.Face));
            var hidden = false;
            for (var i = 0; i < worn.Count; i++) hidden |= worn[i].Piece.HidesHair;
            for (var i = 0; i < look.JobParts.Count; i++)
                hidden |= look.JobParts[i].Piece.HidesHair && !Taken(look.JobParts[i].Slot, worn);
            if (look.Hair is not null && !hidden) into.Add(new DressPart(look.Hair, PartRole.Hair));
            for (var i = 0; i < look.JobParts.Count; i++)
                if (!Taken(look.JobParts[i].Slot, worn)) into.Add(new DressPart(look.JobParts[i].Piece, PartRole.Job));
        }
        for (var i = 0; i < worn.Count; i++) into.Add(new DressPart(worn[i].Piece, PartRole.Gear));
    }

    private static bool Taken(string? slot, IReadOnlyList<WornItem> worn)
    {
        if (slot is null) return false;
        for (var i = 0; i < worn.Count; i++)
            for (var s = 0; s < worn[i].Slots.Count; s++)
                if (worn[i].Slots[s] == slot) return true;
        return false;
    }
}
