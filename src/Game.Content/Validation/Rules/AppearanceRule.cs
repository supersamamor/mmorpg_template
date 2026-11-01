using System.Text.RegularExpressions;
using Game.Shared.Definitions;
using Game.Shared.Vocabulary;

namespace Game.Content.Validation.Rules;

/// <summary>
/// Checks <c>appearance.yaml</c> (character-creation options, ARCHITECTURE §7.5.1): every <see cref="Genders"/> term is present
/// exactly once and no other key is; option lists are not empty; option ids carry the right prefix and are unique in the file;
/// colours are <c>#RRGGBB</c>; defaults point at existing options (a gender's defaults at that gender's own options);
/// and every asset key is a <c>model.*</c> key in the theme manifest.
/// <para>
/// <b>Rig rule.</b> A gender's body model must declare a <c>rig</c>. Each face and hairstyle model of that gender must either declare
/// the <b>same rig</b> as the body, or declare no rig and be attached at the <c>head</c> attach point (<c>"attach": "head"</c>), like
/// headgear. Anything else (another rig, or a rig-less model that is not head-attached) is an error. The rig and attach checks need
/// the manifest metadata and are skipped when the key set was built without it.
/// </para>
/// </summary>
public sealed class AppearanceRule : IValidationRule
{
    private const string File = "appearance.yaml";
    private static readonly Regex Hex = new("^#[0-9A-Fa-f]{6}$", RegexOptions.Compiled);
    private static readonly Regex IdTail = new("^[a-z0-9_]+$", RegexOptions.Compiled);

    /// <inheritdoc />
    public string Name => "appearance";

    /// <inheritdoc />
    public void Validate(ValidationContext context)
    {
        var a = context.Database.Appearance;
        var seen = new HashSet<string>(StringComparer.Ordinal);

        // Known genders in vocabulary order, then unknown keys, so reports do not depend on YAML order.
        foreach (var term in Genders.All)
        {
            if (!a.Genders.ContainsKey(term))
                Error(context, "genders", ValidationCodes.AppearanceGender, $"appearance.yaml is missing gender '{term}'.");
        }

        var ordered = Genders.All.Where(a.Genders.ContainsKey).Concat(a.Genders.Keys.Where(k => !Genders.All.Contains(k)).OrderBy(k => k, StringComparer.Ordinal));
        foreach (var gender in ordered)
        {
            var def = a.Genders[gender];
            var path = "genders." + gender;
            if (!Genders.All.Contains(gender))
            {
                Error(context, path, ValidationCodes.AppearanceGender,
                    $"appearance.yaml gender '{gender}' is not a gender ({string.Join(", ", Genders.All.Ordered)}).");
                continue;
            }
            Gender(context, path, def, seen);
        }

        Palette(context, "skin_tones", a.SkinTones, AppearanceIdPrefixes.Skin, seen);
        Default(context, "default_skin_tone_id", a.DefaultSkinToneId, a.SkinTones.Select(o => o.Id), "skin tone");
        Palette(context, "hair_colors", a.HairColors, AppearanceIdPrefixes.HairColor, seen);
        Default(context, "default_hair_color_id", a.DefaultHairColorId, a.HairColors.Select(o => o.Id), "hair colour");
    }

    private static void Gender(ValidationContext context, string path, GenderAppearanceDef def, HashSet<string> seen)
    {
        var bodyRig = Model(context, path + ".body_model_asset_key", def.BodyModelAssetKey);
        if (context.HasAssetInfo && context.AssetKeys.Contains(def.BodyModelAssetKey) && bodyRig is null)
            Error(context, path + ".body_model_asset_key", ValidationCodes.AppearanceRig,
                $"appearance.yaml body model '{def.BodyModelAssetKey}' has no rig in the asset manifest.");

        if (def.Faces.Count == 0)
            Error(context, path + ".faces", ValidationCodes.AppearanceEmpty, $"appearance.yaml {path}.faces must list at least one face.");
        if (def.Hairstyles.Count == 0)
            Error(context, path + ".hairstyles", ValidationCodes.AppearanceEmpty, $"appearance.yaml {path}.hairstyles must list at least one hairstyle.");

        for (var i = 0; i < def.Faces.Count; i++)
        {
            var o = def.Faces[i];
            var p = $"{path}.faces[{i}]";
            Id(context, p + ".id", o.Id, AppearanceIdPrefixes.Face, seen);
            FitsBody(context, p + ".model_asset_key", o.ModelAssetKey, bodyRig, "face");
        }
        for (var i = 0; i < def.Hairstyles.Count; i++)
        {
            var o = def.Hairstyles[i];
            var p = $"{path}.hairstyles[{i}]";
            Id(context, p + ".id", o.Id, AppearanceIdPrefixes.Hair, seen);
            FitsBody(context, p + ".model_asset_key", o.ModelAssetKey, bodyRig, "hairstyle");
        }

        Default(context, path + ".default.face_id", def.Default.FaceId, def.Faces.Select(o => o.Id), "face of this gender");
        Default(context, path + ".default.hairstyle_id", def.Default.HairstyleId, def.Hairstyles.Select(o => o.Id), "hairstyle of this gender");
    }

