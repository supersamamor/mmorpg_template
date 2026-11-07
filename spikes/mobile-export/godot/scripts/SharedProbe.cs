using System;
using System.Reflection;
using System.Runtime.Versioning;
using System.Text;
using Game.Shared.Definitions;
using Game.Shared.Formulas;
using Game.Shared.Random;
using Game.Shared.Vocabulary;

namespace MobileSpike;

/// <summary>
/// Proves Game.Shared (netstandard2.1) is packaged and runs on device, through ordinary compile-time calls
/// (no reflection over Game.Shared, so the trimmer and NativeAOT see exactly what a real client would use):
/// constructs a definition record and uses its <c>with</c> copy and value equality, compiles and evaluates a
/// formula, and checks <see cref="Pcg32Random"/> against the PCG reference output.
/// Every value is a probe constant, not game content.
/// </summary>
public static class SharedProbe
{
    // pcg32-demo reference output for pcg32_srandom_r(42, 54): first two values.
    const uint PcgRef0 = 0xa15c02b7, PcgRef1 = 0x7b47f409;

    public static string Run()
    {
        var sb = new StringBuilder();
        try
        {
            var asm = typeof(Formula).Assembly;
            var tfm = asm.GetCustomAttribute<TargetFrameworkAttribute>()?.FrameworkName ?? "?";
            sb.Append($"Game.Shared {asm.GetName().Version} loaded, {tfm}");

            var exp = new ExpTableDef { Id = ExpTableDef.IdPrefix + "probe", Exp = new long[] { 0, 10, 30 } };
            var copy = exp with { Name = "probe" };
            sb.Append($"\n  record: {exp.Id}, {exp.Exp.Count} rows, with-copy equal={copy == exp} (expect False), {(exp with { }) == exp} (expect True)");

            var formula = Formula.Compile("max(1, floor(base_level * 1.5 + 2))");
            var ctx = new FormulaContext();
            ctx[FormulaVariables.BaseLevel] = 10;
            var value = formula.Evaluate(ctx);
            sb.Append($"\n  formula: \"{formula.Source}\" @ base_level=10 = {value} (expect 17) {(value == 17 ? "OK" : "MISMATCH")}");

            var rng = new Pcg32Random(42, 54);
            uint a = rng.NextUInt(), b = rng.NextUInt();
            var roll = new Pcg32Random(7).Next(100);
            sb.Append($"\n  pcg32: 0x{a:x8} 0x{b:x8} {(a == PcgRef0 && b == PcgRef1 ? "OK" : "MISMATCH")}, roll(100) seed 7 = {roll}");
        }
        catch (Exception e)
        {
            sb.Append($"\n  Game.Shared FAILED: {e.GetType().Name}: {e.Message}");
        }
        return sb.ToString();
    }
}
