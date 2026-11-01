using Game.Shared.Definitions;
using Game.Shared.Vocabulary;

namespace Game.Content.Schema;

/// <summary>
/// Maps definition members to the vocabulary and id conventions they follow (VOCABULARY.md).
/// Keyed by member name, so a new field that follows a convention needs no code change. A field that
/// looks like a reference or asset key but matches no rule fails schema generation, so it cannot ship unchecked.
/// </summary>
internal static class SchemaRules
{
    /// <summary>Property name → vocabulary its string value (or list items) must come from.</summary>
    public static readonly IReadOnlyDictionary<string, VocabularySet> Enums = new Dictionary<string, VocabularySet>(StringComparer.Ordinal)
    {
        ["Element"] = Elements.All,
        ["Race"] = MonsterRaces.All,
        ["Size"] = MonsterSizes.All,
        ["Slots"] = EquipSlots.All,
        ["WeaponType"] = WeaponTypes.All,
        ["WeaponTypes"] = WeaponTypes.All,
        ["AnimationId"] = AnimationIds.All,
        ["Stat"] = Stats.All,
        ["Facing"] = Directions.All,
    };

    /// <summary>(declaring type, property) → vocabulary per dictionary nesting level, for the keys.</summary>
    public static readonly IReadOnlyDictionary<(Type Type, string Property), VocabularySet[]> DictionaryKeys =
        new Dictionary<(Type, string), VocabularySet[]>
        {
            [(typeof(MonsterDef), nameof(MonsterDef.Stats))] = new[] { Stats.All },
            [(typeof(FormulaSetDef), nameof(FormulaSetDef.Derived))] = new[] { DerivedStats.All },
            [(typeof(ElementTableDef), nameof(ElementTableDef.Multipliers))] = new[] { Elements.All, Elements.All },
            [(typeof(AppearanceDef), nameof(AppearanceDef.Genders))] = new[] { Genders.All },
        };

    private static readonly (string Suffix, string Prefix)[] ReferenceSuffixes =
    {
        ("DropTableId", IdPrefixes.Drop),
        ("ExpTableId", IdPrefixes.Exp),
        ("DialogueId", IdPrefixes.Dialogue),
        ("MonsterId", IdPrefixes.Monster),
        ("StatusId", IdPrefixes.Status),
        ("SkillId", IdPrefixes.Skill),
        ("QuestId", IdPrefixes.Quest),
        ("ItemId", IdPrefixes.Item),
        ("NpcId", IdPrefixes.Npc),
        ("JobId", IdPrefixes.Job),
        ("MapId", IdPrefixes.Map),
        ("FaceId", AppearanceIdPrefixes.Face),
        ("HairstyleId", AppearanceIdPrefixes.Hair),
        ("SkinToneId", AppearanceIdPrefixes.Skin),
        ("HairColorId", AppearanceIdPrefixes.HairColor),
    };

    private static readonly IReadOnlyDictionary<string, string[]> AssetKinds = new Dictionary<string, string[]>(StringComparer.Ordinal)
    {
        ["Icon"] = new[] { AssetKeyPrefixes.Icon },
        ["Model"] = new[] { AssetKeyPrefixes.Model },
        ["BodyModel"] = new[] { AssetKeyPrefixes.Model },
        ["Visual"] = new[] { AssetKeyPrefixes.Model },
        ["Ground"] = new[] { AssetKeyPrefixes.Model, AssetKeyPrefixes.Sprite },
        ["Scene"] = new[] { AssetKeyPrefixes.Scene },
        ["Music"] = new[] { AssetKeyPrefixes.Music },
        ["Portrait"] = new[] { AssetKeyPrefixes.Portrait },
        ["Vfx"] = new[] { AssetKeyPrefixes.Vfx },
        ["Sfx"] = new[] { AssetKeyPrefixes.Sfx },
    };

    /// <summary>Pattern for the <c>Id</c> property of a registry definition, or null if the type has no id prefix.</summary>
    public static string? IdPattern(Type declaringType) =>
        DefinitionIdPrefixes.ByType.TryGetValue(declaringType, out var prefix) ? IdPattern(prefix)
        : OptionIdPrefixes.TryGetValue(declaringType, out var optionPrefix) ? IdPattern(optionPrefix) : null;

    /// <summary>Appearance option records whose own <c>Id</c> has one fixed prefix. (A colour option is shared by two palettes, so the validator checks it.)</summary>
    private static readonly IReadOnlyDictionary<Type, string> OptionIdPrefixes = new Dictionary<Type, string>
    {
        [typeof(FaceOptionDef)] = AppearanceIdPrefixes.Face,
        [typeof(HairstyleOptionDef)] = AppearanceIdPrefixes.Hair,
    };

    /// <summary>Pattern for a reference to another definition, from the property name; null when the name is not a reference.</summary>
    public static string? ReferencePattern(string propertyName)
    {
        if (propertyName == nameof(IDefinition.Id)) return null; // the owning record's own id, not a reference
        var name = propertyName.EndsWith("Ids", StringComparison.Ordinal) ? propertyName[..^1] : propertyName;
        if (!name.EndsWith("Id", StringComparison.Ordinal)) return null;
        foreach (var (suffix, prefix) in ReferenceSuffixes)
            if (name.EndsWith(suffix, StringComparison.Ordinal)) return IdPattern(prefix);
        throw new InvalidOperationException($"Property '{propertyName}' looks like an id reference but has no rule in SchemaRules.ReferenceSuffixes.");
    }

    /// <summary>Pattern for an asset key, from the property name; null when the name is not an asset key.</summary>
    public static string? AssetKeyPattern(string propertyName)
    {
        if (!propertyName.EndsWith("AssetKey", StringComparison.Ordinal)) return null;
        var kind = propertyName[..^"AssetKey".Length];
        if (!AssetKinds.TryGetValue(kind, out var prefixes))
            throw new InvalidOperationException($"Property '{propertyName}' looks like an asset key but has no rule in SchemaRules.AssetKinds.");
        var alternation = prefixes.Length == 1 ? Escape(prefixes[0]) : "(" + string.Join("|", prefixes.Select(Escape)) + ")";
        return "^" + alternation + "[a-z0-9_]+(\\.[a-z0-9_]+)*$";
    }

    private static string IdPattern(string prefix) => "^" + Escape(prefix) + IdPrefixes.LocalPartPattern + "$";

    private static string Escape(string prefix) => prefix.Replace(".", "\\.", StringComparison.Ordinal);
}
