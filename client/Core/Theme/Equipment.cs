using System;
using System.Collections.Generic;

namespace Game.Client.Core.Theme;

/// <summary>How a worn piece is put on a character (ARCHITECTURE §7.5).</summary>
public enum WornMode
{
    /// <summary>A rigid model on a named attach point (a bone of the character's rig): weapon, shield, headgear.</summary>
    Attached,

    /// <summary>A mesh skinned to the character's rig, bound to its skeleton so it plays the same animation: body armour, garment, footgear.</summary>
    Skinned,
}

/// <summary>Where one worn item's model comes from and how it goes on. One object per visual key and rig, shared by every character wearing it.</summary>
public sealed class WornPiece
{
    internal WornPiece(string visualKey, ModelResolution model, WornMode mode, string? bone, bool fallback)
    {
        VisualKey = visualKey;
        Model = model;
        Mode = mode;
        Bone = bone;
        Fallback = fallback;
    }

    /// <summary>The visual asset key the bundle gave the item.</summary>
    public string VisualKey { get; }

    /// <summary>The model to load (the theme's missing model, or the built-in marker, for a key that did not resolve).</summary>
    public ModelResolution Model { get; }

    /// <summary>Attached or skinned.</summary>
    public WornMode Mode { get; }

    /// <summary>For an attached piece, the attach point (bone name) the manifest names; null means the default point (<see cref="AttachRules"/>).</summary>
    public string? Bone { get; }

    /// <summary>True when the key did not resolve and the missing model stands in for it (drawn small, at the default point).</summary>
    public bool Fallback { get; }
}

/// <summary>
/// Resolves worn items to pieces (T-403, ARCHITECTURE §7.5, §7.7): item id → the bundle's <c>visual</c> asset key → the theme chain (active theme, then
/// <c>default</c>). The manifest entry says <c>attach</c> (a bone) or <c>skinned</c> (a rig). A key that no theme has, or an entry that is wrong (neither or
/// both of <c>attach</c> and <c>skinned</c>, or skinned to another rig than the character's) falls back to the theme's missing model on the default attach
/// point, with one warning per key, never a crash. Resolutions are cached, so every character wearing the same item shares one object and one model path.
/// </summary>
public sealed class EquipmentCatalog
{
    private readonly ThemeCatalog _catalog;
    private readonly Func<string, string?> _visualKeyOf;
    private readonly Dictionary<(string Key, string? Rig), WornPiece> _pieces = new Dictionary<(string, string?), WornPiece>();

    /// <summary>Creates a resolver over the theme catalog.</summary>
    /// <param name="catalog">The theme chain.</param>
    /// <param name="visualKeyOf">An item id's worn visual asset key from the display bundle, or null when it has none.</param>
    public EquipmentCatalog(ThemeCatalog catalog, Func<string, string?> visualKeyOf)
    {
        _catalog = catalog;
        _visualKeyOf = visualKeyOf;
    }

    /// <summary>Pieces resolved so far (one per visual key and rig).</summary>
    public int Count => _pieces.Count;

    /// <summary>
    /// The piece for a worn item on a character whose model has <paramref name="rig"/> (null for a model without one). Null when the bundle gives the item no worn
    /// visual (one warning): nothing is drawn for it.
    /// </summary>
    public WornPiece? Resolve(string itemId, string? rig)
    {
        var key = _visualKeyOf(itemId);
        if (key is null)
        {
            _catalog.WarnOnce("item:" + itemId, "[theme] worn item '" + itemId + "' has no worn visual in the bundle; nothing is drawn for it");
            return null;
        }
        if (_pieces.TryGetValue((key, rig), out var cached)) return cached;
        return _pieces[(key, rig)] = Make(key, rig);
    }

    private WornPiece Make(string key, string? rig)
    {
        if (!_catalog.TryFind(key, out var path, out var asset)) return Missing(key, _catalog.WhyMissing(key));
        var model = new ModelResolution(key, path, asset, ResolutionLevel.Exact);
        if (asset.Attach is not null && asset.Skinned is null) return new WornPiece(key, model, WornMode.Attached, asset.Attach, false);
        if (asset.Skinned is not null && asset.Attach is null)
        {
            if (rig is not null && asset.Skinned == rig) return new WornPiece(key, model, WornMode.Skinned, null, false);
            return Missing(key, "it is skinned to rig '" + asset.Skinned + "' but the character's rig is '" + (rig ?? "none") + "'");
        }
        return Missing(key, asset.Attach is null ? "its entry says neither 'attach' nor 'skinned'" : "its entry says both 'attach' and 'skinned'");
    }

    private WornPiece Missing(string key, string why)
    {
        _catalog.WarnOnce(key, "[theme] worn asset '" + key + "': " + why + "; drawing the missing model at the default attach point");
        return new WornPiece(key, _catalog.ResolveMissing(), WornMode.Attached, null, true);
    }
}

