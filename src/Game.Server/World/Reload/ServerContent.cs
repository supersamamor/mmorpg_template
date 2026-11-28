using Game.Content.Registry;
using Game.Server.World.Aoi;
using Game.Server.World.Autopilot;
using Game.Server.World.Combat;
using Game.Server.World.Dialogue;
using Game.Server.World.Inventory;
using Game.Server.World.Monsters;
using Game.Server.World.Quests;
using Game.Shared.Definitions;

namespace Game.Server.World.Reload;

/// <summary>
/// Everything the tick's systems read from content, built once from a validated <see cref="ContentDatabase"/> and shared, read-only, by every
/// instance. Building it runs the same checks the server runs at boot (movement limits, AI timings, AOI numbers, combat and inventory numbers),
/// so a set that fails here is refused like one with a validation error. A hot reload (T-801) builds a new one off the tick threads and hands it
/// to each instance, which swaps it in between two of its ticks (<see cref="MapInstance"/>, <see cref="IContentReloadable"/>).
/// </summary>
public sealed class ServerContent
{
    private ServerContent(ContentDatabase database, long generation)
    {
        Database = database;
        Generation = generation;
        Balance = database.Balance;
        Monsters = MonsterCatalog.From(database);
        MovementSystem.Create(Balance);            // fails now if the movement limits are unusable
        MonsterAiSystem.Create(Monsters, Balance); // likewise for the AI timings
        AoiSystem.Create(Balance);                 // and the AOI numbers
        Combat = CombatContent.From(database, Monsters);   // and the combat, death and loot numbers
        Items = InventoryContent.From(database);            // and the inventory numbers and item modifiers
        Dialogue = DialogueContent.From(database);
        Quests = QuestContent.From(database);
        Shops = ShopContent.From(database);
        Outfits = new Game.Shared.Wardrobe.WardrobeRules(database.Outfits.All);
        Autopilot = AutopilotContent.From(database);
    }

    /// <summary>Builds the systems' content; throws (<see cref="ArgumentException"/>, <see cref="InvalidOperationException"/>, ...) when a number is unusable.</summary>
    public static ServerContent Build(ContentDatabase database, long generation = 0) =>
        new(database ?? throw new ArgumentNullException(nameof(database)), generation);

    /// <summary>The validated content this was built from.</summary>
    public ContentDatabase Database { get; }

    /// <summary>0 for the content the server started with; each hot reload counts up by one.</summary>
    public long Generation { get; }

    /// <summary><c>balance.yaml</c>.</summary>
    public BalanceDef Balance { get; }

    /// <summary>Monsters with their stats pipeline and effects.</summary>
    public MonsterCatalog Monsters { get; }

    /// <summary>Combat, death, loot and progression numbers.</summary>
    public CombatContent Combat { get; }

    /// <summary>Inventory numbers and item rules.</summary>
    public InventoryContent Items { get; }

    /// <summary>NPCs and their dialogue.</summary>
    public DialogueContent Dialogue { get; }

    /// <summary>Quest rules and numbers.</summary>
    public QuestContent Quests { get; }

    /// <summary>Shops with their prices resolved.</summary>
    public ShopContent Shops { get; }

    /// <summary>The outfits there are (T-708).</summary>
    public Game.Shared.Wardrobe.WardrobeRules Outfits { get; }

    /// <summary>Autopilot numbers and journeys.</summary>
    public AutopilotContent Autopilot { get; }
}

/// <summary>
/// A tick system that reads content and can take a new set between two ticks (T-801). <see cref="Reload"/> is called on the instance's tick thread,
/// after the instance has removed its monsters and NPCs and taken its players off the map (their <c>EntityLeftMap</c> has been handled with the old
/// content), and before the players come back (their <c>EntityEnteredMap</c> is handled with the new one). So per-entity state built from the old
/// content is gone by then; the system replaces its content references and anything it derived from them, and keeps what is not content (queues of
/// work in flight, counters).
/// </summary>
public interface IContentReloadable
{
    /// <summary>Takes <paramref name="next"/> from now on.</summary>
    void Reload(ServerContent next);
}
