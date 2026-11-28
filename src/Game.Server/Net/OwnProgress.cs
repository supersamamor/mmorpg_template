using Game.Content.Registry;
using Game.Content.Simulation;
using Game.Server.World;
using Game.Server.World.Combat;
using Game.Shared.Definitions;
using Game.Shared.Progression;
using Game.Shared.Protocol;
using Game.Shared.Quests;
using Game.Shared.Vocabulary;

namespace Game.Server.Net;

/// <summary>
/// Builds a player's own <c>exp</c> message from the progression store combat writes to (levels, EXP, and the next level's requirement from
/// the EXP tables). Call it on the tick thread of the instance that holds the player: the store's state for a character is only touched there.
/// </summary>
public sealed class OwnProgress
{
    private readonly IPlayerProgressionStore _store;
    private readonly Func<ContentDatabase> _live;   // T-801: the content in effect (a hot reload swaps it)
    private volatile Snapshot _snapshot;
    private readonly SortedSet<string> _offer = new(StringComparer.Ordinal);
    private readonly SortedSet<string> _turnIn = new(StringComparer.Ordinal);

    /// <summary>Reads the EXP tables from validated content; <paramref name="store"/> is the one the combat systems use.</summary>
    public OwnProgress(ContentDatabase content, IPlayerProgressionStore store) : this(() => content, store)
    {
        ArgumentNullException.ThrowIfNull(content);
    }

    /// <summary>
    /// T-801: reads the content in effect through <paramref name="live"/>. Each message is built from one snapshot of it (the rules derived from one content set),
    /// so an answer never mixes an old set with a new one.
    /// </summary>
    public OwnProgress(Func<ContentDatabase> live, IPlayerProgressionStore store)
    {
        _live = live ?? throw new ArgumentNullException(nameof(live));
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _snapshot = new Snapshot(live());
    }

    /// <summary>The rules of the content in effect, built again when it changed.</summary>
    private Snapshot Now()
    {
        var snapshot = _snapshot;
        var content = _live();
        if (ReferenceEquals(content, snapshot.Database)) return snapshot;
        return _snapshot = new Snapshot(content);
    }

    /// <summary>Everything a message reads, derived from one content set.</summary>
    private sealed class Snapshot
    {
        public Snapshot(ContentDatabase content)
        {
            ArgumentNullException.ThrowIfNull(content);
            Database = content;
            Rules = new ProgressionRules(content.Balance, new ContentProgression(content));
            Content = new ContentProgression(content);
            Quests = new QuestRules(content.Quests.All, content.Balance.Quests.MaxActive);
            Wardrobe = new Game.Shared.Wardrobe.WardrobeRules(content.Outfits.All);
            var id = content.Balance.Progression.BaseExpTableId;
            if (!Content.TryGetExpTable(id, out var table) || table is null) throw new ArgumentException($"Base EXP table '{id}' not found.", nameof(content));
            BaseTable = table;
        }

        public ContentDatabase Database { get; }

        public ProgressionRules Rules { get; }

        public IProgressionContent Content { get; }

        public QuestRules Quests { get; }

        public Game.Shared.Wardrobe.WardrobeRules Wardrobe { get; }

        public ExpTableDef BaseTable { get; }
    }

    /// <summary>The player's <c>exp</c> message, or null when combat has not seen it yet (it has no progression).</summary>
    public ExpMessage? For(EntityId player)
    {
        if (!_store.TryGet(player, out var state)) return null;
        var now = Now();
        ExpTableDef? jobTable = null;
        if (now.Content.TryGetJob(state.JobId, out var job) && job is not null) now.Content.TryGetExpTable(job.JobExpTableId, out jobTable);
        return new ExpMessage(state.BaseLevel, state.BaseExp, Next(now.BaseTable, state.BaseLevel), state.JobLevel, state.JobExp, Next(jobTable, state.JobLevel));
    }

