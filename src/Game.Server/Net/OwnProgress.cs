using Game.Content.Registry;
using Game.Content.Simulation;
using Game.Server.World;
using Game.Server.World.Combat;
using Game.Shared.Definitions;
using Game.Shared.Progression;
using Game.Shared.Protocol;

namespace Game.Server.Net;

/// <summary>
/// Builds a player's own <c>exp</c> message from the progression store combat writes to (levels, EXP, and the next level's requirement from
/// the EXP tables). Call it on the tick thread of the instance that holds the player: the store's state for a character is only touched there.
/// </summary>
public sealed class OwnProgress
{
    private readonly IPlayerProgressionStore _store;
    private readonly IProgressionContent _content;
    private readonly ExpTableDef _baseTable;
    private readonly ContentDatabase _database;
    private readonly ProgressionRules _rules;

    /// <summary>Reads the EXP tables from validated content; <paramref name="store"/> is the one the combat systems use.</summary>
    public OwnProgress(ContentDatabase content, IPlayerProgressionStore store)
    {
        ArgumentNullException.ThrowIfNull(content);
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _database = content;
        _rules = new ProgressionRules(content.Balance, new ContentProgression(content));
        _content = new ContentProgression(content);
        var id = content.Balance.Progression.BaseExpTableId;
        if (!_content.TryGetExpTable(id, out var table) || table is null) throw new ArgumentException($"Base EXP table '{id}' not found.", nameof(content));
        _baseTable = table;
    }

    /// <summary>The player's <c>exp</c> message, or null when combat has not seen it yet (it has no progression).</summary>
    public ExpMessage? For(EntityId player)
    {
        if (!_store.TryGet(player, out var state)) return null;
        ExpTableDef? jobTable = null;
        if (_content.TryGetJob(state.JobId, out var job) && job is not null) _content.TryGetExpTable(job.JobExpTableId, out jobTable);
        return new ExpMessage(state.BaseLevel, state.BaseExp, Next(_baseTable, state.BaseLevel), state.JobLevel, state.JobExp, Next(jobTable, state.JobLevel));
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
        var jobs = _database.Jobs.All.Where(j => _rules.CanChangeJob(state, j.Id).Allowed).Select(j => j.Id).ToList();
        return new SkillsMessage(state.JobId, state.SkillPoints, levels, cooldowns, jobs);
    }

    /// <summary>EXP level <paramref name="level"/> needs to level up (entry <c>level - 1</c>); 0 at the cap or without a table.</summary>
    private static long Next(ExpTableDef? table, int level) => table is not null && level >= 1 && level <= table.Exp.Count ? table.Exp[level - 1] : 0;
}
