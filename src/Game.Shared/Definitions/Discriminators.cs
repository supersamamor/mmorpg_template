using System.Collections.Generic;
using System.Collections.ObjectModel;
using Game.Shared.Vocabulary;

namespace Game.Shared.Definitions
{
    /// <summary>
    /// The single source of truth for polymorphic definitions: discriminator value → concrete record type,
    /// per abstract base. Serializer-neutral on purpose (no YamlDotNet or System.Text.Json attributes),
    /// so Game.Shared stays dependency-free and trim/AOT-safe. Game.Content feeds these tables to
    /// YamlDotNet (key/value type discriminator) and to System.Text.Json (<c>JsonPolymorphismOptions</c>),
    /// and removes the computed <c>Type</c> property from the member list, because the discriminator
    /// key already carries it.
    /// </summary>
    public static class Discriminators
    {
        /// <summary>Key that holds the discriminator in YAML/JSON.</summary>
        public const string PropertyName = "type";

        /// <summary><see cref="EffectDef"/> subtypes by <see cref="EffectTypes"/> value.</summary>
        public static readonly IReadOnlyDictionary<string, System.Type> Effects = Table(
            (EffectTypes.Damage, typeof(DamageEffect)),
            (EffectTypes.Heal, typeof(HealEffect)),
            (EffectTypes.RestoreSp, typeof(RestoreSpEffect)),
            (EffectTypes.ApplyStatus, typeof(ApplyStatusEffect)),
            (EffectTypes.RemoveStatus, typeof(RemoveStatusEffect)),
            (EffectTypes.ModifyStat, typeof(ModifyStatEffect)),
            (EffectTypes.Knockback, typeof(KnockbackEffect)),
            (EffectTypes.Teleport, typeof(TeleportEffect)),
            (EffectTypes.Summon, typeof(SummonEffect)),
            (EffectTypes.Area, typeof(AreaEffect)));

        /// <summary><see cref="QuestObjectiveDef"/> subtypes by <see cref="QuestObjectiveTypes"/> value.</summary>
        public static readonly IReadOnlyDictionary<string, System.Type> QuestObjectives = Table(
            (QuestObjectiveTypes.Kill, typeof(KillObjective)),
            (QuestObjectiveTypes.Collect, typeof(CollectObjective)),
            (QuestObjectiveTypes.Talk, typeof(TalkObjective)),
            (QuestObjectiveTypes.Reach, typeof(ReachObjective)),
            (QuestObjectiveTypes.Level, typeof(LevelObjective)),
            (QuestObjectiveTypes.Job, typeof(JobObjective)),
            (QuestObjectiveTypes.UseItem, typeof(UseItemObjective)),
            (QuestObjectiveTypes.Equip, typeof(EquipObjective)));

        /// <summary><see cref="RewardDef"/> subtypes by <see cref="RewardTypes"/> value.</summary>
        public static readonly IReadOnlyDictionary<string, System.Type> Rewards = Table(
            (RewardTypes.Exp, typeof(ExpReward)),
            (RewardTypes.Item, typeof(ItemReward)),
            (RewardTypes.Zeny, typeof(CurrencyReward)),
            (RewardTypes.SkillPoint, typeof(SkillPointReward)),
            (RewardTypes.StatPoint, typeof(StatPointReward)),
            (RewardTypes.UnlockJob, typeof(UnlockJobReward)),
            (RewardTypes.Status, typeof(StatusReward)));

        /// <summary>Every polymorphic base type and its discriminator table.</summary>
        public static readonly IReadOnlyDictionary<System.Type, IReadOnlyDictionary<string, System.Type>> ByBaseType =
            new ReadOnlyDictionary<System.Type, IReadOnlyDictionary<string, System.Type>>(
                new Dictionary<System.Type, IReadOnlyDictionary<string, System.Type>>
                {
                    [typeof(EffectDef)] = Effects,
                    [typeof(QuestObjectiveDef)] = QuestObjectives,
                    [typeof(RewardDef)] = Rewards,
                });

        private static IReadOnlyDictionary<string, System.Type> Table(params (string Discriminator, System.Type Type)[] entries)
        {
            var map = new Dictionary<string, System.Type>(System.StringComparer.Ordinal);
            foreach (var (discriminator, type) in entries) map.Add(discriminator, type);
            return new ReadOnlyDictionary<string, System.Type>(map);
        }
    }
}
