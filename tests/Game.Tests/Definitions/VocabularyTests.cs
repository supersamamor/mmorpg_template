using Game.Shared.Definitions;
using Game.Shared.Vocabulary;

namespace Game.Tests.Definitions;

public class VocabularyTests
{
    // Expected values are copied from docs/VOCABULARY.md, in document order.
    public static TheoryData<string, VocabularySet, string[]> Categories => new()
    {
        { "Id namespaces", IdPrefixes.All, ["job.", "skill.", "item.", "monster.", "status.", "quest.", "npc.", "dialogue.", "map.", "drop.", "exp."] },
        { "Asset key prefixes", AssetKeyPrefixes.All, ["model.", "anim.", "scene.", "tex.", "mat.", "vfx.", "sprite.", "icon.", "ui.", "portrait.", "sfx.", "music.", "font.", "ambience."] },
        { "Audio buses", AudioBuses.All, ["music", "sfx", "ui", "ambient"] },
        { "Audio default categories", AudioCategories.Fixed, ["monster.aggro", "monster.hit", "monster.die", "item.use", "item.equip", "ui.tap", "ui.open", "ui.close", "ui.error"] },
        { "Directions", Directions.All, ["n", "ne", "e", "se", "s", "sw", "w", "nw"] },
        { "Cell flags", CellFlags.All, ["walkable", "blocks_los"] },
        { "Map object types", MapObjectTypes.All, ["spawn", "warp", "npc", "zone"] },
        { "Zone kinds", ZoneKinds.All, ["safe", "pvp"] },
        { "Appearance option namespaces", AppearanceIdPrefixes.All, ["face.", "hair.", "skin.", "haircolor."] },
        { "Genders", Genders.All, ["male", "female"] },
        { "Attach points", AttachPoints.All, ["head", "face", "mouth", "back", "hand_r", "hand_l"] },
        { "Animation ids", AnimationIds.All, ["idle", "walk", "attack", "cast", "hit", "die", "sit", "pick_up"] },
        { "Primary stats", PrimaryStats.All, ["str", "agi", "vit", "int", "dex", "luk"] },
        { "Derived stats", DerivedStats.All, ["max_hp", "max_sp", "atk", "matk", "def", "mdef", "hit", "flee", "crit", "aspd", "move_speed", "cast_time_mod", "hp_regen", "sp_regen", "weight_limit"] },
        { "Modifier ops", ModifierOps.All, ["flat", "percent", "override"] },
        { "Elements", Elements.All, ["neutral", "fire", "water", "earth", "wind", "holy", "shadow", "poison", "ghost", "undead"] },
        { "Races", MonsterRaces.All, ["formless", "beast", "plant", "insect", "fish", "demon", "undead", "humanoid", "dragon", "angel"] },
        { "Sizes", MonsterSizes.All, ["small", "medium", "large"] },
        { "Effect types", EffectTypes.All, ["damage", "heal", "restore_sp", "apply_status", "remove_status", "modify_stat", "knockback", "teleport", "summon", "area"] },
        { "Equipment slots", EquipSlots.All, ["head_top", "head_mid", "head_low", "body", "weapon", "shield", "garment", "footgear", "accessory_1", "accessory_2"] },
        { "Weapon types", WeaponTypes.All, ["unarmed", "dagger", "sword_1h", "sword_2h", "spear", "axe", "mace", "staff", "bow", "knuckle"] },
        { "Item kinds", ItemKinds.All, ["consumable", "equip", "card", "material", "quest", "ammo"] },
        { "Quest objective types", QuestObjectiveTypes.All, ["kill", "collect", "talk", "reach", "level", "job", "use_item", "equip"] },
        { "Reward types", RewardTypes.All, ["exp", "item", "currency", "skill_point", "stat_point", "unlock_job", "status"] },
        { "Monster AI states", AiStates.All, ["idle", "wander", "chase", "attack", "return"] },
        { "Aggro reasons", AggroReasons.All, ["sighted", "attacked", "target_gone", "leash", "unreachable"] },
        { "Events", EventNames.All, ["EntitySpawned", "EntityDied", "MonsterKilled", "DamageDealt", "ItemAcquired", "ItemUsed", "ItemEquipped", "NpcTalked", "ZoneEntered", "LevelUp", "JobLevelUp", "JobChanged", "SkillLearned", "QuestStarted", "QuestStepCompleted", "QuestCompleted", "StatusApplied", "StatusExpired", "EntityEnteredMap", "EntityLeftMap", "WarpRejected", "MovementStarted", "CellReached", "MovementStopped", "MoveRejected", "AggroGained", "AggroLost", "MonsterReturned", "AttackRequested", "AttackRefused", "AttackStopped", "ExpGained", "ExpLost", "PlayerRespawned", "ItemDropped", "ItemExpired"] },
        { "Attack reasons", AttackReasons.All, ["attacker_dead", "target_dead", "target_gone", "out_of_range", "no_line_of_sight", "safe_zone", "invalid_target", "unreachable", "cancelled"] },
        { "Hit results", HitResults.All, ["hit", "crit", "miss"] },
        { "Currency", Currencies.All, ["currency"] },
        { "Formula functions", FormulaFunctions.All, ["min", "max", "floor", "ceil", "round", "clamp", "pow", "sqrt"] },
    };

