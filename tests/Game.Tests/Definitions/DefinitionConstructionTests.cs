using Game.Shared.Definitions;
using Game.Shared.Vocabulary;

namespace Game.Tests.Definitions;

// One of each definition built in code. Ids and names here are throwaway test values, not content.
public class DefinitionConstructionTests
{
    [Fact]
    public void Constructs_JobDef()
    {
        var job = new JobDef
        {
            Id = "job.test_fighter",
            Name = "Test Fighter",
            ParentJobId = "job.test_novice",
            Requirements = new JobChangeRequirementsDef { MinJobLevel = 10, QuestIds = ["quest.test"] },
            JobExpTableId = "exp.test_job",
            Skills = [new JobSkillDef { SkillId = "skill.test_b", Requires = [new SkillRequirementDef { SkillId = "skill.test_a", Level = 3 }] }],
            StatBonuses = [new ModifierDef(PrimaryStats.Str, ModifierOp.Flat, "job_level / 10")],
            WeaponTypes = [WeaponTypes.Sword1h],
            ModelAssetKey = "model.char.test",
        };

        job.Id.ShouldStartWith(JobDef.IdPrefix);
        job.Skills[0].Requires[0].Level.ShouldBe(3);
        job.Skills[0].MaxLevel.ShouldBeNull();
    }

    [Fact]
    public void Constructs_SkillDef()
    {
        var skill = new SkillDef
        {
            Id = "skill.test_strike",
            Name = "Test Strike",
            MaxLevel = 10,
            Target = new SkillTargetDef { Range = 1 },
            SpCost = "8",
            Effects = [new DamageEffect { Multiplier = "1 + level * 0.3" }, new KnockbackEffect { Cells = 2 }],
            AnimationId = AnimationIds.Attack,
            IconAssetKey = "icon.skill.test_strike",
        };

        skill.Id.ShouldStartWith(SkillDef.IdPrefix);
        skill.Effects.Select(e => e.Type).ShouldBe([EffectTypes.Damage, EffectTypes.Knockback]);
        skill.Target.Friendly.ShouldBeFalse();
    }

    [Fact]
    public void Constructs_ItemDef_uses_optional_blocks_per_kind()
    {
        var potion = new ItemDef
        {
            Id = "item.test_potion",
            Name = "Test Potion",
            Kind = ItemKind.Consumable,
            MaxStack = 100,
            Consumable = new ItemConsumableDef { Effects = [new HealEffect { Amount = "40" }] },
        };
        var sword = new ItemDef
        {
            Id = "item.test_sword",
            Name = "Test Sword",
            Kind = ItemKind.Equip,
            Equip = new ItemEquipDef
            {
                Slots = [EquipSlots.Weapon],
                WeaponType = WeaponTypes.Sword1h,
                WeaponAtk = 25,
                CardSlots = 1,
                Refinable = true,
                VisualAssetKey = "model.equip.test_sword",
            },
        };
        var card = new ItemDef
        {
            Id = "item.test_card",
            Name = "Test Card",
            Kind = ItemKind.Card,
            Card = new ItemCardDef { Slots = [EquipSlots.Weapon], Modifiers = [new ModifierDef(DerivedStats.Atk, ModifierOp.Flat, "5")] },
        };

        potion.Id.ShouldStartWith(ItemDef.IdPrefix);
        potion.Equip.ShouldBeNull();
        potion.Consumable!.Consumed.ShouldBeTrue();
        sword.Equip!.AttackRange.ShouldBe(1);
        card.Card!.Modifiers[0].Op.ShouldBe(ModifierOp.Flat);
    }

    [Fact]
    public void Constructs_MonsterDef()
    {
        var monster = new MonsterDef
        {
            Id = "monster.test_blob",
            Name = "Test Blob",
            Level = 3,
            Race = MonsterRaces.Formless,
            Size = MonsterSizes.Small,
            Element = Elements.Water,
            Stats = new Dictionary<string, double> { [PrimaryStats.Vit] = 4, [DerivedStats.MaxHp] = 60, [DerivedStats.MoveSpeed] = 2.5 },
            BaseExp = 12,
            JobExp = 8,
            DropTableIds = ["drop.test_blob"],
            Ai = new MonsterAiDef { Aggressive = false, ChaseRange = 8, Skills = [new MonsterSkillDef { SkillId = "skill.test_strike", Chance = 0.1 }] },
        };

        monster.Id.ShouldStartWith(MonsterDef.IdPrefix);
        monster.Stats[DerivedStats.MoveSpeed].ShouldBe(2.5);
        monster.Ai.Skills[0].Level.ShouldBe(1);
    }

