using System.Text.Json;
using Game.Client.Core.Bundle;
using Game.Client.Core.Theme;
using Game.Content.ClientExport;
using Game.Shared.Vocabulary;
using Game.Tests.World;

namespace Game.Tests.Client;

/// <summary>
/// Worn equipment on 3D characters (T-403, ARCHITECTURE §7.5–§7.7): attach or skin from the manifest, the fallback chain for missing keys and bones, re-dressing
/// only on a look-counter change and only the changed pieces, the two-handed case, clip choice by weapon type, and one shared resolution per key. Pure: no engine.
/// </summary>
public class EquipmentTests
{
    private const string Manifest = """
        {
          "model.char.hero": { "file": "models/hero.glb", "rig": "humanoid", "anims": "anim.humanoid" },
          "model.equip.blade": { "file": "models/blade.glb", "attach": "hand_r" },
          "model.equip.buckler": { "file": "models/buckler.glb", "attach": "hand_l" },
          "model.equip.staff": { "file": "models/staff.glb", "attach": "hand_r" },
          "model.equip.crest": { "file": "models/crest.glb", "attach": "crown" },
          "model.equip.vest": { "file": "models/vest.glb", "skinned": "humanoid" },
          "model.equip.saddle": { "file": "models/saddle.glb", "skinned": "quadruped" },
          "model.equip.odd": { "file": "models/odd.glb" },
          "model.equip.both": { "file": "models/both.glb", "attach": "hand_r", "skinned": "humanoid" },
          "model.equip.gone": { "file": "models/gone.glb", "attach": "head" },
          "model.missing": { "file": "models/missing.glb" },
          "anim.humanoid": { "file": "anims/humanoid.glb", "rig": "humanoid",
                             "clips": { "idle": "Idle", "attack": "Attack", "walk": "Walk", "attack.staff": "AttackTwoHand", "idle.staff": "IdleTwoHand", "attack.bow": "AttackBow" } },
          "fallbacks": { "missing": "model.missing" }
        }
        """;

    private static readonly Dictionary<string, string> Visuals = new(StringComparer.Ordinal)
    {
        ["item.blade"] = "model.equip.blade", ["item.blade_copy"] = "model.equip.blade", ["item.buckler"] = "model.equip.buckler",
        ["item.staff"] = "model.equip.staff", ["item.crest"] = "model.equip.crest", ["item.vest"] = "model.equip.vest",
        ["item.saddle"] = "model.equip.saddle", ["item.odd"] = "model.equip.odd", ["item.both"] = "model.equip.both",
        ["item.gone"] = "model.equip.gone", ["item.unknown"] = "model.equip.nowhere",
    };

    private sealed class Rig
    {
        public readonly List<string> Warnings = new();
        public readonly ThemeCatalog Catalog;
        public readonly EquipmentCatalog Equipment;

        public Rig(bool withMissingModel = true)
        {
            ThemeManifest.TryParse("default", Manifest, out var manifest, out var error).ShouldBeTrue(error);
            var files = new HashSet<string>(StringComparer.Ordinal);
            foreach (var key in manifest!.Keys)
                if (manifest.TryGet(key, out var asset) && asset.File is not null && key != "model.equip.gone" && (withMissingModel || key != "model.missing"))
                    files.Add(ThemeCatalog.PathOf("default", asset.File));
            Catalog = new ThemeCatalog(new[] { manifest }, files.Contains, Warnings.Add);
            Equipment = new EquipmentCatalog(Catalog, id => Visuals.TryGetValue(id, out var key) ? key : null);
        }
    }

    // ---- attach or skin ----

    [Fact]
    public void The_manifest_says_whether_a_piece_is_attached_to_a_bone_or_skinned_to_the_rig()
    {
        var rig = new Rig();

        var blade = rig.Equipment.Resolve("item.blade", "humanoid")!;
        blade.Mode.ShouldBe(WornMode.Attached);
        blade.Bone.ShouldBe(AttachPoints.HandR);
        blade.Model.Path.ShouldBe("themes/default/models/blade.glb");
        blade.Fallback.ShouldBeFalse();

        var buckler = rig.Equipment.Resolve("item.buckler", "humanoid")!;
        buckler.Mode.ShouldBe(WornMode.Attached);
        buckler.Bone.ShouldBe(AttachPoints.HandL);

        var vest = rig.Equipment.Resolve("item.vest", "humanoid")!;
        vest.Mode.ShouldBe(WornMode.Skinned);
        vest.Bone.ShouldBeNull();
        vest.Model.Path.ShouldBe("themes/default/models/vest.glb");
        rig.Warnings.ShouldBeEmpty();
    }

