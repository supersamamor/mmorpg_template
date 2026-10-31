using System;
using System.Linq;
using System.Reflection;
using System.Runtime.Versioning;
using System.Text;

namespace MobileSpike;

/// <summary>
/// Proves Game.Shared (netstandard2.1) is packaged and runs on device.
/// Game.Shared has no public types yet (T-002 adds them), so this goes through reflection:
/// it loads the assembly by name, reads its target framework, reads every public static
/// <c>IdPrefix</c> (runs the type's static initializer) and constructs one record and calls
/// its compiler-generated ToString (runs real Shared IL, including <c>init</c> setters).
/// Once T-002 is merged, replace this with direct compile-time calls.
/// </summary>
public static class SharedProbe
{
    public static string Run()
    {
        var sb = new StringBuilder();
        try
        {
            var asm = Assembly.Load(new AssemblyName("Game.Shared"));
            var tfm = asm.GetCustomAttribute<TargetFrameworkAttribute>()?.FrameworkName ?? "?";
            var types = asm.GetExportedTypes().OrderBy(t => t.FullName).ToArray();
            sb.Append($"Game.Shared {asm.GetName().Version} loaded, {tfm}, {types.Length} public types");

            int prefixes = 0;
            foreach (var t in types)
            {
                var f = t.GetField("IdPrefix", BindingFlags.Public | BindingFlags.Static);
                if (f?.GetValue(null) is string p)
                {
                    if (prefixes < 3) sb.Append($"\n  {t.Name}.IdPrefix = \"{p}\"");
                    prefixes++;
                }
            }
            if (prefixes > 3) sb.Append($"\n  (+{prefixes - 3} more IdPrefix)");

            var record = types.FirstOrDefault(t => t.IsClass && !t.IsAbstract && !t.IsGenericTypeDefinition
                                                   && t.GetMethod("<Clone>$") != null
                                                   && t.GetConstructor(Type.EmptyTypes) != null);
            if (record != null)
            {
                var text = Activator.CreateInstance(record)!.ToString() ?? "";
                sb.Append($"\n  new {record.Name}() => {(text.Length > 90 ? text.Substring(0, 90) + "…" : text)}");
            }
            else if (types.Length == 0)
            {
                sb.Append("\n  (no public types yet: only load + metadata verified)");
            }
        }
        catch (Exception e)
        {
            sb.Append($"Game.Shared FAILED: {e.GetType().Name}: {e.Message}");
        }
        return sb.ToString();
    }
}
