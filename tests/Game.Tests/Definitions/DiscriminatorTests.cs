using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using Game.Shared.Definitions;
using Game.Shared.Vocabulary;

namespace Game.Tests.Definitions;

public class DiscriminatorTests
{
    public static TheoryData<Type, VocabularySet> Bases => new()
    {
        { typeof(EffectDef), EffectTypes.All },
        { typeof(QuestObjectiveDef), QuestObjectiveTypes.All },
        { typeof(RewardDef), RewardTypes.All },
    };

    [Theory]
    [MemberData(nameof(Bases))]
    public void Every_vocabulary_type_has_exactly_one_record(Type baseType, VocabularySet vocabulary)
    {
        var table = Discriminators.ByBaseType[baseType];
        table.Keys.ShouldBe(vocabulary.Ordered, ignoreOrder: true);

        foreach (var (discriminator, type) in table)
        {
            type.IsSealed.ShouldBeTrue(type.Name);
            baseType.IsAssignableFrom(type).ShouldBeTrue(type.Name);
            var instance = Activator.CreateInstance(type)!;
            type.GetProperty("Type")!.GetValue(instance).ShouldBe(discriminator);
        }
    }

    [Theory]
    [MemberData(nameof(Bases))]
    public void Every_concrete_record_is_registered(Type baseType, VocabularySet _)
    {
        var concrete = typeof(EffectDef).Assembly.GetTypes()
            .Where(t => !t.IsAbstract && baseType.IsAssignableFrom(t));
        Discriminators.ByBaseType[baseType].Values.ShouldBe(concrete, ignoreOrder: true);
    }

    [Fact]
    public void System_Text_Json_round_trips_polymorphic_lists()
    {
        var skill = new SkillDef
        {
            Id = "skill.test_burst",
            Name = "Test Burst",
            MaxLevel = 3,
            Target = new SkillTargetDef { Range = 5, Ground = true },
            Effects =
            [
                new AreaEffect
                {
                    Radius = 2,
                    Effects =
                    [
                        new DamageEffect { Magic = true, Multiplier = "1 + level", Element = Elements.Fire },
                        new ApplyStatusEffect { StatusId = "status.test", Chance = "0.5", DurationMs = "2000" },
                    ],
                },
                new ModifyStatEffect { Modifier = new ModifierDef(DerivedStats.Atk, ModifierOp.Percent, "5") },
            ],
        };

        var json = JsonSerializer.Serialize(skill, Options);
        var back = JsonSerializer.Deserialize<SkillDef>(json, Options)!;

        JsonSerializer.Serialize(back, Options).ShouldBe(json);
        var area = back.Effects[0].ShouldBeOfType<AreaEffect>();
        area.Effects[0].ShouldBe(new DamageEffect { Magic = true, Multiplier = "1 + level", Element = Elements.Fire });
        back.Effects[1].ShouldBeOfType<ModifyStatEffect>().Modifier.Op.ShouldBe(ModifierOp.Percent);
        json.ShouldContain("\"type\":\"area\"");
        json.ShouldContain("\"op\":\"percent\"");
    }

    [Fact]
    public void System_Text_Json_reads_hand_written_snake_case()
    {
        const string json = """
            {
              "id": "quest.test", "name": "Test",
              "steps": [ { "objectives": [ { "type": "kill", "monster_id": "monster.test", "count": 3 } ] } ],
              "rewards": [ { "type": "currency", "amount": 50 }, { "type": "unlock_job", "job_id": "job.test" } ]
            }
            """;
        var quest = JsonSerializer.Deserialize<QuestDef>(json, Options)!;

        quest.Steps[0].Objectives[0].ShouldBe(new KillObjective { MonsterId = "monster.test", Count = 3 });
        quest.Rewards[0].ShouldBe(new CurrencyReward { Amount = 50 });
        quest.Rewards[1].ShouldBe(new UnlockJobReward { JobId = "job.test" });
    }

    [Fact]
    public void System_Text_Json_rejects_unknown_type()
    {
        Should.Throw<JsonException>(() => JsonSerializer.Deserialize<List<EffectDef>>("""[ { "type": "nope" } ]""", Options));
    }

    [Fact]
    public void System_Text_Json_rejects_missing_type()
    {
        // STJ reports a missing discriminator on an abstract base as NotSupportedException, not JsonException.
        Should.Throw<NotSupportedException>(() => JsonSerializer.Deserialize<List<EffectDef>>("""[ { "magic": true } ]""", Options));
    }

    // How Game.Content is expected to wire the tables into System.Text.Json (and JsonSchemaExporter in T-003).
    private static readonly JsonSerializerOptions Options = CreateOptions();

    private static JsonSerializerOptions CreateOptions()
    {
        var resolver = new DefaultJsonTypeInfoResolver();
        resolver.Modifiers.Add(info =>
        {
            if (info.Kind != JsonTypeInfoKind.Object) return;
            if (!Discriminators.ByBaseType.Keys.Any(b => b.IsAssignableFrom(info.Type))) return;

            // The discriminator key carries the computed Type; serializing it as a property would collide.
            var typeProperty = info.Properties.Single(p => p.Name == Discriminators.PropertyName);
            info.Properties.Remove(typeProperty);

            if (Discriminators.ByBaseType.TryGetValue(info.Type, out var table))
            {
                info.PolymorphismOptions = new JsonPolymorphismOptions
                {
                    TypeDiscriminatorPropertyName = Discriminators.PropertyName,
                    UnknownDerivedTypeHandling = JsonUnknownDerivedTypeHandling.FailSerialization,
                };
                foreach (var (discriminator, type) in table)
                    info.PolymorphismOptions.DerivedTypes.Add(new JsonDerivedType(type, discriminator));
            }
        });

        return new JsonSerializerOptions
        {
            TypeInfoResolver = resolver,
            PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
            UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
            Converters = { new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower) },
        };
    }
}
