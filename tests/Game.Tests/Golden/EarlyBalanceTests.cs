using Game.Content.Loading;
using Game.Content.Simulation;

namespace Game.Tests.Golden;

/// <summary>
/// T-309: the first fights of a new character are winnable without a guide, and danger stays real. Each test pins one target from
/// tasks/prompts/T-309.md with the real /content: the expected-value model (<see cref="Duel"/>) and a seeded simulation whose every swing
/// goes through the real CombatResolver. Characters are Wanderers without a weapon (the server gives none until T-401) and spend their
/// points by a simple rule: everything into str, or a str/vit split. Monster skills (the wasp's venom sting) are not modelled.
/// </summary>
public class EarlyBalanceTests
{
    private const string Wanderer = "job.wanderer";
    private const int Fights = 1000;
    private const ulong Seed = 1;

    private static readonly Lazy<BalanceSimulator> Content = new(() =>
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "Game.sln"))) dir = dir.Parent;
        if (dir == null) throw new InvalidOperationException("Game.sln not found above " + AppContext.BaseDirectory);
        var result = ContentLoader.Load(Path.Combine(dir.FullName, "content"));
        result.Errors.ShouldBeEmpty(string.Join("\n", result.Errors));
        return new BalanceSimulator(result.Database!);
    });

    private static DuelResult Fight(int level, IReadOnlyDictionary<string, int> build, string monsterId)
    {
        var sim = Content.Value;
        var player = sim.Character(Wanderer, level, build, unarmed: true);
        return Duel.Run(sim, player, sim.Db.Monsters.Get(monsterId), Fights, Seed);
    }

    private static Dictionary<string, int> Build(params (string Stat, int Value)[] stats) => stats.ToDictionary(s => s.Stat, s => s.Value);

    /// <summary>
    /// All stats 1, 10 points unspent: atk 1 → 1 damage per hit, so the slime has 9 HP (9 hits at 80% → 11.25 swings, under the target of 12)
    /// and hits for 3 (≈ 3 × 0.81 per swing leaves 42.7% of the 48 HP, over the target of 30%).
    /// </summary>
    [Fact]
    public void New_character_with_no_points_spent_beats_a_gel_slime()
    {
        var r = Fight(1, Build(), "monster.gel_slime");

        r.PlayerHp.ShouldBe(48);
        r.MonsterHp.ShouldBe(9);
        r.ExpectedSwings.ShouldBeLessThanOrEqualTo(12);
        r.ExpectedSwings.ShouldBe(11.25, 1e-6);
        r.ExpectedHpLeftFraction.ShouldBeGreaterThanOrEqualTo(0.30);
        r.ExpectedHpLeftFraction.ShouldBe(0.427251, 1e-4);
    }

    /// <summary>The same fight through the real CombatResolver, 1,000 fights with seed 1: the player all but always wins (the slime can only win by a very long streak of misses).</summary>
    [Fact]
    public void Seeded_simulation_of_the_first_fight_agrees_with_the_model()
    {
        var r = Fight(1, Build(), "monster.gel_slime");

        r.Fights.ShouldBe(1000);
        r.WinRate.ShouldBeGreaterThanOrEqualTo(0.99);
        r.WinRate.ShouldBe(0.998, 1e-9);
        r.MeanSwingsOnWin.ShouldBeLessThanOrEqualTo(12);
        r.MeanSwingsOnWin.ShouldBe(11.2886, 1e-4);
        r.MeanHpLeftFractionOnWin.ShouldBeGreaterThanOrEqualTo(0.30);
        r.MeanHpLeftFractionOnWin.ShouldBe(0.425539, 1e-4);
    }

    /// <summary>Points spent (str 6 = 5 raises of 2 points, or str 4 + vit 3): 2 or 3.75 swings, over 87% HP left. Targets: ≤ 5 swings, ≥ 60%.</summary>
    [Theory]
    [InlineData(6, 1, 2.5, 0.872722)]   // all str: 6 damage per hit, 9 HP → 2 hits
    [InlineData(4, 3, 3.75, 0.875092)]  // str 4 / vit 3: 4 damage per hit, 3 hits, a little more HP
    public void New_character_with_points_spent_beats_a_gel_slime_quickly(int str, int vit, double swings, double hpLeft)
    {
        var r = Fight(1, Build(("str", str), ("vit", vit)), "monster.gel_slime");

        r.ExpectedSwings.ShouldBeLessThanOrEqualTo(5);
        r.ExpectedSwings.ShouldBe(swings, 1e-6);
        r.ExpectedHpLeftFraction.ShouldBeGreaterThanOrEqualTo(0.60);
        r.ExpectedHpLeftFraction.ShouldBe(hpLeft, 1e-4);
        r.WinRate.ShouldBe(1.0, 1e-9);
    }

    /// <summary>Level 3 has 16 points. Mossling (32 HP, atk 7): all str (9) leaves 52% HP, the str 6 / vit 4 split 30%. Target: ≥ 20%.</summary>
    [Theory]
    [InlineData(9, 1, 0.519629)]
    [InlineData(6, 4, 0.300585)]
    public void Level_3_character_beats_a_mossling(int str, int vit, double hpLeft)
    {
        var r = Fight(3, Build(("str", str), ("vit", vit)), "monster.mossling");

        r.ExpectedHpLeftFraction.ShouldBeGreaterThanOrEqualTo(0.20);
        r.ExpectedHpLeftFraction.ShouldBe(hpLeft, 1e-4);
        r.WinRate.ShouldBeGreaterThanOrEqualTo(0.90);
    }

    /// <summary>
    /// Level 5 (22 points, str 11) and level 6 (25 points, str 12) against the Sting Wasp (66 HP, atk 10, dodgy, fast): the seeded win rate is
    /// 42.8% and 67.6%, inside the 40–70% band. The wasp is the toughest monster a level 5–6 unarmed character is meant to face; the
    /// Rock Beetle (level 7) and Thornback Boar (level 10) are for later levels and weapons (T-401).
    /// </summary>
    [Theory]
    // T-501: the duel now models the wasp's Venom Sting and its poison (ADR-002 row 118); before it, 0.428 and 0.676.
    [InlineData(5, 11, 0.433)]
    [InlineData(6, 12, 0.643)]
    public void Level_5_and_6_characters_have_an_even_fight_with_a_sting_wasp(int level, int str, double winRate)
    {
        var r = Fight(level, Build(("str", str)), "monster.sting_wasp");

        r.WinRate.ShouldBeInRange(0.40, 0.70);
        r.WinRate.ShouldBe(winRate, 1e-9);
    }

    /// <summary>The unarmed duel is deterministic: the same seed gives the same result twice.</summary>
    [Fact]
    public void Duel_is_deterministic()
    {
        var a = Fight(5, Build(("str", 11)), "monster.sting_wasp");
        var b = Fight(5, Build(("str", 11)), "monster.sting_wasp");
        b.ShouldBe(a);
    }
}
