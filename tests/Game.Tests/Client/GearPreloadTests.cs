using Game.Client.Core.Theme;

namespace Game.Tests.Client;

/// <summary>Which worn-visual models load ahead of need (T-405, ADR-002 row 182): worn first, then the bag, each path once, bounded, and the same paths worn pieces use.</summary>
public class GearPreloadTests
{
    private const string Manifest = """
        {
          "model.equip.blade": { "file": "models/blade.glb", "attach": "hand_r" },
          "model.equip.buckler": { "file": "models/buckler.glb", "attach": "hand_l" },
          "model.equip.staff": { "file": "models/staff.glb", "attach": "hand_r" },
          "model.equip.vest": { "file": "models/vest.glb", "skinned": "humanoid" },
          "model.equip.odd": { "file": "models/odd.glb" },
          "model.equip.both": { "file": "models/both.glb", "attach": "hand_r", "skinned": "humanoid" },
          "model.missing": { "file": "models/missing.glb" }
        }
        """;

    private static readonly Dictionary<string, string> Visuals = new(StringComparer.Ordinal)
    {
        ["item.blade"] = "model.equip.blade", ["item.blade_copy"] = "model.equip.blade", ["item.buckler"] = "model.equip.buckler",
        ["item.staff"] = "model.equip.staff", ["item.vest"] = "model.equip.vest", ["item.odd"] = "model.equip.odd", ["item.both"] = "model.equip.both",
        ["item.unknown"] = "model.equip.nowhere",
    };

    private static (GearPreload Preload, EquipmentCatalog Equipment, List<string> Warnings) Make(int limit)
    {
        ThemeManifest.TryParse("default", Manifest, out var manifest, out var error).ShouldBeTrue(error);
        var files = new HashSet<string>(manifest!.Keys.Select(k => ThemeCatalog.PathOf("default", manifest.TryGet(k, out var a) ? a.File! : "")), StringComparer.Ordinal);
        var warnings = new List<string>();
        var catalog = new ThemeCatalog(new[] { manifest }, files.Contains, warnings.Add);
        Func<string, string?> visual = id => Visuals.TryGetValue(id, out var key) ? key : null;
        return (new GearPreload(catalog, visual, limit), new EquipmentCatalog(catalog, visual), warnings);
    }

    [Fact]
    public void Worn_items_come_before_the_bag_and_each_path_appears_once()
    {
        var (preload, _, _) = Make(16);
        var paths = preload.Select(new[] { "item.staff", "item.blade" }, new[] { "item.buckler", "item.blade_copy", "item.vest" });
        paths.ShouldBe(new[]
        {
            "themes/default/models/staff.glb", "themes/default/models/blade.glb", "themes/default/models/buckler.glb", "themes/default/models/vest.glb",
        });
    }

    [Fact]
    public void Items_without_a_visual_a_resolvable_key_or_a_valid_entry_are_left_out()
    {
        var (preload, _, warnings) = Make(16);
        preload.Select(new[] { "item.potion", "item.unknown", "item.odd", "item.both" }, Array.Empty<string>()).ShouldBeEmpty();
        warnings.ShouldBeEmpty("preloading is quiet; the worn piece warns when it is really needed");
    }

    [Fact]
    public void The_limit_bounds_the_list_and_keeps_the_worn_ones()
    {
        var (preload, _, _) = Make(2);
        preload.Select(new[] { "item.staff" }, new[] { "item.blade", "item.buckler", "item.vest" })
            .ShouldBe(new[] { "themes/default/models/staff.glb", "themes/default/models/blade.glb" });
        Make(0).Preload.Select(new[] { "item.staff", "item.blade" }, Array.Empty<string>()).Count.ShouldBe(1, "a limit below 1 still names one path; the client switches the feature off with 0 instead");
    }

    [Fact]
    public void The_paths_are_the_ones_worn_pieces_load_so_the_cache_is_shared()
    {
        var (preload, equipment, _) = Make(16);
        var paths = preload.Select(new[] { "item.blade", "item.vest" }, new[] { "item.buckler" });
        var used = new[] { equipment.Resolve("item.blade", "humanoid")!, equipment.Resolve("item.vest", "humanoid")!, equipment.Resolve("item.buckler", "humanoid")! }
            .Select(p => p.Model.Path!).ToArray();
        paths.ShouldBe(used);
    }
}