    /// <summary>
    /// The player's <c>skills</c> message (T-502): job, unspent points, learned levels, cooldowns left, and the jobs it can change to right now. Null
    /// without a progression. Cooldown times come from the entity (<see cref="Entity.Cooldowns"/>), which only its instance's tick thread touches.
    /// </summary>
    public SkillsMessage? SkillsFor(Entity player)
    {
        if (!_store.TryGet(player.Id, out var state)) return null;
        var levels = state.Skills.Where(p => p.Value > 0).OrderBy(p => p.Key, StringComparer.Ordinal).Select(p => new SkillLevelRecord(p.Key, p.Value)).ToList();
        var cooldowns = (player.Cooldowns?.Running() ?? [])
            .Select(c => new SkillCooldownRecord(c.Key, (int)Math.Min(c.Value, int.MaxValue))).ToList();
        var now = Now();
        var jobs = now.Database.Jobs.All.Where(j => now.Rules.CanChangeJob(state, j.Id).Allowed).Select(j => j.Id).ToList();
        return new SkillsMessage(state.JobId, state.SkillPoints, levels, cooldowns, jobs);
    }

    /// <summary>
    /// The player's <c>stats</c> message (T-503): unspent stat points, each primary stat with the cost of its next +1 (0 at the maximum), and the panel's
    /// derived stats from the entity's sheet, floored. Null without a progression or a sheet.
    /// </summary>
    public StatsMessage? StatsFor(Entity player)
    {
        if (!_store.TryGet(player.Id, out var state) || player.Stats is not { } sheet) return null;
        var now = Now();
        var max = now.Database.Balance.Progression.MaxStatValue;
        var stats = PrimaryStats.All.Ordered
            .Select(s =>
            {
                var value = state.StatValue(s);
                return new StatRecord(s, value, value >= max ? 0 : now.Rules.StatRaiseCost(value));
            }).ToList();
        var derived = DerivedStats.Panel.Ordered.Select(d => new DerivedRecord(d, sheet.GetInt(d))).ToList();
        return new StatsMessage(state.StatPoints, stats, derived);
    }

    /// <summary>
    /// The player's <c>quest_log</c> (T-602): every active quest with its step, objectives and held reason, the completed quests, and the NPC markers, all from the
    /// progression store the quest systems write to. Null without a progression. Call it on the tick thread of the player's instance (it reuses buffers).
    /// </summary>
    public QuestLogMessage? QuestsFor(EntityId player)
    {
        if (!_store.TryGet(player, out var state)) return null;
        var quests = Now().Quests;
        var active = state.ActiveQuests.Values.OrderBy(p => p.QuestId, StringComparer.Ordinal).Where(p => quests.TryGet(p.QuestId, out _)).Select(quests.Entry).ToList();
        var completed = state.CompletedQuests.OrderBy(q => q, StringComparer.Ordinal).ToList();
        lock (_offer)
        {
            quests.Markers(state, _offer, _turnIn);
            return new QuestLogMessage(active, completed, _offer.ToList(), _turnIn.ToList());
        }
    }

    /// <summary>The player's <c>wardrobe</c> (T-708): the outfits it owns that the content has, and what is chosen. Null without a progression.</summary>
    public WardrobeMessage? WardrobeFor(Entity player)
    {
        if (!_store.TryGet(player.Id, out var state)) return null;
        var wardrobe = Now().Wardrobe;
        return new WardrobeMessage(wardrobe.Owned(state.Wardrobe), wardrobe.Chosen(state.Wardrobe, player.Appearance?.Gender), state.Wardrobe.Hidden);
    }

    /// <summary>EXP level <paramref name="level"/> needs to level up (entry <c>level - 1</c>); 0 at the cap or without a table.</summary>
    private static long Next(ExpTableDef? table, int level) => table is not null && level >= 1 && level <= table.Exp.Count ? table.Exp[level - 1] : 0;
}
