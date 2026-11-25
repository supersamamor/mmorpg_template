using Game.Server.World.Aoi;
using Game.Server.World.Dialogue;
using Game.Server.World.Inventory;
using Game.Server.World.Quests;
using Game.Shared.Time;

namespace Game.Server.World.Saving;

/// <summary>
/// Decides when a saved character is copied (T-702): on the interval, after it arrived on a map, and in the tick it put something down for others to take. It copies in phase 5, when
/// every system has run and every event is out, and does nothing else: no I/O, no changes. The last copy, when a character leaves, is the <see cref="CharacterKeeper"/>'s.
/// </summary>
/// <remarks>
/// Some things span ticks: an item is used up, and its effects run on the next tick; a dialogue takes its price, and its other actions follow. A copy between the two would split them,
/// so it waits, at most <see cref="CharacterKeeper.MostTicksPutOff"/> ticks. Quest turn-ins and rewards need no waiting (<c>QuestSystem</c>), but are waited for all the same.
/// </remarks>
public sealed class SaveSystem(CharacterKeeper keeper, IClock clock, long intervalMs) : ITickSystem, IOutboundPhase
{
    private MapInstance? _instance;

    /// <inheritdoc />
    public string Name => "saving";

    /// <inheritdoc />
    public IReadOnlyCollection<Type> IntentTypes => [];

    /// <inheritdoc />
    public void Tick(TickContext context)
    {
        if (ReferenceEquals(_instance, context.Instance)) return;
        _instance = context.Instance;
        var events = _instance.Events;
        events.Subscribe<EntityEnteredMap>(e =>
        {
            if (_instance.TryGetEntity(e.Entity, out var entity) && entity.Save is { } ticket) ticket.SaveSoon = true;
        });
        events.Subscribe<ItemUsed>(e => Mark(e.Player));
        events.Subscribe<DialogueItemsRequested>(e => Mark(e.Player));
        events.Subscribe<DialogueItemsResolved>(e => Mark(e.Player));
        events.Subscribe<DialogueEffectsRequested>(e => Mark(e.Player));
        events.Subscribe<QuestStartRequested>(e => Mark(e.Player));
        events.Subscribe<QuestItemsRequested>(e => Mark(e.Player));
        events.Subscribe<QuestItemsResolved>(e => Mark(e.Player));
        events.Subscribe<QuestRewardsRequested>(e => Mark(e.Player));
    }

    /// <inheritdoc />
    public void Outbound(TickContext context)
    {
        var players = context.Instance.Players;
        if (players.Count == 0) return;
        var now = clock.NowMilliseconds;
        for (var i = 0; i < players.Count; i++) keeper.CopyIfDue(players[i], context.TickNumber, now, intervalMs);
    }

    private void Mark(EntityId player)
    {
        if (_instance!.TryGetEntity(player, out var entity)) CharacterKeeper.MarkUnsettled(entity, _instance.TickNumber);
    }
}