    // ---- fallbacks ----

    [Theory]
    [InlineData("item.unknown", "model.equip.nowhere", "no theme lists it")]
    [InlineData("item.gone", "model.equip.gone", "file is missing")]
    [InlineData("item.saddle", "model.equip.saddle", "rig 'quadruped'")]
    [InlineData("item.odd", "model.equip.odd", "neither")]
    [InlineData("item.both", "model.equip.both", "both")]
    public void A_missing_or_wrong_key_draws_the_missing_model_at_the_default_point_with_one_warning(string item, string key, string why)
    {
        var rig = new Rig();

        var piece = rig.Equipment.Resolve(item, "humanoid")!;
        rig.Equipment.Resolve(item, "humanoid");
        new Rig().Equipment.Resolve(item, "humanoid"); // another client: its own warning, not this one's

        piece.Fallback.ShouldBeTrue();
        piece.VisualKey.ShouldBe(key);
        piece.Mode.ShouldBe(WornMode.Attached);
        piece.Bone.ShouldBeNull("the default point");
        piece.Model.Key.ShouldBe("model.missing");
        piece.Model.Level.ShouldBe(ResolutionLevel.MissingFallback);
        rig.Warnings.Count.ShouldBe(1);
        rig.Warnings[0].ShouldContain(key);
        rig.Warnings[0].ShouldContain(why);
    }

    [Fact]
    public void Without_a_missing_model_in_any_theme_the_built_in_marker_stands_in()
    {
        var rig = new Rig(withMissingModel: false);

        var piece = rig.Equipment.Resolve("item.unknown", "humanoid")!;

        piece.Fallback.ShouldBeTrue();
        piece.Model.Level.ShouldBe(ResolutionLevel.BuiltIn);
        piece.Model.Path.ShouldBeNull();
    }

    [Fact]
    public void An_item_the_bundle_gives_no_worn_visual_draws_nothing_and_warns_once()
    {
        var rig = new Rig();

        rig.Equipment.Resolve("item.plain", "humanoid").ShouldBeNull();
        rig.Equipment.Resolve("item.plain", "humanoid").ShouldBeNull();

        rig.Warnings.Count.ShouldBe(1);
        rig.Warnings[0].ShouldContain("item.plain");
    }

    [Fact]
    public void A_missing_bone_falls_back_to_the_rigs_root_bone_and_warns_once_per_model_and_bone()
    {
        var rig = new Rig();
        var bones = new[] { "hips", "spine", "head", "arm_l", "hand_l", "arm_r", "hand_r" };
        int Find(string name) => Array.IndexOf(bones, name);
        var crest = rig.Equipment.Resolve("item.crest", "humanoid")!;

        AttachRules.BoneFor(AttachPoints.HandR, bones.Length, Find, "models/hero.glb", rig.Catalog).ShouldBe(6);
        AttachRules.BoneFor(crest.Bone, bones.Length, Find, "models/hero.glb", rig.Catalog).ShouldBe(AttachRules.DefaultBone);
        AttachRules.BoneFor(crest.Bone, bones.Length, Find, "models/hero.glb", rig.Catalog).ShouldBe(AttachRules.DefaultBone);
        AttachRules.BoneFor(null, bones.Length, Find, "models/hero.glb", rig.Catalog).ShouldBe(AttachRules.DefaultBone, "a fallback piece names no bone");
        rig.Warnings.Count.ShouldBe(1);
        rig.Warnings[0].ShouldContain("crown");

        AttachRules.BoneFor(AttachPoints.HandR, 0, Find, "models/statue.glb", rig.Catalog).ShouldBe(-1, "no skeleton: the model's root node");
        rig.Warnings.Count.ShouldBe(2);
    }

    // ---- re-dressing ----