    [Fact]
    public void Constructs_StatusDef()
    {
        var status = new StatusDef
        {
            Id = "status.test_poison",
            Name = "Test Poison",
            Debuff = true,
            Tags = ["test_dot"],
            OnApply = [new ModifyStatEffect { Modifier = new ModifierDef(DerivedStats.Def, ModifierOp.Percent, "-10") }],
            TickIntervalMs = 1000,
            OnTick = [new DamageEffect { Multiplier = "0.05", Element = Elements.Poison }],
        };

        status.Id.ShouldStartWith(StatusDef.IdPrefix);
        status.MaxStacks.ShouldBe(1);
        status.OnTick[0].Type.ShouldBe(EffectTypes.Damage);
    }

    [Fact]
    public void Constructs_QuestDef()
    {
        var quest = new QuestDef
        {
            Id = "quest.test_hunt",
            Name = "Test Hunt",
            GiverNpcId = "npc.test_guide",
            Prerequisites = new QuestPrerequisitesDef { MinBaseLevel = 2 },
            Steps =
            [
                new QuestStepDef { Objectives = [new KillObjective { MonsterId = "monster.test_blob", Count = 5 }] },
                new QuestStepDef { Objectives = [new TalkObjective { NpcId = "npc.test_guide" }] },
            ],
            Rewards = [new ExpReward { Base = 100, Job = 40 }, new ItemReward { ItemId = "item.test_potion", Count = 3 }],
        };

        quest.Id.ShouldStartWith(QuestDef.IdPrefix);
        quest.Steps.SelectMany(s => s.Objectives).Select(o => o.Type).ShouldBe([QuestObjectiveTypes.Kill, QuestObjectiveTypes.Talk]);
        quest.Rewards.Select(r => r.Type).ShouldBe([RewardTypes.Exp, RewardTypes.Item]);
    }

    [Fact]
    public void Constructs_NpcDef()
    {
        var npc = new NpcDef
        {
            Id = "npc.test_guide",
            Name = "Test Guide",
            DialogueId = "dialogue.test_intro",
            ModelAssetKey = "model.npc.test_guide",
            PortraitAssetKey = "portrait.test_guide",
        };

        npc.Id.ShouldStartWith(NpcDef.IdPrefix);
    }

    [Fact]
    public void Constructs_DialogueDef()
    {
        var dialogue = new DialogueDef
        {
            Id = "dialogue.test_intro",
            Name = "Test intro",
            Start = "hello",
            Nodes = new Dictionary<string, DialogueNodeDef>
            {
                ["hello"] = new() { Text = "Hello.", Choices = [new DialogueChoiceDef { Text = "Help?", Next = "quest", StartQuestId = "quest.test_hunt" }] },
                ["quest"] = new() { Text = "Go hunt." },
            },
        };

        dialogue.Id.ShouldStartWith(DialogueDef.IdPrefix);
        dialogue.Nodes[dialogue.Start].Choices[0].Next.ShouldBe("quest");
        dialogue.Nodes["quest"].Next.ShouldBeNull();
    }

    [Fact]
    public void Constructs_DropTableDef()
    {
        var drops = new DropTableDef
        {
            Id = "drop.test_blob",
            Entries = [new DropEntryDef { ItemId = "item.test_potion", Chance = 0.25, Max = 2 }],
        };

        drops.Id.ShouldStartWith(DropTableDef.IdPrefix);
        drops.Name.ShouldBeNull();
        drops.Entries[0].Min.ShouldBe(1);
    }