    private static void Palette(ValidationContext context, string path, IReadOnlyList<ColorOptionDef> list, string prefix, HashSet<string> seen)
    {
        if (list.Count == 0)
            Error(context, path, ValidationCodes.AppearanceEmpty, $"appearance.yaml {path} must list at least one colour.");
        for (var i = 0; i < list.Count; i++)
        {
            var p = $"{path}[{i}]";
            Id(context, p + ".id", list[i].Id, prefix, seen);
            if (!Hex.IsMatch(list[i].Color))
                Error(context, p + ".color", ValidationCodes.AppearanceColor,
                    $"appearance.yaml {p}.color '{list[i].Color}' must be a hex colour like #RRGGBB.");
        }
    }

    private static void Id(ValidationContext context, string path, string id, string prefix, HashSet<string> seen)
    {
        if (!id.StartsWith(prefix, StringComparison.Ordinal) || !IdTail.IsMatch(id.Substring(prefix.Length)))
            Error(context, path, ValidationCodes.AppearanceId,
                $"appearance.yaml id '{id}' must be '{prefix}' followed by lowercase letters, digits and '_'.");
        else if (!seen.Add(id))
            Error(context, path, ValidationCodes.AppearanceId, $"appearance.yaml has a duplicate id '{id}'.");
    }

    private static void Default(ValidationContext context, string path, string id, IEnumerable<string> options, string what)
    {
        if (!options.Contains(id, StringComparer.Ordinal))
            Error(context, path, ValidationCodes.AppearanceDefault, $"appearance.yaml {path} '{id}' is not a {what}.");
    }

    /// <summary>Checks that <paramref name="key"/> exists and is a model; returns its rig (null if none or unknown).</summary>
    private static string? Model(ValidationContext context, string path, string key)
    {
        if (string.IsNullOrEmpty(key))
        {
            Error(context, path, ValidationCodes.AssetEmpty, $"appearance.yaml must set '{path}'.");
            return null;
        }
        if (!key.StartsWith(AssetKeyPrefixes.Model, StringComparison.Ordinal))
            Error(context, path, ValidationCodes.AppearanceRig, $"appearance.yaml {path} '{key}' must be a '{AssetKeyPrefixes.Model}' asset key.");
        if (!context.AssetKeys.Contains(key))
        {
            Error(context, path, ValidationCodes.AssetMissing, $"Asset key '{key}' is not in the theme's asset-manifest.json");
            return null;
        }
        return context.InfoOf(key)?.Rig;
    }

    private static void FitsBody(ValidationContext context, string path, string key, string? bodyRig, string what)
    {
        var rig = Model(context, path, key);
        if (!context.HasAssetInfo || !context.AssetKeys.Contains(key) || bodyRig is null) return; // missing keys and rigs are reported elsewhere
        var attach = context.InfoOf(key)?.Attach;
        if (rig is not null)
        {
            if (!string.Equals(rig, bodyRig, StringComparison.Ordinal))
                Error(context, path, ValidationCodes.AppearanceRig,
                    $"appearance.yaml {what} model '{key}' uses rig '{rig}' but the body uses rig '{bodyRig}'.");
        }
        else if (attach != AttachPoints.Head)
        {
            Error(context, path, ValidationCodes.AppearanceRig,
                $"appearance.yaml {what} model '{key}' has no rig and is not attached at '{AttachPoints.Head}' in the asset manifest, so it cannot fit the '{bodyRig}' body.");
        }
    }

    private static void Error(ValidationContext context, string path, string code, string message) =>
        context.ErrorInFile(File, path, code, message);
}
