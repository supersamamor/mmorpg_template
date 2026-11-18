using Game.Shared.Vocabulary;
using Game.Tests.World;
using Game.ThemeGen;

namespace Game.Tests.Client;

/// <summary>Placeholder weapons look like their weapon type (T-405, ADR-002 row 181).</summary>
public class WeaponShapeTests
{
    [Fact]
    public void The_types_come_from_the_items_content_not_from_the_model_names()
    {
        var types = WeaponShapes.TypesByVisualKey(Path.Combine(TestGrids.RepoRoot(), "content"));
        types["model.equip.bone_dirk"].ShouldBe(WeaponTypes.Dagger);
        types["model.equip.iron_blade"].ShouldBe(WeaponTypes.Sword1h);
        types["model.equip.oak_staff"].ShouldBe(WeaponTypes.Staff);
        types.ContainsKey("model.equip.leather_cap").ShouldBeFalse("headgear has no weapon type");
    }

    [Fact]
    public void A_dagger_is_a_shorter_blade_than_a_sword_and_a_sword_than_a_two_hander()
    {
        var dagger = WeaponShapes.Size(WeaponTypes.Dagger).Z;
        var sword = WeaponShapes.Size(WeaponTypes.Sword1h).Z;
        var greatsword = WeaponShapes.Size(WeaponTypes.Sword2h).Z;
        dagger.ShouldBeLessThan(sword);
        sword.ShouldBeLessThan(greatsword);
        dagger.ShouldBeLessThan(0.5f);
    }

    [Fact]
    public void A_staff_stands_up_while_blades_point_forward()
    {
        var staff = WeaponShapes.Size(WeaponTypes.Staff);
        staff.Y.ShouldBeGreaterThan(staff.Z * 4);
        var dagger = WeaponShapes.Size(WeaponTypes.Dagger);
        dagger.Z.ShouldBeGreaterThan(dagger.Y);
    }

    [Fact]
    public void Every_weapon_type_has_a_shape_of_its_own_except_unarmed_which_takes_the_default()
    {
        var sizes = new Dictionary<string, System.Numerics.Vector3>();
        foreach (var type in WeaponTypes.All.Where(t => t != WeaponTypes.Unarmed))
        {
            WeaponShapes.ShapeOf(type).ShouldBe(type);
            sizes[type] = WeaponShapes.Size(type);
        }
        sizes.Values.Distinct().Count().ShouldBe(sizes.Count, "no two weapon types share a size");
        WeaponShapes.ShapeOf(null).ShouldBe(WeaponShapes.ShapeOf(WeaponTypes.Unarmed));
    }
}