/// <summary>Where an attached piece goes on a skeleton (T-403).</summary>
public static class AttachRules
{
    /// <summary>
    /// The default attach point: the skeleton's first bone (index 0, the root of the rig: the hips on the humanoid). A piece goes there when the manifest names no
    /// bone (a fallback) or the character's rig lacks the named bone; a character model without a skeleton takes it on its own root node (index -1).
    /// </summary>
    public const int DefaultBone = 0;

    /// <summary>
    /// The bone index for an attached piece: the named bone, or <see cref="DefaultBone"/> with one warning per model and bone when the rig lacks it; -1 (the
    /// model's root node) when the model has no skeleton.
    /// </summary>
    /// <param name="bone">The attach point the manifest names, or null.</param>
    /// <param name="boneCount">Bones in the character's skeleton (0 without one).</param>
    /// <param name="find">The index of a bone by name, or -1.</param>
    /// <param name="model">The character model's path, for the warning.</param>
    /// <param name="catalog">Raises the warning once.</param>
    public static int BoneFor(string? bone, int boneCount, Func<string, int> find, string model, ThemeCatalog catalog)
    {
        if (boneCount <= 0)
        {
            if (bone is not null) catalog.WarnOnce("bone:" + model + ":", "[theme] " + model + " has no skeleton; worn pieces sit on its root node");
            return -1;
        }
        if (bone is null) return DefaultBone;
        var index = find(bone);
        if (index >= 0) return index;
        catalog.WarnOnce("bone:" + model + ":" + bone, "[theme] " + model + " has no bone '" + bone + "'; pieces for it go on the default attach point (the rig's root bone)");
        return DefaultBone;
    }
}

/// <summary>
/// What a character wears now, and what changed (T-403). It re-dresses only when the entity's look counter (<c>EntityView.LookVersion</c>) moves, and then
/// reports only the pieces that changed: an item still worn stays on. Item ids may repeat (the same accessory twice); they are compared as a multiset.
/// </summary>
public sealed class Wardrobe
{
    private const int Unknown = int.MinValue;

    private readonly List<string> _worn = new List<string>();
    private readonly List<string> _scratch = new List<string>();
    private int _version = Unknown;

    /// <summary>The item ids worn now.</summary>
    public IReadOnlyList<string> Worn => _worn;

    /// <summary>Forget the counter (a pooled visual meets a new entity) but keep the pieces, so the next update swaps only what differs.</summary>
    public void Rebase() => _version = Unknown;

    /// <summary>
    /// Takes the entity's look. False, doing nothing, when <paramref name="version"/> is the one already seen. Otherwise fills <paramref name="added"/> and
    /// <paramref name="removed"/> (cleared first) and returns true, even if nothing differs. Allocates nothing once the lists have grown.
    /// </summary>
    public bool Update(int version, IReadOnlyList<string> worn, List<string> added, List<string> removed)
    {
        if (version == _version) return false;
        _version = version;
        added.Clear();
        removed.Clear();
        _scratch.Clear();
        _scratch.AddRange(_worn);
        for (var i = 0; i < worn.Count; i++)
        {
            var at = _scratch.IndexOf(worn[i]);
            if (at >= 0) _scratch.RemoveAt(at);
            else added.Add(worn[i]);
        }
        removed.AddRange(_scratch);
        _worn.Clear();
        for (var i = 0; i < worn.Count; i++) _worn.Add(worn[i]);
        return true;
    }
}

/// <summary>
/// Which clip a weapon type plays (T-403, ARCHITECTURE §7.6). A rig's animation library may map <c>&lt;animation id&gt;.&lt;weapon type&gt;</c> (for example
/// <c>attack.staff</c>, <c>idle.staff</c>) as well as the plain id; the weapon-specific clip wins where the library has it, else the generic one.
/// </summary>
public static class WeaponClips
{
    /// <summary>The library's entry name for an animation id held with a weapon type.</summary>
    public static string VariantId(string animationId, string weaponType) => animationId + "." + weaponType;

    /// <summary>
    /// The clip name for <paramref name="animationId"/> with <paramref name="weaponType"/> (null: none): the weapon's variant if the library maps it and
    /// <paramref name="exists"/> says the clip is there, else the generic clip if it exists, else null.
    /// </summary>
    public static string? NameFor(IReadOnlyDictionary<string, string> clips, string animationId, string? weaponType, Func<string, bool> exists)
    {
        if (weaponType is not null && clips.TryGetValue(VariantId(animationId, weaponType), out var special) && exists(special)) return special;
        return clips.TryGetValue(animationId, out var plain) && exists(plain) ? plain : null;
    }
}
