using System.Collections;
using System.Reflection;
using Game.Shared.Definitions;
using Game.Shared.Formulas;

namespace Game.Tests.Formulas;

/// <summary>
/// Keeps <see cref="FormulaAttribute"/> and <see cref="FormulaSources"/> in step (T-110). The test builds each definition with
/// every collection filled, writes a unique sentinel into each [Formula] property, and then compares what <c>FormulaSources.Of</c>
/// returns with the marked properties. Reflection is test-only; the runtime stays reflection-free.
/// </summary>
public class FormulaMarkerTests
{
    private const string DefinitionsNamespace = "Game.Shared.Definitions";
    private const int MaxDepth = 4;

    private static readonly Assembly Shared = typeof(SkillDef).Assembly;

    private static bool IsDefinitionType(Type t) => t.IsClass && t.Namespace == DefinitionsNamespace;

    private static string SentinelOf(PropertyInfo p) => $"@{p.DeclaringType!.Name}.{p.Name}";

    private static IEnumerable<PropertyInfo> MarkedProperties() =>
        Shared.GetTypes().Where(IsDefinitionType)
            .SelectMany(t => t.GetProperties(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
            .Where(p => p.GetCustomAttribute<FormulaAttribute>() != null);

    /// <summary>Roots are the types <c>FormulaSources.Of</c> accepts.</summary>
    private static IEnumerable<MethodInfo> OfOverloads() =>
        typeof(FormulaSources).GetMethods(BindingFlags.Public | BindingFlags.Static).Where(m => m.Name == nameof(FormulaSources.Of));

    [Fact]
    public void Marked_properties_are_strings_or_string_dictionaries()
    {
        foreach (var p in MarkedProperties())
        {
            var ok = p.PropertyType == typeof(string) || p.PropertyType == typeof(IReadOnlyDictionary<string, string>);
            ok.ShouldBeTrue($"{SentinelOf(p)} is marked [Formula] but is a {p.PropertyType.Name}");
        }
    }

    [Fact]
    public void There_are_marked_properties()
    {
        MarkedProperties().Count().ShouldBeGreaterThan(10);
    }

    [Fact]
    public void Every_marked_property_is_returned_by_FormulaSources()
    {
        var seen = SentinelsFound();
        foreach (var p in MarkedProperties())
            seen.ShouldContain(SentinelOf(p), $"{SentinelOf(p)} is marked [Formula] but FormulaSources.Of never returns it");
    }

    [Fact]
    public void Every_FormulaSources_path_has_a_marked_property()
    {
        var marked = MarkedProperties().Select(SentinelOf).ToHashSet();
        foreach (var overload in OfOverloads())
        {
            var root = Populate(overload.GetParameters()[0].ParameterType, 0);
            foreach (var source in (IReadOnlyList<FormulaSource>)overload.Invoke(null, new[] { root })!)
                marked.ShouldContain(source.Text, $"FormulaSources.Of({overload.GetParameters()[0].ParameterType.Name}) returns '{source.Path}' " +
                                                  "but the property behind it has no [Formula] marker");
        }
    }

    private static HashSet<string> SentinelsFound()
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var overload in OfOverloads())
        {
            var root = Populate(overload.GetParameters()[0].ParameterType, 0);
            foreach (var source in (IReadOnlyList<FormulaSource>)overload.Invoke(null, new[] { root })!)
                seen.Add(source.Text);
        }
        return seen;
    }

    /// <summary>Creates a <paramref name="type"/> with sentinels in its [Formula] properties and one of every concrete shape in its collections.</summary>
    private static object Populate(Type type, int depth)
    {
        var instance = Activator.CreateInstance(type, nonPublic: true)!;
        foreach (var p in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (p.SetMethod == null || p.GetIndexParameters().Length > 0) continue;
            var t = p.PropertyType;
            if (p.GetCustomAttribute<FormulaAttribute>() != null)
            {
                p.SetValue(instance, t == typeof(string) ? SentinelOf(p) : new Dictionary<string, string> { ["k"] = SentinelOf(p) });
            }
            else if (depth < MaxDepth && t.IsGenericType && t.GetGenericTypeDefinition() == typeof(IReadOnlyList<>) && IsDefinitionType(t.GetGenericArguments()[0]))
            {
                var element = t.GetGenericArguments()[0];
                var list = (IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(element))!;
                foreach (var concrete in ConcreteTypes(element)) list.Add(Populate(concrete, depth + 1));
                p.SetValue(instance, list);
            }
            else if (IsDefinitionType(t) && !t.IsAbstract && t.GetConstructor(Type.EmptyTypes) != null)
            {
                p.SetValue(instance, Populate(t, depth + 1));
            }
        }
        return instance;
    }

    private static IEnumerable<Type> ConcreteTypes(Type element) =>
        Discriminators.ByBaseType.TryGetValue(element, out var table) ? table.Values
            : element.IsAbstract || element.GetConstructor(Type.EmptyTypes) == null ? Array.Empty<Type>() : new[] { element };
}
