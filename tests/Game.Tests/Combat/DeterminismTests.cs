using System.Text.Json;
using Game.Shared.Combat;
using Game.Shared.Random;
using static Game.Tests.Combat.CombatTestContent;

namespace Game.Tests.Combat;

/// <summary>Seeded RNG pins and same-seed → byte-identical combat results.</summary>
public class DeterminismTests
{
    [Fact]
    public void Pcg32_matches_the_reference_demo_output()
    {
        // pcg32-demo from the reference C implementation: pcg32_srandom_r(&rng, 42u, 54u), first six pcg32_random_r.
        var rng = new Pcg32Random(42, 54);
        var expected = new uint[] { 0xa15c02b7, 0x7b47f409, 0xba1d3330, 0x83d2f293, 0xbfa4784b, 0xcbed606e };
        expected.Select(_ => rng.NextUInt()).ToArray().ShouldBe(expected);
    }

    [Fact]
    public void Pcg32_default_stream_first_outputs_are_pinned()
    {
        // Pins the single-argument constructor (default stream) and the double / bounded paths, so a change to
        // either is caught here rather than as drifting combat results.
        var rng = new Pcg32Random(12345);
        var uints = Enumerable.Range(0, 4).Select(_ => rng.NextUInt()).ToArray();
        var doubles = Enumerable.Range(0, 3).Select(_ => rng.NextDouble()).ToArray();
        var ints = Enumerable.Range(0, 6).Select(_ => rng.Next(100)).ToArray();

        var again = new Pcg32Random(12345);
        uints.ShouldBe(Enumerable.Range(0, 4).Select(_ => again.NextUInt()).ToArray());
        doubles.ShouldBe(Enumerable.Range(0, 3).Select(_ => again.NextDouble()).ToArray());
        ints.ShouldBe(Enumerable.Range(0, 6).Select(_ => again.Next(100)).ToArray());

        uints.ShouldBe(PinnedUInts);
        doubles.ShouldBe(PinnedDoubles);
        ints.ShouldBe(PinnedInts);
    }

    private static readonly uint[] PinnedUInts = { 0x2747ce80u, 0xf4ca7b11u, 0x56bfd1beu, 0xf04efa42u };
    private static readonly double[] PinnedDoubles = { 0.2622751489433477, 0.08061385239875651, 0.2910208421003796 };
    private static readonly int[] PinnedInts = { 37, 79, 34, 8, 38, 44 };

    [Fact]
    public void Next_double_is_in_unit_interval_and_next_is_in_range()
    {
        var rng = new Pcg32Random(7);
        for (var i = 0; i < 10_000; i++)
        {
            var d = rng.NextDouble();
            (d >= 0 && d < 1).ShouldBeTrue();
            var n = rng.Next(3);
            (n >= 0 && n < 3).ShouldBeTrue();
        }
        Should.Throw<ArgumentOutOfRangeException>(() => rng.Next(0));
    }

    [Fact]
    public void Different_seeds_give_different_sequences()
    {
        var a = new Pcg32Random(1);
        var b = new Pcg32Random(2);
        Enumerable.Range(0, 8).Select(_ => a.NextUInt()).ShouldNotBe(Enumerable.Range(0, 8).Select(_ => b.NextUInt()));
    }

    [Fact]
    public void Same_seed_produces_byte_identical_result_lists()
    {
        var first = Run(seed: 20261101);
        var second = Run(seed: 20261101);
        second.ShouldBe(first);

        Run(seed: 1).ShouldNotBe(first); // the rolls really come from the seed
    }

    private static byte[] Run(ulong seed)
    {
        var resolver = new CombatResolver(Rules(critMultiplier: "1.5"), new Pcg32Random(seed));
        var attacks = new[]
        {
            Attack(Warden(3), "item.iron_blade", 3, "monster.gel_slime"),
            Attack(Warden(0), "item.iron_blade", 0, "monster.thornback_boar"),
            Attack(Character("job.novice", 1, 1, 1, 1, 1, 1, 1, 1), null, 0, "monster.sting_wasp"),
        };
        attacks[1].Effect = DamageEffectOf("skill.shockwave");
        attacks[1].SkillLevel = 4;

        var results = new List<object>();
        for (var i = 0; i < 300; i++)
        {
            var r = resolver.Resolve(attacks[i % attacks.Length]);
            results.Add(new { r.Hit, r.Crit, r.Element, r.Multiplier, r.Damage });
        }
        return JsonSerializer.SerializeToUtf8Bytes(results);
    }
}
