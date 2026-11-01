using Game.Shared.Definitions;
using YamlDotNet.Core;
using YamlDotNet.Core.Events;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;
using YamlDotNet.Serialization.TypeInspectors;

namespace Game.Content.Loading;

/// <summary>
/// Builds the strict YamlDotNet deserializer for the definition records: underscored names, unknown keys are
/// errors (YamlDotNet's default; <c>IgnoreUnmatchedProperties</c> is deliberately not called), read-only
/// collections bind, and polymorphic records use the <see cref="Discriminators"/> tables from Game.Shared.
/// </summary>
internal static class YamlSetup
{
    public static IDeserializer BuildDeserializer() => new DeserializerBuilder()
        .WithNamingConvention(UnderscoredNamingConvention.Instance)
        .WithTypeInspector(inner => new SkipDiscriminatorInspector(inner))
        .WithNodeDeserializer(new ReadOnlyCollectionsDeserializer(), s => s.OnTop())
        .WithTypeDiscriminatingNodeDeserializer(o =>
        {
            o.AddKeyValueTypeDiscriminator<EffectDef>(Discriminators.PropertyName, ToMap(Discriminators.Effects));
            o.AddKeyValueTypeDiscriminator<QuestObjectiveDef>(Discriminators.PropertyName, ToMap(Discriminators.QuestObjectives));
            o.AddKeyValueTypeDiscriminator<RewardDef>(Discriminators.PropertyName, ToMap(Discriminators.Rewards));
        })
        .Build();

    private static IDictionary<string, Type> ToMap(IReadOnlyDictionary<string, Type> table) =>
        table.ToDictionary(kv => kv.Key, kv => kv.Value);

    /// <summary>Hides the computed <c>Type</c> of polymorphic records: the <c>type</c> key carries it and is accepted as a no-op.</summary>
    private sealed class SkipDiscriminatorInspector : TypeInspectorSkeleton
    {
        private readonly ITypeInspector _inner;
        public SkipDiscriminatorInspector(ITypeInspector inner) => _inner = inner;

        public override string GetEnumName(Type enumType, string name) => _inner.GetEnumName(enumType, name);
        public override string GetEnumValue(object enumValue) => _inner.GetEnumValue(enumValue);
        public override bool HasParseMethod(Type type) => _inner.HasParseMethod(type);
        public override object? Parse(string value, Type type) => _inner.Parse(value, type);

        public override IEnumerable<IPropertyDescriptor> GetProperties(Type type, object? container)
        {
            var props = _inner.GetProperties(type, container);
            var polymorphic = Discriminators.ByBaseType.Keys.Any(b => b.IsAssignableFrom(type));
            return polymorphic
                ? props.Where(p => p.Name != Discriminators.PropertyName).Append(new DiscriminatorKeyDescriptor())
                : props;
        }
    }

    private sealed class DiscriminatorKeyDescriptor : IPropertyDescriptor
    {
        public string Name => Discriminators.PropertyName;
        public bool AllowNulls => false;
        public bool CanWrite => true;
        public Type Type => typeof(string);
        public Type? TypeOverride { get; set; }
        public int Order { get; set; }
        public ScalarStyle ScalarStyle { get; set; }
        public bool Required => true;
        public Type? ConverterType => null;
        public T? GetCustomAttribute<T>() where T : Attribute => null;
        public IObjectDescriptor Read(object target) => throw new NotSupportedException();
        public void Write(object target, object? value) { }
    }

    /// <summary>YamlDotNet cannot bind <c>IReadOnlyList&lt;T&gt;</c> / <c>IReadOnlyDictionary&lt;K,V&gt;</c> itself; deserialize the mutable implementation instead.</summary>
    private sealed class ReadOnlyCollectionsDeserializer : INodeDeserializer
    {
        public bool Deserialize(IParser reader, Type expectedType, Func<IParser, Type, object?> nestedObjectDeserializer, out object? value, ObjectDeserializer rootDeserializer)
        {
            if (expectedType.IsGenericType)
            {
                var def = expectedType.GetGenericTypeDefinition();
                var args = expectedType.GetGenericArguments();
                Type? impl = def == typeof(IReadOnlyList<>) || def == typeof(IReadOnlyCollection<>) ? typeof(List<>).MakeGenericType(args)
                    : def == typeof(IReadOnlyDictionary<,>) ? typeof(Dictionary<,>).MakeGenericType(args)
                    : null;
                if (impl != null)
                {
                    value = nestedObjectDeserializer(reader, impl);
                    return true;
                }
            }
            value = null;
            return false;
        }
    }
}