    [Fact]
    public void It_re_dresses_only_when_the_look_counter_moves_and_only_the_changed_pieces()
    {
        var wardrobe = new Wardrobe();
        var added = new List<string>();
        var removed = new List<string>();

        wardrobe.Update(1, new[] { "item.blade", "item.vest" }, added, removed).ShouldBeTrue();
        added.ShouldBe(new[] { "item.blade", "item.vest" });
        removed.ShouldBeEmpty();

        wardrobe.Update(1, new[] { "item.staff" }, added, removed).ShouldBeFalse("the same counter: nothing to do, whatever the list says");
        wardrobe.Worn.ShouldBe(new[] { "item.blade", "item.vest" });

        wardrobe.Update(2, new[] { "item.crest", "item.blade", "item.vest" }, added, removed).ShouldBeTrue();
        added.ShouldBe(new[] { "item.crest" });
        removed.ShouldBeEmpty("the blade and the vest stay on");

        wardrobe.Update(3, new[] { "item.crest", "item.vest" }, added, removed).ShouldBeTrue();
        added.ShouldBeEmpty();
        removed.ShouldBe(new[] { "item.blade" });

        wardrobe.Update(4, Array.Empty<string>(), added, removed).ShouldBeTrue();
        removed.ShouldBe(new[] { "item.crest", "item.vest" });
        wardrobe.Worn.ShouldBeEmpty();
    }

    [Fact]
    public void The_same_item_worn_twice_is_two_pieces_and_a_pooled_visual_keeps_what_its_next_entity_also_wears()
    {
        var wardrobe = new Wardrobe();
        var added = new List<string>();
        var removed = new List<string>();
        wardrobe.Update(7, new[] { "item.ring", "item.ring" }, added, removed);
        added.Count.ShouldBe(2);

        wardrobe.Update(8, new[] { "item.ring" }, added, removed);
        removed.ShouldBe(new[] { "item.ring" });
        added.ShouldBeEmpty();

        wardrobe.Rebase(); // back to the pool; the next entity's counter may be any number, even the same
        wardrobe.Update(8, new[] { "item.ring", "item.vest" }, added, removed).ShouldBeTrue();
        added.ShouldBe(new[] { "item.vest" });
        removed.ShouldBeEmpty();
    }

    [Fact]
    public void A_two_handed_weapon_takes_off_the_one_handed_weapon_and_the_shield_in_one_change_and_picks_its_own_clips()
    {
        // ADR-002 row 170: the server takes the shield off in the same step, so the next look has the staff and no shield.
        var rig = new Rig();
        var wardrobe = new Wardrobe();
        var added = new List<string>();
        var removed = new List<string>();
        wardrobe.Update(1, new[] { "item.blade", "item.buckler", "item.vest" }, added, removed);

        wardrobe.Update(2, new[] { "item.staff", "item.vest" }, added, removed).ShouldBeTrue();

        added.ShouldBe(new[] { "item.staff" });
        removed.ShouldBe(new[] { "item.blade", "item.buckler" }, ignoreOrder: true);
        var pieces = wardrobe.Worn.Select(id => rig.Equipment.Resolve(id, "humanoid")!).ToList();
        pieces.Count(p => p.Bone == AttachPoints.HandL).ShouldBe(0, "the off hand is empty");
        pieces.Single(p => p.Bone == AttachPoints.HandR).VisualKey.ShouldBe("model.equip.staff");

        rig.Catalog.TryFind("anim.humanoid", out _, out var library).ShouldBeTrue();
        WeaponClips.NameFor(library.Clips, AnimationIds.Attack, WeaponTypes.Staff, _ => true).ShouldBe("AttackTwoHand");
        WeaponClips.NameFor(library.Clips, AnimationIds.Idle, WeaponTypes.Staff, _ => true).ShouldBe("IdleTwoHand");
    }

    // ---- clips ----

    [Theory]
    [InlineData("attack", "staff", "AttackTwoHand")] // the library has the weapon's own clip
    [InlineData("attack", "dagger", "Attack")] // it does not: the generic clip
    [InlineData("attack", null, "Attack")] // no weapon type
    [InlineData("walk", "staff", "Walk")] // only some ids have variants
    [InlineData("attack", "bow", "Attack")] // mapped, but the file lacks the clip
    [InlineData("die", "staff", null)] // nothing at all
    public void The_weapon_type_picks_its_clip_where_the_library_has_one_and_falls_back_to_the_generic_one(string id, string? weapon, string? expected)
    {
        var rig = new Rig();
        rig.Catalog.TryFind("anim.humanoid", out _, out var library).ShouldBeTrue();
        var inFile = new HashSet<string> { "Idle", "Walk", "Attack", "AttackTwoHand", "IdleTwoHand" };

        WeaponClips.NameFor(library.Clips, id, weapon, inFile.Contains).ShouldBe(expected);
    }

