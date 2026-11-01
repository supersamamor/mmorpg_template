using Game.Content.Loading;
using Game.Content.Registry;
using Game.Shared.Definitions;
using Game.Shared.Formulas;
using Game.Shared.Stats;
using Game.Shared.Vocabulary;

namespace Game.Tests.StatPipeline; // not Game.Tests.Stats: that would shadow Vocabulary.Stats in sibling test namespaces

/// <summary>
/// Golden values: three sample characters built from the real /content (jobs, items, statuses, formulas.yaml),
/// with every final stat calculated by hand in the comments. If formulas.yaml or the sample items change,
/// these numbers are expected to change: redo the arithmetic, don't just paste the new output.
/// </summary>
/// <remarks>
/// Pipeline per stat: <c>(Base + ΣFlat) × (100 + ΣPercent) / 100</c>, override wins; primaries floored; derived
/// Base = its formula on final primaries + levels; derived results are not rounded (see <see cref="StatSheet"/>).
/// </remarks>
public class GoldenCharacterTests
{
    private const double Tolerance = 1e-9;

    private static readonly Lazy<ContentDatabase> Db = new(() =>
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "Game.sln"))) dir = dir.Parent;
        if (dir == null) throw new InvalidOperationException("Game.sln not found above " + AppContext.BaseDirectory);
        var result = ContentLoader.Load(Path.Combine(dir.FullName, "content"));
        result.Errors.ShouldBeEmpty(string.Join("\n", result.Errors));
        return result.Database!;
    });

    private static readonly Lazy<DerivedStatFormulas> Formulas = new(() => DerivedStatFormulas.Compile(Db.Value.Formulas));

    private static StatSheet NewCharacter(string jobId, int baseLevel, int jobLevel,
        int str, int agi, int vit, int intel, int dex, int luk)
    {
        var sheet = new StatSheet(Formulas.Value);
        sheet.Base.BaseLevel = baseLevel;
        sheet.Base.JobLevel = jobLevel;
        sheet.Base[PrimaryStats.Str] = str;
        sheet.Base[PrimaryStats.Agi] = agi;
        sheet.Base[PrimaryStats.Vit] = vit;
        sheet.Base[PrimaryStats.Int] = intel;
        sheet.Base[PrimaryStats.Dex] = dex;
        sheet.Base[PrimaryStats.Luk] = luk;

        var context = new FormulaContext();
        context[FormulaVariables.BaseLevel] = baseLevel;
        context[FormulaVariables.JobLevel] = jobLevel;
        Add(sheet, "job:" + jobId, Db.Value.Jobs.Get(jobId).StatBonuses, context);
        return sheet;
    }

    private static void Equip(StatSheet sheet, string slot, string itemId, int refine = 0)
    {
        var context = new FormulaContext();
        context[FormulaVariables.Refine] = refine;
        Add(sheet, "equip:" + slot, Db.Value.Items.Get(itemId).Equip!.Modifiers, context);
    }

    private static void Card(StatSheet sheet, string slot, string cardId) =>
        Add(sheet, "card:" + slot + ":0", Db.Value.Items.Get(cardId).Card!.Modifiers, FormulaContext.None);

    private static void Status(StatSheet sheet, string statusId)
    {
        var modifiers = Db.Value.Statuses.Get(statusId).OnApply.OfType<ModifyStatEffect>().Select(e => e.Modifier).ToList();
        Add(sheet, "status:" + statusId, modifiers, FormulaContext.None);
    }

    private static void Add(StatSheet sheet, string source, IReadOnlyList<ModifierDef> defs, IFormulaContext context)
    {
        foreach (var def in defs) sheet.Modifiers.Add(source, CompiledModifier.Compile(def), context);
    }

    private static void ShouldHave(StatSheet sheet, string stat, double expected) =>
        sheet.Get(stat).ShouldBe(expected, Tolerance, stat);

    /// <summary>
    /// Wanderer (job.novice, no stat bonuses), base 3 / job 4. STR 6, AGI 4, VIT 5, INT 2, DEX 5, LUK 3.
    /// Equipment: item.bone_dirk (aspd +5%), item.leather_cap (def +1, mdef +1).
    /// </summary>
    [Fact]
    public void Wanderer_level_3_with_dirk_and_cap()
    {
        var c = NewCharacter("job.novice", 3, 4, str: 6, agi: 4, vit: 5, intel: 2, dex: 5, luk: 3);
        Equip(c, "weapon", "item.bone_dirk");
        Equip(c, "head_top", "item.leather_cap");

        // Primaries: no modifiers, unchanged.
        ShouldHave(c, "str", 6);
        ShouldHave(c, "agi", 4);
        ShouldHave(c, "vit", 5);
        ShouldHave(c, "int", 2);
        ShouldHave(c, "dex", 5);
        ShouldHave(c, "luk", 3);

        ShouldHave(c, "max_hp", 67);        // floor((40 + 3×8) × (100 + 5) / 100) = floor(64 × (100 + 5) / 100) = floor(67.2) = 67
        ShouldHave(c, "max_sp", 19);        // floor((10 + 3×3) × (100 + 2) / 100) = floor(19 × (100 + 2) / 100) = floor(19.38) = 19
        ShouldHave(c, "atk", 7);            // 6 + floor(5/5)=1 + floor(3/5)=0 + floor(3/4)=0 = 7
        ShouldHave(c, "matk", 3);           // floor(2×1.5)=3 + floor(3/4)=0 = 3
        ShouldHave(c, "def", 3);            // floor(5/2)=2 + floor(3/5)=0 = 2; + cap 1 = 3
        ShouldHave(c, "mdef", 2);           // floor(2/3)=0 + floor(5/5)=1 = 1; + cap 1 = 2
        ShouldHave(c, "hit", 18);           // 3 + 5 + 10
        ShouldHave(c, "flee", 17);          // 3 + 4 + 10
        ShouldHave(c, "crit", 1.9);         // 1 + 3×0.3
        ShouldHave(c, "aspd", 1.1025);      // min(3, 1 + 4×0.01 + 5×0.002) = 1.05; × 105/100 (dirk) = 1.1025
        ShouldHave(c, "move_speed", 3.54);  // min(6, 3.5 + 4×0.01)
        ShouldHave(c, "cast_time_mod", 1 - 5.0 / 150); // clamp(1 − 5/150, 0.2, 1) = 0.96667
        ShouldHave(c, "hp_regen", 0.51);    // 0.2 + 5×0.05 + 3×0.02 = 0.2 + 0.25 + 0.06
        ShouldHave(c, "sp_regen", 0.19);    // 0.1 + 2×0.03 + 3×0.01 = 0.1 + 0.06 + 0.03
        ShouldHave(c, "weight_limit", 2210); // 2000 + 6×30 + 3×10
    }

    /// <summary>
    /// Warden (job.warden), base 20 / job 12. STR 18, AGI 8, VIT 14, INT 1, DEX 10, LUK 4.
    /// Job bonuses at job 12: max_hp +12×0.5 = +6%, vit +floor(12/10) = +1.
    /// Equipment: item.iron_blade +3 (str +1), item.padded_vest +2 (def +4+2×2 = +8) with item.gelcap_card (max_hp +8%),
    /// item.wooden_buckler +0 (def +2+0, flee −3).
    /// </summary>
    [Fact]
    public void Warden_level_20_full_kit()
    {
        var c = NewCharacter("job.warden", 20, 12, str: 18, agi: 8, vit: 14, intel: 1, dex: 10, luk: 4);
        Equip(c, "weapon", "item.iron_blade", refine: 3);
        Equip(c, "body", "item.padded_vest", refine: 2);
        Card(c, "body", "item.gelcap_card");
        Equip(c, "shield", "item.wooden_buckler", refine: 0);

        ShouldHave(c, "str", 19);           // 18 + 1 (blade)
        ShouldHave(c, "agi", 8);
        ShouldHave(c, "vit", 15);           // 14 + 1 (job)
        ShouldHave(c, "int", 1);
        ShouldHave(c, "dex", 10);
        ShouldHave(c, "luk", 4);

        // Formula: floor((40 + 20×8) × (100 + 15) / 100) = floor(200 × 115 / 100) = floor(23000 / 100) = 230.
        // Integer products stay exact in doubles; the old `(1 + vit × 0.01)` form gave 229 here (1.15 ≈ 1.1499999…).
        // Then percent modifiers: 230 × (100 + 6 job + 8 card) / 100 = 230 × 1.14 = 262.2.
        ShouldHave(c, "max_hp", 262.2);
        c.GetInt("max_hp").ShouldBe(262);
        ShouldHave(c, "max_sp", 70);        // floor((10 + 20×3) × (100 + 1) / 100) = floor(70 × (100 + 1) / 100) = floor(70.7) = 70
        ShouldHave(c, "atk", 26);           // 19 + floor(10/5)=2 + floor(4/5)=0 + floor(20/4)=5 = 26
        ShouldHave(c, "matk", 6);           // floor(1×1.5)=1 + floor(20/4)=5 = 6
        ShouldHave(c, "def", 21);           // floor(15/2)=7 + floor(20/5)=4 = 11; + vest 8 + buckler 2 = 21
        ShouldHave(c, "mdef", 3);           // floor(1/3)=0 + floor(15/5)=3 = 3
        ShouldHave(c, "hit", 40);           // 20 + 10 + 10
        ShouldHave(c, "flee", 35);          // 20 + 8 + 10 = 38; − 3 buckler = 35
        ShouldHave(c, "crit", 2.2);         // 1 + 4×0.3
        ShouldHave(c, "aspd", 1.1);         // min(3, 1 + 8×0.01 + 10×0.002) = 1 + 0.08 + 0.02
        ShouldHave(c, "move_speed", 3.58);  // min(6, 3.5 + 8×0.01)
        ShouldHave(c, "cast_time_mod", 1 - 10.0 / 150); // 0.93333
        ShouldHave(c, "hp_regen", 1.35);    // 0.2 + 15×0.05 + 20×0.02 = 0.2 + 0.75 + 0.4
        ShouldHave(c, "sp_regen", 0.33);    // 0.1 + 1×0.03 + 20×0.01 = 0.1 + 0.03 + 0.2
        ShouldHave(c, "weight_limit", 2770); // 2000 + 19×30 + 20×10 = 2000 + 570 + 200

        // Unequipping the body armour (vest + its card) restores the formula-only values.
        c.Modifiers.RemoveBySource("equip:body");
        c.Modifiers.RemoveBySource("card:body:0");
        ShouldHave(c, "def", 13);           // 11 + buckler 2
        ShouldHave(c, "max_hp", 243.8);     // 230 × 106/100
    }

    /// <summary>
    /// Spellbinder (job.spellbinder), base 15 / job 20. STR 2, AGI 6, VIT 6, INT 20, DEX 12, LUK 3.
    /// Job bonuses at job 20: max_sp +20×0.5 = +10%, int +floor(20/10) = +2.
    /// Equipment: item.oak_staff +4 (matk +10+4×3 = +22, int +2). Statuses: status.swift (move_speed +1, aspd +15%),
    /// status.iron_skin (def +30%, move_speed −10%).
    /// </summary>
    [Fact]
    public void Spellbinder_level_15_buffed()
    {
        var c = NewCharacter("job.spellbinder", 15, 20, str: 2, agi: 6, vit: 6, intel: 20, dex: 12, luk: 3);
        Equip(c, "weapon", "item.oak_staff", refine: 4);
        Status(c, "status.swift");
        Status(c, "status.iron_skin");

        ShouldHave(c, "str", 2);
        ShouldHave(c, "agi", 6);
        ShouldHave(c, "vit", 6);
        ShouldHave(c, "int", 24);           // 20 + 2 (job) + 2 (staff)
        ShouldHave(c, "dex", 12);
        ShouldHave(c, "luk", 3);

        ShouldHave(c, "max_hp", 169);       // floor((40 + 15×8) × (100 + 6) / 100) = floor(160 × (100 + 6) / 100) = floor(169.6) = 169
        ShouldHave(c, "max_sp", 74.8);      // floor((10 + 15×3) × (100 + 24) / 100) = floor(55 × (100 + 24) / 100) = floor(68.2) = 68; × 110/100 = 74.8
        ShouldHave(c, "atk", 7);            // 2 + floor(12/5)=2 + floor(3/5)=0 + floor(15/4)=3 = 7
        ShouldHave(c, "matk", 61);          // floor(24×1.5)=36 + floor(15/4)=3 = 39; + staff 22 = 61
        ShouldHave(c, "def", 7.8);          // floor(6/2)=3 + floor(15/5)=3 = 6; × 130/100 = 7.8
        ShouldHave(c, "mdef", 9);           // floor(24/3)=8 + floor(6/5)=1 = 9
        ShouldHave(c, "hit", 37);           // 15 + 12 + 10
        ShouldHave(c, "flee", 31);          // 15 + 6 + 10
        ShouldHave(c, "crit", 1.9);         // 1 + 3×0.3
        ShouldHave(c, "aspd", 1.2466);      // min(3, 1 + 6×0.01 + 12×0.002) = 1.084; × 115/100 = 1.2466
        ShouldHave(c, "move_speed", 4.104); // min(6, 3.5 + 6×0.01) = 3.56; (3.56 + 1) × (100 − 10)/100 = 4.56 × 0.9 = 4.104
        ShouldHave(c, "cast_time_mod", 0.92); // clamp(1 − 12/150, 0.2, 1) = 1 − 0.08
        ShouldHave(c, "hp_regen", 0.8);     // 0.2 + 6×0.05 + 15×0.02 = 0.2 + 0.3 + 0.3
        ShouldHave(c, "sp_regen", 0.97);    // 0.1 + 24×0.03 + 15×0.01 = 0.1 + 0.72 + 0.15
        ShouldHave(c, "weight_limit", 2210); // 2000 + 2×30 + 15×10

        // Statuses expire: swift and iron skin removed.
        c.Modifiers.RemoveBySource("status:status.swift");
        c.Modifiers.RemoveBySource("status:status.iron_skin");
        ShouldHave(c, "move_speed", 3.56);
        ShouldHave(c, "def", 6);
        ShouldHave(c, "aspd", 1.084);
    }
}
