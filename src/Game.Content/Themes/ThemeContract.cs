using Game.Shared.Vocabulary;

namespace Game.Content.Themes;

/// <summary>
/// The theme contract's checkable numbers and names (T-224, docs/THEMES.md): what every theme model must meet so the client can draw it. These are engine rules and
/// ARCHITECTURE §9 budgets, not game content; the rig names, clips and attach points a theme picks live in its manifest.
/// </summary>
public static class ThemeContract
{
    /// <summary>The rig every character body, face and garment shares, so gear and appearance attach (ARCHITECTURE §7.5).</summary>
    public const string HumanoidRig = "humanoid";

    /// <summary>The bones the shared humanoid rig must have: the attach points gear uses, the root (the default attach point) and the limbs the clips move.</summary>
    public static readonly IReadOnlyList<string> HumanoidBones = new[] { "hips", "spine", "head", "arm_l", "hand_l", "arm_r", "hand_r", "leg_l", "leg_r" };

    /// <summary>The animation ids every animation library must map: the clips the client plays (idle and walk loop; the rest play once).</summary>
    public static readonly IReadOnlyList<string> RequiredClips = new[] { AnimationIds.Idle, AnimationIds.Walk, AnimationIds.Attack, AnimationIds.Hit, AnimationIds.Die };

    /// <summary>The animation ids the client loops (ARCHITECTURE §7.6); it sets the loop mode itself, so a file's loop flags do not matter.</summary>
    public static readonly IReadOnlyList<string> LoopingClips = new[] { AnimationIds.Idle, AnimationIds.Walk };

    /// <summary>§9: a character's LOD0 (the nearest 8 characters).</summary>
    public const int CharacterLod0Triangles = 5000;

    /// <summary>§9: a character's LOD1 (everyone else); a model without LOD1 draws its LOD0 there.</summary>
    public const int CharacterLod1Triangles = 2000;

    /// <summary>A worn piece or appearance part (merged into its character's mesh, so it spends the character's budget; a T-224 decision).</summary>
    public const int PieceTriangles = 1000;

    /// <summary>A prop or ground item (many can lie on screen, so the LOD1 budget).</summary>
    public const int PropTriangles = 2000;

    /// <summary>§9: one material per model, so a character is one draw call (two with the outline).</summary>
    public const int MaxMaterials = 1;

    /// <summary>§9: the largest texture side.</summary>
    public const int MaxTextureSize = 1024;

    /// <summary>
    /// The heights a living model may have at rest, in world units (1 cell = 1 unit). Outside them the units are wrong (centimetres, inches) and the mapping
    /// needs a <c>scale</c>.
    /// </summary>
    public const float MinLivingHeight = 0.1f, MaxLivingHeight = 8f;

    /// <summary>The largest a piece or prop may be, in world units.</summary>
    public const float MaxPieceSize = 4f;

    /// <summary>How far below or above the ground a living model's lowest point may be, as a share of its height (origin on the ground, ARCHITECTURE §7.7).</summary>
    public const float GroundTolerance = 0.35f;

    /// <summary>A model drawn with a variant's <c>scale</c> must stay between these.</summary>
    public const double MinVariantScale = 0.1, MaxVariantScale = 4;

    /// <summary>How many variants may stand on each other.</summary>
    public const int MaxVariantDepth = 4;

    /// <summary>The key prefixes a variant may have: models drawn whole (worn pieces merge into their character's mesh, where a tint or scale cannot apply).</summary>
    public static readonly IReadOnlyList<string> VariantPrefixes = new[] { "model.monster.", "model.npc.", "model.char.", "model.prop." };

    /// <summary>Keys whose model is a worn piece or an appearance part rather than a whole entity.</summary>
    public static bool IsPiece(string key, string? attach, string? skinned, string? rig, string? anims) =>
        key.StartsWith("model.equip.", StringComparison.Ordinal) || attach is not null || skinned is not null || (rig is not null && anims is null);
}
