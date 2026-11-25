using System.Collections.Concurrent;
using Game.Content.Registry;
using Game.Shared.Definitions;
using Game.Shared.Formulas;
using Game.Shared.Items;
using Game.Shared.Stats;

namespace Game.Server.World.Inventory;

/// <summary>
/// The read-only content the inventory reads: items (with their equipment modifiers compiled once), the jobs' usable weapon types, the rules and
/// <c>balance.yaml</c>. Built once at boot and shared by every instance's inventory system; everything here is immutable or thread-safe.
/// </summary>
public sealed class InventoryContent
{
    private readonly IReadOnlyDictionary<string, ItemDef> _items;
    private readonly IReadOnlyDictionary<string, IReadOnlyList<string>> _jobWeapons;
    private readonly ConcurrentDictionary<string, CompiledModifier[]> _modifiers = new(StringComparer.Ordinal);

    /// <summary>
    /// Inventory content from parts (tests use their own items and balance). <paramref name="jobWeaponTypes"/> maps a job id to the weapon types
    /// it may use; a job that is missing, or maps to an empty list, may use any. <paramref name="refine"/> is <c>refine.yaml</c> (none: nothing refines).
    /// </summary>
    public InventoryContent(IReadOnlyDictionary<string, ItemDef> items, BalanceDef balance, IReadOnlyDictionary<string, IReadOnlyList<string>>? jobWeaponTypes = null,
        RefineDef? refine = null)
    {
        _items = items ?? throw new ArgumentNullException(nameof(items));
        Balance = balance ?? throw new ArgumentNullException(nameof(balance));
        _jobWeapons = jobWeaponTypes ?? new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
        var inventory = balance.Inventory;
        Check(inventory.MaxEntries >= 1, "inventory.max_entries must be at least 1.");
        Check(inventory.BurdenedPercent is >= 1 and <= 100, "inventory.burdened_percent must be from 1 to 100.");
        Check(inventory.OverweightPercent >= inventory.BurdenedPercent && inventory.OverweightPercent <= 100,
            "inventory.overweight_percent must be from burdened_percent to 100.");
        Check(inventory.PickupRange >= 0, "inventory.pickup_range must not be negative.");
        Check(inventory.DroppedLifetimeMs > 0, "inventory.dropped_lifetime_ms must be above 0.");
        Rules = new InventoryRules(new ItemLookup(items), inventory, refine);
        foreach (var item in items.Values) Modifiers(item);   // compile now: a bad modifier is a boot error, not a runtime one
    }

    /// <summary>Inventory content of a loaded database.</summary>
    public static InventoryContent From(ContentDatabase content)
    {
        ArgumentNullException.ThrowIfNull(content);
        var items = content.Items.All.ToDictionary(i => i.Id, StringComparer.Ordinal);
        var jobs = content.Jobs.All.ToDictionary(j => j.Id, j => j.WeaponTypes, StringComparer.Ordinal);
        return new InventoryContent(items, content.Balance, jobs, refine: content.Refine);
    }

    /// <summary>The rules (items, slots, weight thresholds, two-handed weapons, the refine table).</summary>
    public InventoryRules Rules { get; }

    /// <summary><c>balance.yaml</c>: <c>inventory</c>, <c>new_character</c>.</summary>
    public BalanceDef Balance { get; }

    /// <summary>Finds an item.</summary>
    public bool TryGetItem(string id, out ItemDef item) => _items.TryGetValue(id, out item!);

    /// <summary>The weapon types <paramref name="jobId"/> may use, or null when it may use any (or the job is unknown).</summary>
    public IReadOnlyList<string>? WeaponTypesOf(string? jobId) =>
        jobId is not null && _jobWeapons.TryGetValue(jobId, out var types) && types.Count > 0 ? types : null;

    /// <summary>An equipment item's or a card's modifiers, compiled once (thread-safe); empty for anything else.</summary>
    public CompiledModifier[] Modifiers(ItemDef item) =>
        _modifiers.GetOrAdd(item.Id, _ => item.Equip is { } equip ? equip.Modifiers.Select(CompiledModifier.Compile).ToArray()
            : item.Card is { } card ? card.Modifiers.Select(CompiledModifier.Compile).ToArray() : []);

    private static void Check(bool ok, string message)
    {
        if (!ok) throw new ArgumentOutOfRangeException(nameof(Balance), message);
    }
}

/// <summary>
/// Where each player character's inventory lives while it is in the world. A saved character's is put here before any system sees it, and copied from here, whole, for a
/// save (T-702, <c>CharacterKeeper</c>). Shared by every instance, so implementations are thread-safe; a character is in one instance at a time, so its state is only touched from that instance's tick thread.
/// </summary>
public interface IInventoryStore
{
    /// <summary>The character's inventory; <paramref name="created"/> is true when <paramref name="create"/> made it now (a new character).</summary>
    InventoryState GetOrCreate(EntityId player, Func<InventoryState> create, out bool created);

    /// <summary>The character's inventory, if it has one.</summary>
    bool TryGet(EntityId player, out InventoryState state);

    /// <summary>Forgets a character that left the world; a saved one has been copied for its last save (T-702).</summary>
    void Release(EntityId player);

    /// <summary>A new entry id (equipment instance ids come from here too). With a database it is unique for ever (T-702, <c>CharacterSaves.NextItemId</c>).</summary>
    long NextEntryId();
}

/// <summary>
/// Inventories of the characters in the world, in memory, keyed by entity id. <paramref name="newId"/> is the id source of a server with a database (T-702); without one the ids
/// are only unique for the server run, and nothing is saved.
/// </summary>
public sealed class InMemoryInventoryStore(Func<long>? newId = null) : IInventoryStore
{
    private readonly ConcurrentDictionary<EntityId, InventoryState> _states = new();
    private long _lastId;

    /// <summary>Characters held.</summary>
    public int Count => _states.Count;

    /// <inheritdoc />
    public InventoryState GetOrCreate(EntityId player, Func<InventoryState> create, out bool created)
    {
        ArgumentNullException.ThrowIfNull(create);
        created = false;
        if (_states.TryGetValue(player, out var state)) return state;
        var fresh = create();
        if (_states.TryAdd(player, fresh))
        {
            created = true;
            return fresh;
        }
        return _states[player];
    }

    /// <inheritdoc />
    public bool TryGet(EntityId player, out InventoryState state) => _states.TryGetValue(player, out state!);

    /// <inheritdoc />
    public void Release(EntityId player) => _states.TryRemove(player, out _);

    /// <inheritdoc />
    public long NextEntryId() => newId is null ? Interlocked.Increment(ref _lastId) : newId();
}
