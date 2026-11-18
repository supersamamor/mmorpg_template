using Game.Content.Loading;
using Game.Content.Simulation;

namespace Game.Tests.Golden;

/// <summary>
/// T-309, re-pinned by T-404: the first fights of a new character are winnable without a guide, and danger stays real. Each test pins one target from
/// tasks/prompts/T-309.md with the real /content: the expected-value model (<see cref="Duel"/>) and a seeded simulation whose every swing goes through
/// the real CombatResolver. Characters are Wanderers wearing the Bone Dirk (<c>new_character.start_items</c> since T-401; T-309 itself measured them unarmed)
/// and spend their points by a simple rule: everything into str, or a str/vit split. The wasp's Venom Sting is modelled (T-501).
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
        var player = sim.Character(Wanderer, level, build);
        player.Weapon!.Id.ShouldBe("item.bone_dirk");   // what a new character wears
        return Duel.Run(sim, player, sim.Db.Monsters.Get(monsterId), Fights, Seed);
    }

    private static Dictionary<string, int> Build(params (string Stat, int Value)[] stats) => stats.ToDictionary(s => s.Stat, s => s.Value);

    /// <summary>
    /// All stats 1, 10 points unspent. The Bone Dirk (weapon atk 12, +5% aspd) makes the 9 HP slime a one-hit kill (13 damage a landed hit):
    /// 1 / 0.8 = 1.25 swings (target ≤ 12) and 93.9% HP left (target ≥ 30%). Before the dirk: 11.25 swings, 42.7%.
    /// </summary>
    [Fact]
    public void New_character_with_no_points_spent_beats_a_gel_slime()
    {
        var r = Fight(1, Build(), "monster.gel_slime");

        r.PlayerHp.ShouldBe(48);
        r.MonsterHp.ShouldBe(9);
        r.ExpectedSwings.ShouldBeLessThanOrEqualTo(12);
        r.ExpectedSwings.ShouldBe(1.25, 1e-6);
        r.ExpectedHpLeftFraction.ShouldBeGreaterThanOrEqualTo(0.30);
        r.ExpectedHpLeftFraction.ShouldBe(0.939392, 1e-4);
    }

    /// <summary>The same fight through the real CombatResolver, 1,000 fights with seed 1: the player always wins.</summary>
    [Fact]
    public void Seeded_simulation_of_the_first_fight_agrees_with_the_model()
    {
        var r = Fight(1, Build(), "monster.gel_slime");

        r.Fights.ShouldBe(1000);
        r.WinRate.ShouldBe(1.0, 1e-9);
        r.MeanSwingsOnWin.ShouldBeLessThanOrEqualTo(12);
        r.MeanSwingsOnWin.ShouldBe(1.239, 1e-4);
        r.MeanHpLeftFractionOnWin.ShouldBeGreaterThanOrEqualTo(0.30);
        r.MeanHpLeftFractionOnWin.ShouldBe(0.987437, 1e-4);
    }

    /// <summary>Points spent (str 6, or str 4 + vit 3): the dirk already one-shots the slime, so the points change only the HP (48 or 49). Targets: ≤ 5 swings, ≥ 60% HP.</summary>
    [Theory]
    [InlineData(6, 1, 1.25, 0.939392)]
    [InlineData(4, 3, 1.25, 0.960347)]
    public void New_character_with_points_spent_beats_a_gel_slime_quickly(int str, int vit, double swings, double hpLeft)
    {
        var r = Fight(1, Build(("str", str), ("vit", vit)), "monster.gel_slime");

        r.ExpectedSwings.ShouldBeLessThanOrEqualTo(5);
        r.ExpectedSwings.ShouldBe(swings, 1e-6);
        r.ExpectedHpLeftFraction.ShouldBeGreaterThanOrEqualTo(0.60);
        r.ExpectedHpLeftFraction.ShouldBe(hpLeft, 1e-4);
        r.WinRate.ShouldBe(1.0, 1e-9);
    }

    /// <summary>Level 3 has 16 points. Mossling (32 HP, atk 7): all str (9) leaves 77% HP, the str 6 / vit 4 split 81%. Target: ≥ 20%.</summary>
    [Theory]
    [InlineData(9, 1, 0.771252)]
    [InlineData(6, 4, 0.809683)]
    public void Level_3_character_beats_a_mossling(int str, int vit, double hpLeft)
    {
        var r = Fight(3, Build(("str", str), ("vit", vit)), "monster.mossling");

        r.ExpectedHpLeftFraction.ShouldBeGreaterThanOrEqualTo(0.20);
        r.ExpectedHpLeftFraction.ShouldBe(hpLeft, 1e-4);
        r.WinRate.ShouldBeGreaterThanOrEqualTo(0.90);
    }

    /// <summary>
    /// Level 5 (str 11) and level 6 (str 12) with the Bone Dirk against the Sting Wasp (T-404: 90 HP, atk 13; was 66 HP, atk 10, which the dirk beat 98% of
    /// the time): the seeded win rate is 46.4% and 67.9%, inside the 40–70% band. The wasp is still the toughest monster a level 5–6 character meets.
    /// </summary>
    [Theory]
    [InlineData(5, 11, 0.464)]
    [InlineData(6, 12, 0.679)]
    public void Level_5_and_6_characters_have_an_even_fight_with_a_sting_wasp(int level, int str, double winRate)
    {
        var r = Fight(level, Build(("str", str)), "monster.sting_wasp");

        r.WinRate.ShouldBeInRange(0.40, 0.70);
        r.WinRate.ShouldBe(winRate, 1e-9);
    }

    /// <summary>The duel is deterministic: the same seed gives the same result twice.</summary>
    [Fact]
    public void Duel_is_deterministic()
    {
        var a = Fight(5, Build(("str", 11)), "monster.sting_wasp");
        var b = Fight(5, Build(("str", 11)), "monster.sting_wasp");
        b.ShouldBe(a);
    }
}