    [Theory]
    [MemberData(nameof(Categories))]
    public void Category_matches_vocabulary_doc_in_order(string category, VocabularySet set, string[] expected)
    {
        set.Ordered.ShouldBe(expected, category);
        set.Count.ShouldBe(expected.Length);
        foreach (var value in expected) set.Contains(value).ShouldBeTrue(value);
    }

    [Fact]
    public void Sets_are_ordinal_and_reject_unknown_values()
    {
        Elements.All.Contains("Fire").ShouldBeFalse();
        Elements.All.Contains("lightning").ShouldBeFalse();
        Elements.All.Contains(null!).ShouldBeFalse();
    }

    [Fact]
    public void Sets_are_IReadOnlySet_on_modern_targets()
    {
        IReadOnlySet<string> set = Directions.All;
        set.SetEquals(["n", "ne", "e", "se", "s", "sw", "w", "nw"]).ShouldBeTrue();
    }

    [Fact]
    public void Duplicate_terms_are_rejected()
    {
        Should.Throw<ArgumentException>(() => new VocabularySet("a", "b", "a"));
        Should.Throw<ArgumentException>(() => VocabularySet.Union(PrimaryStats.All, PrimaryStats.All));
    }

    [Fact]
    public void Stats_is_primary_then_derived()
    {
        Stats.All.Ordered.ShouldBe(PrimaryStats.All.Concat(DerivedStats.All));
    }

    [Fact]
    public void Formula_variables_cover_caster_target_and_skill()
    {
        foreach (var name in new[] { "base_level", "job_level", "weapon_atk", "refine", "str", "max_hp", "move_speed", "level", "t_def", "t_vit", "t_level" })
            FormulaVariables.All.Contains(name).ShouldBeTrue(name);

        FormulaVariables.All.Contains("foo").ShouldBeFalse();
        FormulaVariables.All.Contains("t_foo").ShouldBeFalse();
        foreach (var stat in Stats.All) FormulaVariables.Target.Contains("t_" + stat).ShouldBeTrue(stat);
    }

    [Fact]
    public void Enums_mirror_their_vocabulary_lists_in_snake_case()
    {
        Enum.GetNames<ItemKind>().Select(ToSnakeCase).ShouldBe(ItemKinds.All.Ordered);
        Enum.GetNames<ModifierOp>().Select(ToSnakeCase).ShouldBe(ModifierOps.All.Ordered);
    }

    private static string ToSnakeCase(string name) => System.Text.Json.JsonNamingPolicy.SnakeCaseLower.ConvertName(name);
}
