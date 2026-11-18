using System.Collections.Generic;
using Game.Shared.Definitions;

namespace Game.Shared.Dialogue
{
    /// <summary>Where a <c>run_effects</c> action's compiled effects are found in the <see cref="Effects.EffectLibrary"/>.</summary>
    public static class DialogueActionKeys
    {
        /// <summary>The key of action <paramref name="action"/> of choice <paramref name="choice"/> of node <paramref name="node"/> of a dialogue.</summary>
        public static string Of(string dialogueId, string node, int choice, int action) => dialogueId + "#" + node + "#" + choice + "#" + action;
    }

    /// <summary>What the dialogue conditions ask about the talking player. The server adapts its stores to this; tests use a plain object.</summary>
    public interface IDialoguePlayer
    {
        /// <summary>Current base level.</summary>
        int BaseLevel { get; }

        /// <summary>Current job level.</summary>
        int JobLevel { get; }

        /// <summary>Current job id.</summary>
        string JobId { get; }

        /// <summary>How many of the item the bag holds (worn pieces excluded).</summary>
        int ItemCount(string itemId);

        /// <summary>True when the status is active on the player.</summary>
        bool HasStatus(string statusId);
    }

    /// <summary>
    /// Evaluates dialogue conditions (ARCHITECTURE §6.4). The seam for new condition families (quest state, T-602): add a record to
    /// <see cref="Discriminators.DialogueConditions"/>, a term to <c>DialogueConditionTypes</c>, a member to <see cref="IDialoguePlayer"/> and a case here.
    /// A condition type this build does not know fails (hides the choice) rather than passing.
    /// </summary>
    public static class DialogueRules
    {
        /// <summary>True when every condition holds (an empty list always does).</summary>
        public static bool Matches(IReadOnlyList<DialogueConditionDef> conditions, IDialoguePlayer player)
        {
            for (var i = 0; i < conditions.Count; i++)
                if (!Matches(conditions[i], player)) return false;
            return true;
        }

        /// <summary>True when the condition holds.</summary>
        public static bool Matches(DialogueConditionDef condition, IDialoguePlayer player)
        {
            switch (condition)
            {
                case BaseLevelCondition b: return Within(player.BaseLevel, b.Min, b.Max);
                case JobLevelCondition j: return Within(player.JobLevel, j.Min, j.Max);
                case JobIsCondition jobIs: return string.Equals(player.JobId, jobIs.JobId, System.StringComparison.Ordinal);
                case JobIsNotCondition jobIsNot: return !string.Equals(player.JobId, jobIsNot.JobId, System.StringComparison.Ordinal);
                case HasItemCondition item: return player.ItemCount(item.ItemId) >= System.Math.Max(1, item.Quantity);
                case HasStatusCondition status: return player.HasStatus(status.StatusId);
                default: return false;
            }
        }

        private static bool Within(int value, int? min, int? max) => (min is null || value >= min.Value) && (max is null || value <= max.Value);
    }
}
