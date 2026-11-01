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

    /// <summary>Reads the EXP tables from validated content; <paramref name="store"/> is the one the combat systems use.</summary>
    public OwnProgress(ContentDatabase content, IPlayerProgressionStore store)
    {
        ArgumentNullException.ThrowIfNull(content);
        _store = store ?? throw new ArgumentNullException(nameof(store));
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

    /// <summary>EXP level <paramref name="level"/> needs to level up (entry <c>level - 1</c>); 0 at the cap or without a table.</summary>
    private static long Next(ExpTableDef? table, int level) => table is not null && level >= 1 && level <= table.Exp.Count ? table.Exp[level - 1] : 0;
}
