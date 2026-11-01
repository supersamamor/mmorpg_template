using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Schema;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using Game.Shared.Definitions;
using Game.Shared.World;

namespace Game.Content.Schema;

/// <summary>Generates one JSON Schema per content file type from the C# definition records.</summary>
public sealed class SchemaGenerator
{
    private const string SchemaUri = "https://json-schema.org/draft/2020-12/schema";

    /// <summary>The schema files: file stem (<c>schemas/&lt;stem&gt;.schema.json</c>) → root definition type.</summary>
    public static readonly IReadOnlyList<(string Name, Type Type)> RootTypes = new (string, Type)[]
    {
        ("job", typeof(JobDef)),
        ("skill", typeof(SkillDef)),
        ("item", typeof(ItemDef)),
        ("monster", typeof(MonsterDef)),
        ("status", typeof(StatusDef)),
        ("quest", typeof(QuestDef)),
        ("npc", typeof(NpcDef)),
        ("dialogue", typeof(DialogueDef)),
        ("drop_table", typeof(DropTableDef)),
        ("map", typeof(MapDef)),
        ("map-grid", typeof(MapGridFile)),
        ("exp_table", typeof(ExpTableDef)),
        ("element_table", typeof(ElementTableDef)),
        ("formula_set", typeof(FormulaSetDef)),
        ("balance", typeof(BalanceDef)),
        ("appearance", typeof(AppearanceDef)),
    };

    private static readonly JsonSerializerOptions OutputOptions = new()
    {
        WriteIndented = true,
        NewLine = "\n",
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private readonly XmlDocReader _docs;
    private readonly JsonSerializerOptions _options;

    /// <summary>Creates a generator that takes descriptions from <paramref name="docs"/>.</summary>
    public SchemaGenerator(XmlDocReader docs)
    {
        _docs = docs;
        _options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
            UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
            TypeInfoResolver = new DefaultJsonTypeInfoResolver { Modifiers = { ConfigurePolymorphism } },
            Converters = { new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower) },
        };
    }

    /// <summary>Generates every schema, in <see cref="RootTypes"/> order, as stable text (LF, trailing newline).</summary>
    public IReadOnlyList<(string FileName, string Json)> GenerateAll() =>
        RootTypes.Select(r => (r.Name + ".schema.json", Generate(r.Type))).ToList();

    /// <summary>Generates the schema for one definition type.</summary>
    public string Generate(Type type)
    {
        var exporterOptions = new JsonSchemaExporterOptions
        {
            TreatNullObliviousAsNonNullable = true,
            TransformSchemaNode = Transform,
        };
        var generated = (JsonObject)_options.GetJsonSchemaAsNode(type, exporterOptions);

        var root = new JsonObject { ["$schema"] = SchemaUri, ["title"] = type.Name };
        foreach (var (key, value) in generated.ToList())
        {
            generated.Remove(key);
            root[key] = value;
        }

        return root.ToJsonString(OutputOptions) + "\n";
    }

    /// <summary>
    /// Effects, objectives and rewards become a <c>type</c>-discriminated union, from <see cref="Discriminators"/>.
    /// The computed <c>Type</c> property is dropped: the discriminator key carries it.
    /// </summary>
    private static void ConfigurePolymorphism(JsonTypeInfo info)
    {
        if (Discriminators.ByBaseType.TryGetValue(info.Type, out var table))
        {
            info.PolymorphismOptions = new JsonPolymorphismOptions
            {
                TypeDiscriminatorPropertyName = Discriminators.PropertyName,
                IgnoreUnrecognizedTypeDiscriminators = false,
                UnknownDerivedTypeHandling = JsonUnknownDerivedTypeHandling.FailSerialization,
            };
            foreach (var (discriminator, derived) in table)
                info.PolymorphismOptions.DerivedTypes.Add(new JsonDerivedType(derived, discriminator));
        }

        if (info.Kind == JsonTypeInfoKind.Object && Discriminators.ByBaseType.Keys.Any(b => b.IsAssignableFrom(info.Type)))
        {
            var computed = info.Properties.FirstOrDefault(p => p.Name == Discriminators.PropertyName);
            if (computed is not null) info.Properties.Remove(computed);
        }
    }

    private JsonNode Transform(JsonSchemaExporterContext context, JsonNode node)
    {
        if (node is not JsonObject schema) return node;

        if (context.PropertyInfo is { } property && property.AttributeProvider is MemberInfo member)
        {
            ApplyPropertyRules(property.DeclaringType, member.Name, schema);
            if (_docs.ForProperty(property.DeclaringType, member.Name) is { } description)
                schema["description"] = description;
        }
        else
        {
            var type = context.TypeInfo.Type;
            if ((schema.ContainsKey("properties") || schema.ContainsKey("anyOf")) && _docs.ForType(type) is { } description)
                schema["description"] = description;
            if (schema["properties"] is JsonObject properties)
                AddRequired(type, schema, properties);
        }

        return schema;
    }

    private static void ApplyPropertyRules(Type declaringType, string name, JsonObject schema)
    {
        // Applies to the string schema itself or to the items of a list of strings.
        var target = schema["items"] as JsonObject ?? schema;

        string? pattern = null;
        if (name == nameof(IDefinition.Id)) pattern = SchemaRules.IdPattern(declaringType);
        var isEnum = SchemaRules.Enums.TryGetValue(name, out var vocabulary);
        if (!isEnum) pattern ??= SchemaRules.ReferencePattern(name) ?? SchemaRules.AssetKeyPattern(name);
        if (pattern is not null) target["pattern"] = pattern;
        if (isEnum) target["enum"] = EnumArray(target, vocabulary!.Ordered);

        if (SchemaRules.DictionaryKeys.TryGetValue((declaringType, name), out var keySets))
        {
            var level = schema;
            foreach (var keys in keySets)
            {
                level["propertyNames"] = new JsonObject { ["enum"] = EnumArray(null, keys.Ordered) };
                if (level["additionalProperties"] is not JsonObject inner) break;
                level = inner;
            }
        }
    }

    /// <summary>Marks string members whose C# default is <c>""</c> as required: the default is a placeholder, not a value.</summary>
    private static void AddRequired(Type type, JsonObject schema, JsonObject properties)
    {
        if (type.IsAbstract || type.GetConstructor(Type.EmptyTypes) is null) return;
        var instance = Activator.CreateInstance(type)!;
        var required = schema["required"] as JsonArray ?? new JsonArray();
        var present = required.Select(n => (string)n!).ToHashSet(StringComparer.Ordinal);

        foreach (var clr in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (clr.PropertyType != typeof(string) || !clr.CanWrite) continue;
            if (clr.GetValue(instance) is not "") continue;
            var json = JsonNamingPolicy.SnakeCaseLower.ConvertName(clr.Name);
            if (properties.ContainsKey(json) && present.Add(json)) required.Add(json);
        }

        if (required.Count > 0) schema["required"] = required;
    }

    private static JsonArray EnumArray(JsonObject? target, IEnumerable<string> values)
    {
        var array = new JsonArray();
        foreach (var value in values) array.Add(value);
        if (target?["type"] is JsonArray types && types.Any(t => (string?)t == "null")) array.Add(null);
        return array;
    }
}