    // ---- sharing ----

    [Fact]
    public void Every_character_wearing_the_same_item_shares_one_resolution_and_one_model_path()
    {
        var rig = new Rig();

        var first = rig.Equipment.Resolve("item.blade", "humanoid");
        var second = rig.Equipment.Resolve("item.blade", "humanoid");
        var copy = rig.Equipment.Resolve("item.blade_copy", "humanoid"); // another item with the same visual key

        second.ShouldBeSameAs(first);
        copy.ShouldBeSameAs(first);
        rig.Equipment.Count.ShouldBe(1);
        rig.Equipment.Resolve("item.unknown", "humanoid")!.Model.ShouldBeSameAs(rig.Equipment.Resolve("item.gone", "humanoid")!.Model, "every fallback shares the missing model");
    }

    [Fact]
    public void Characters_with_the_same_body_and_pieces_in_any_order_share_one_merged_mesh_key()
    {
        var rig = new Rig();
        DressPart P(string item) => new(rig.Equipment.Resolve(item, "humanoid")!, PartRole.Gear);

        var a = WornSet.Key("themes/default/models/hero.glb", null, new[] { P("item.blade"), P("item.vest"), P("item.buckler") });
        var b = WornSet.Key("themes/default/models/hero.glb", null, new[] { P("item.buckler"), P("item.blade_copy"), P("item.vest") });

        b.ShouldBe(a, "the server's order and which item carries the visual do not matter");
        WornSet.Key("themes/default/models/other.glb", null, new[] { P("item.blade"), P("item.vest"), P("item.buckler") }).ShouldNotBe(a, "another body");
        WornSet.Key("themes/default/models/hero.glb", null, new[] { P("item.staff"), P("item.vest") }).ShouldNotBe(a, "other gear");
        WornSet.Key("themes/default/models/hero.glb", null, new[] { P("item.unknown") }).ShouldContain("!", Case.Sensitive, "a fallback piece is marked");
    }

    // ---- the real pack ----

    [Fact]
    public void Every_worn_visual_in_the_content_resolves_in_the_default_theme_to_an_attached_or_skinned_piece_on_the_humanoid_rig()
    {
        var themes = Path.Combine(TestGrids.RepoRoot(), "themes");
        ThemeManifest.TryParse("default", File.ReadAllText(Path.Combine(themes, "default", "asset-manifest.json")), out var manifest, out var error).ShouldBeTrue(error);
        var warnings = new List<string>();
        var catalog = new ThemeCatalog(new[] { manifest! }, path => File.Exists(Path.Combine(TestGrids.RepoRoot(), path)), warnings.Add);
        DisplayBundle.TryRead(ClientBundleExporter.Export(TestGrids.SampleContent).FileBytes, out var bundle, out _).ShouldBe(BundleStatus.Ok);
        var equipment = new EquipmentCatalog(catalog, id => bundle!.Item(id)?.Visual);

        using var document = JsonDocument.Parse(ClientBundleExporter.Export(TestGrids.SampleContent).FileBytes);
        var worn = document.RootElement.GetProperty("items").EnumerateArray().Where(i => i.TryGetProperty("visual", out _)).Select(i => i.GetProperty("id").GetString()!).ToList();
        worn.Count.ShouldBeGreaterThanOrEqualTo(6);
        var modes = new HashSet<WornMode>();
        foreach (var item in worn)
        {
            var piece = equipment.Resolve(item, "humanoid")!;
            piece.Fallback.ShouldBeFalse(item);
            piece.Model.Level.ShouldBe(ResolutionLevel.Exact, item);
            if (piece.Mode == WornMode.Attached) AttachPoints.All.Contains(piece.Bone!).ShouldBeTrue(item);
            modes.Add(piece.Mode);
        }
        modes.Count.ShouldBe(2, "the placeholders show both kinds");
        warnings.ShouldBeEmpty();
    }
}
