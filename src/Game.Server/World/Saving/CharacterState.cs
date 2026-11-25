using Game.Server.World.Dialogue;
using Game.Shared.Items;
using Game.Shared.Progression;
using Game.Shared.World;

namespace Game.Server.World.Saving;

/// <summary>One active quest as it is saved.</summary>
public sealed record SavedQuest(string QuestId, int Step, IReadOnlyList<int> Counters, long Token);

/// <summary>An item the current content has no place for (ARCHITECTURE §3 "Loading"): kept with the character, not in its bag, until content has it again.</summary>
public sealed record SetAsideItem(InventoryEntry Entry, string Reason);

/// <summary>
/// Anything else the current content has no place for: a <see cref="SetAsideKinds"/> term, the content id, what is needed to put it back (or to look it up: levels, a step), and why.
/// </summary>
public sealed record SetAside(string Kind, string Id, IReadOnlyList<long> Data, string Reason);

/// <summary>What <see cref="SetAside.Kind"/> may be.</summary>
public static class SetAsideKinds
{
    /// <summary>An active quest: step, turn-in number, counters. Comes back with the quest.</summary>
    public const string ActiveQuest = "active_quest";

    /// <summary>A completed quest. Comes back with the quest.</summary>
    public const string CompletedQuest = "completed_quest";

    /// <summary>An unlocked job. Comes back with the job.</summary>
    public const string UnlockedJob = "unlocked_job";

    /// <summary>The job the character had: job level, job EXP. A record for support; the character goes on as the start job.</summary>
    public const string Job = "job";

    /// <summary>A learned skill, whose points were given back: its level. A record for support.</summary>
    public const string Skill = "skill";
}

/// <summary>
/// Everything about a character that is saved (T-702, ARCHITECTURE §3): ids and numbers, never content. Immutable: the tick thread makes one as a consistent copy, and any
/// thread may read it. One per save; <see cref="TakenAt"/> dates the times left.
/// </summary>
public sealed record CharacterState
{
    /// <summary>When the copy was made (UTC).</summary>
    public required DateTime TakenAt { get; init; }

    // ---- where it is ----

    /// <summary>The map.</summary>
    public required string MapId { get; init; }

    /// <summary>The cell.</summary>
    public required GridCell Cell { get; init; }

    /// <summary>The facing.</summary>
    public required string Facing { get; init; }

    /// <summary>Current HP; 0 for a dead character, which comes back as one that has respawned.</summary>
    public required int Hp { get; init; }

    /// <summary>Current SP.</summary>
    public required int Sp { get; init; }

    /// <summary>Its save point, if it has set one.</summary>
    public SavePoint? SavePoint { get; init; }

    // ---- progression ----

    /// <summary>Base level.</summary>
    public required int BaseLevel { get; init; }

    /// <summary>EXP toward the next base level.</summary>
    public long BaseExp { get; init; }

    /// <summary>Job id.</summary>
    public required string JobId { get; init; }

    /// <summary>Job level.</summary>
    public required int JobLevel { get; init; }

    /// <summary>EXP toward the next job level.</summary>
    public long JobExp { get; init; }

    /// <summary>Unspent stat points.</summary>
    public int StatPoints { get; init; }

    /// <summary>Unspent skill points.</summary>
    public int SkillPoints { get; init; }

    /// <summary>Primary stat → base value.</summary>
    public IReadOnlyDictionary<string, int> Stats { get; init; } = Empty<int>();

    /// <summary>Skill → learned level.</summary>
    public IReadOnlyDictionary<string, int> Skills { get; init; } = Empty<int>();

    /// <summary>Active quests.</summary>
    public IReadOnlyList<SavedQuest> ActiveQuests { get; init; } = [];

    /// <summary>Completed quests.</summary>
    public IReadOnlyList<string> CompletedQuests { get; init; } = [];

    /// <summary>Unlocked jobs.</summary>
    public IReadOnlyList<string> UnlockedJobs { get; init; } = [];

    /// <summary>The last quest turn-in number given out.</summary>
    public long TurnInSequence { get; init; }

    /// <summary>Quest rewards not paid yet.</summary>
    public IReadOnlyList<OwedQuestRewards> OwedRewards { get; init; } = [];

    // ---- how it looks ----

    /// <summary>Owned outfits (T-708), in id order, each once; ids the content has lost included.</summary>
    public IReadOnlyList<string> Outfits { get; init; } = [];

    /// <summary>Outfit slot → the outfit chosen for it (T-708).</summary>
    public IReadOnlyDictionary<string, string> OutfitSlots { get; init; } = Empty<string>();

    /// <summary>Every outfit is hidden (T-708).</summary>
    public bool OutfitsHidden { get; init; }

    // ---- what it owns ----

    /// <summary>Its items, worn ones included, in id order.</summary>
    public IReadOnlyList<InventoryEntry> Items { get; init; } = [];

    /// <summary>Its currency.</summary>
    public long Currency { get; init; }

    /// <summary>Quest turn-ins the bag has paid.</summary>
    public IReadOnlyList<long> SettledTurnIns { get; init; } = [];

    // ---- times ----

    /// <summary>Skill → milliseconds left on its cooldown.</summary>
    public IReadOnlyDictionary<string, long> SkillCooldowns { get; init; } = Empty<long>();

    /// <summary>Item → milliseconds left on its cooldown.</summary>
    public IReadOnlyDictionary<string, long> ItemCooldowns { get; init; } = Empty<long>();

    /// <summary>Autopilot time used in the day that began at <see cref="AutopilotDayStartedAt"/> (ADR-002 row 293).</summary>
    public long AutopilotUsedMs { get; init; }

    /// <summary>When the autopilot's day began (UTC), or null before any run.</summary>
    public DateTime? AutopilotDayStartedAt { get; init; }

    // ---- set aside ----

    /// <summary>Items the content had no place for when it was loaded.</summary>
    public IReadOnlyList<SetAsideItem> AsideItems { get; init; } = [];

    /// <summary>Anything else the content had no place for.</summary>
    public IReadOnlyList<SetAside> Aside { get; init; } = [];

    private static IReadOnlyDictionary<string, T> Empty<T>() => new Dictionary<string, T>();
}
