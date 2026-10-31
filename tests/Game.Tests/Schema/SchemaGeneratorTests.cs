using System.Text.Json.Nodes;
using Game.Content.Schema;
using Game.Shared.Definitions;
using Game.Shared.Vocabulary;

namespace Game.Tests.Schema;

public class SchemaGeneratorTests
{
    private static readonly SchemaGenerator Generator = new(XmlDocReader.ForAssembly(typeof(ItemDef).Assembly));

    private static JsonObject Schema<T>() => (JsonObject)JsonNode.Parse(Generator.Generate(typeof(T)))!;

    private static string[] Strings(JsonNode? array) => array!.AsArray().Select(n => (string)n!).ToArray();

    [Fact]
    public void ItemDef_has_id_pattern_and_kind_enum()
    {
        var schema = Schema<ItemDef>();
        var properties = schema["properties"]!.AsObject();

        ((string)properties["id"]!["pattern"]!).ShouldBe(@"^item\.[a-z0-9_]+$");
        Strings(properties["kind"]!["enum"]).ShouldBe(ItemKinds.All.Ordered);
        Strings(schema["required"]).ShouldContain("id");
        ((string)properties["id"]!["description"]!).ShouldNotBeNullOrWhiteSpace();
    }

    [Fact]
    public void Every_registry_type_gets_its_id_prefix_pattern()
    {
        foreach (var (type, prefix) in DefinitionIdPrefixes.ByType)
        {
            var schema = JsonNode.Parse(Generator.Generate(type))!;
            ((string)schema["properties"]!["id"]!["pattern"]!).ShouldBe("^" + prefix.Replace(".", "\\.") + "[a-z0-9_]+$");
        }
    }

    [Fact]
    public void Vocabulary_fields_get_enums()
    {
        var monster = Schema<MonsterDef>()["properties"]!;
        Strings(monster["element"]!["enum"]).ShouldBe(Elements.All.Ordered);
        Strings(monster["race"]!["enum"]).ShouldBe(MonsterRaces.All.Ordered);
        Strings(monster["stats"]!["propertyNames"]!["enum"]).ShouldBe(Stats.All.Ordered);

        var equip = Schema<ItemDef>()["properties"]!["equip"]!["properties"]!;
        Strings(equip["slots"]!["items"]!["enum"]).ShouldBe(EquipSlots.All.Ordered);
    }

    [Fact]
    public void Effects_are_a_type_discriminated_union_matching_the_vocabulary()
    {
        var items = Schema<SkillDef>()["properties"]!["effects"]!["items"]!;
        var discriminators = items["anyOf"]!.AsArray()
            .Select(branch => (string)branch!["properties"]!["type"]!["const"]!)
            .ToArray();

        discriminators.ShouldBe(EffectTypes.All.Ordered);
        Strings(items["required"]).ShouldContain("type");
    }

    [Fact]
    public void Quest_objectives_and_rewards_are_discriminated_unions()
    {
        var quest = Schema<QuestDef>()["properties"]!;
        var objectives = quest["steps"]!["items"]!["properties"]!["objectives"]!["items"]!["anyOf"]!.AsArray();
        var rewards = quest["rewards"]!["items"]!["anyOf"]!.AsArray();

        objectives.Select(b => (string)b!["properties"]!["type"]!["const"]!).ShouldBe(QuestObjectiveTypes.All.Ordered);
        rewards.Select(b => (string)b!["properties"]!["type"]!["const"]!).ShouldBe(RewardTypes.All.Ordered);
    }

    [Fact]
    public void Unknown_keys_are_rejected()
    {
        Schema<ItemDef>()["additionalProperties"]!.GetValue<bool>().ShouldBeFalse();
    }

    [Fact]
    public void Generation_is_deterministic_and_uses_lf()
    {
        var first = Generator.GenerateAll();
        var second = new SchemaGenerator(XmlDocReader.ForAssembly(typeof(ItemDef).Assembly)).GenerateAll();

        first.ShouldBe(second);
        first.Select(f => f.FileName).Distinct().Count().ShouldBe(SchemaGenerator.RootTypes.Count);
        foreach (var (_, json) in first)
        {
            json.ShouldNotContain("\r");
            json.ShouldEndWith("}\n");
        }
    }

    [Fact]
    public void Committed_schemas_are_up_to_date()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "Game.sln"))) root = root.Parent;
        root.ShouldNotBeNull();

        foreach (var (fileName, json) in Generator.GenerateAll())
        {
            var path = Path.Combine(root!.FullName, "schemas", fileName);
            File.Exists(path).ShouldBeTrue($"{fileName} is missing; run `dotnet run --project src/Game.Tools -- schema`");
            File.ReadAllText(path).Replace("\r\n", "\n").ShouldBe(json, $"{fileName} is stale; run `dotnet run --project src/Game.Tools -- schema`");
        }
    }
}