    [Fact]
    public void Constructs_MapDef_holds_metadata_only()
    {
        var map = new MapDef
        {
            Id = "map.test_town",
            Name = "Test Town",
            SceneAssetKey = "scene.map.test_town",
            MusicAssetKey = "music.test_town",
            GridFile = "test_town.grid.json",
        };

        map.Id.ShouldStartWith(MapDef.IdPrefix);
        map.SceneAssetKey.ShouldStartWith(AssetKeyPrefixes.Scene);
        typeof(MapDef).GetProperties().Select(p => p.Name)
            .ShouldBe(["Id", "Name", "SceneAssetKey", "MusicAssetKey", "AmbienceAssetKey", "GridFile"], ignoreOrder: true);
    }

    [Fact]
    public void Constructs_ExpTableDef()
    {
        var table = new ExpTableDef { Id = "exp.test_base", Exp = [10, 25, 60] };

        table.Id.ShouldStartWith(ExpTableDef.IdPrefix);
        (table.Exp.Count + 1).ShouldBe(4); // max level
    }

    [Fact]
    public void Constructs_ElementTableDef()
    {
        var table = new ElementTableDef
        {
            Multipliers = new Dictionary<string, IReadOnlyDictionary<string, double>>
            {
                [Elements.Fire] = new Dictionary<string, double> { [Elements.Water] = 0.5, [Elements.Earth] = 1.5 },
            },
        };

        table.Multipliers[Elements.Fire][Elements.Earth].ShouldBe(1.5);
    }

    [Fact]
    public void Constructs_FormulaSetDef()
    {
        var formulas = new FormulaSetDef
        {
            Derived = new Dictionary<string, string> { [DerivedStats.MaxHp] = "35 + base_level * 5 + vit * 3" },
            PhysicalDamage = "atk - t_def",
            MagicDamage = "matk - t_mdef",
            HitChance = "clamp((hit - t_flee) / 100 + 0.8, 0.05, 1)",
            CritChance = "luk / 300",
        };

        formulas.Derived[DerivedStats.MaxHp].ShouldContain("vit");
    }

    [Fact]
    public void Defaults_are_empty_not_null()
    {
        var skill = new SkillDef();
        skill.Effects.ShouldBeEmpty();
        skill.WeaponTypes.ShouldBeEmpty();
        new DialogueDef().Nodes.ShouldBeEmpty();
        new MonsterDef().Stats.ShouldBeEmpty();
        new ElementTableDef().Multipliers.ShouldBeEmpty();
    }

    [Fact]
    public void Records_are_value_equal_and_immutable()
    {
        var a = new ModifierDef(PrimaryStats.Agi, ModifierOp.Flat, "3");
        a.ShouldBe(new ModifierDef { Stat = PrimaryStats.Agi, Op = ModifierOp.Flat, Value = "3" });
        (a with { Value = "4" }).ShouldNotBe(a);

        foreach (var type in typeof(IDefinition).Assembly.GetTypes().Where(t => t.Namespace == typeof(IDefinition).Namespace && t.IsPublic && t.IsClass && t.IsSealed && !t.IsAbstract))
        {
            foreach (var property in type.GetProperties())
            {
                var setter = property.SetMethod;
                if (setter == null) continue;
                setter.ReturnParameter.GetRequiredCustomModifiers()
                    .ShouldContain(typeof(System.Runtime.CompilerServices.IsExternalInit), $"{type.Name}.{property.Name} must be init-only");
            }
        }
    }

    [Fact]
    public void Every_registry_definition_has_a_prefix_in_vocabulary()
    {
        var definitionTypes = typeof(IDefinition).Assembly.GetTypes()
            .Where(t => typeof(IDefinition).IsAssignableFrom(t) && !t.IsInterface)
            .ToList();

        DefinitionIdPrefixes.ByType.Keys.ShouldBe(definitionTypes, ignoreOrder: true);
        DefinitionIdPrefixes.ByType.Values.ShouldBe(IdPrefixes.All.Ordered, ignoreOrder: true);
        foreach (var type in definitionTypes)
        {
            type.IsSealed.ShouldBeTrue(type.Name);
            type.GetField("IdPrefix")!.GetRawConstantValue().ShouldBe(DefinitionIdPrefixes.ByType[type]);
        }
    }
}
