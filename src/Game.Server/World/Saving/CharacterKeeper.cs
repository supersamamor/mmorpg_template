using Game.Server.World.Combat;
using Game.Server.World.Dialogue;
using Game.Server.World.Inventory;
using Game.Shared.Progression;
using Game.Shared.Skills;
using Game.Shared.Time;

namespace Game.Server.World.Saving;

/// <summary>
/// The tick thread's half of saving (T-702): puts a loaded character into the stores before any system sees it, and copies a character's whole state out of them, on the tick
/// thread that holds it, between systems, so every copy is consistent. The copies go to <see cref="CharacterSaves"/>, which does the database work elsewhere.
/// </summary>
public sealed class CharacterKeeper(CharacterSaves saves, IPlayerProgressionStore progressions, IInventoryStore inventories, ISavePointStore savePoints, IClock clock, TimeProvider time)
    : IEntityKeeper
{
    /// <summary>Most ticks a copy or a leave is put off for something under way: longer than anything in the game takes, so this only bounds a fault.</summary>
    public const int MostTicksPutOff = 10;

    /// <summary>Where the copies go.</summary>
    public CharacterSaves Saves => saves;

    /// <summary>
    /// The character enters, in its spawn's initializer, with its stat sheet set: a loaded one's state goes into the stores; a new one's (<paramref name="loaded"/> null) is made by the
    /// systems, as for any new character.
    /// </summary>
    public void Enter(Entity entity, LoadedCharacter? loaded)
    {
        var ticket = entity.Save ?? throw new InvalidOperationException($"{entity.Id} is not a saved character.");
        if (loaded is not null)
        {
            var now = clock.NowMilliseconds;
            progressions.GetOrCreate(entity.Id, () => loaded.Progression);
            foreach (var (item, left) in loaded.ItemCooldowns) loaded.Inventory.CooldownUntilMs[item] = now + left;
            inventories.GetOrCreate(entity.Id, () => loaded.Inventory, out _);
            if (loaded.SavePoint is { } point) savePoints.Set(entity.Id, point);
            entity.Cooldowns = new SkillCooldowns();
            entity.Cooldowns.Load(loaded.SkillCooldowns);
            if (loaded.AutopilotDayAgeMs is { } age) entity.AutopilotUsage = Autopilot.AutopilotUsage.Restore(loaded.AutopilotUsedMs, age, now);
            if (entity.Stats is { } stats) entity.Vitals = Vitals.Restore(stats, loaded.Hp, loaded.Sp, loaded.HpPercent);
            ticket.AsideItems = loaded.AsideItems;
            ticket.Aside = loaded.Aside;
        }
        ticket.SaveSoon = true;   // a new character's start items, or what loading changed, land soon
        ticket.Entered = true;
    }

    /// <summary>Something of the character spans ticks and is under way (see <see cref="SaveSystem"/>): copies wait for it, so no crash splits what it paid from what it bought.</summary>
    internal static void MarkUnsettled(Entity entity, long tick)
    {
        if (entity.Save is not { } ticket) return;
        ticket.UnsettledTick = tick;
        ticket.UnsettledOn = entity.MapId;
    }

    /// <summary>Copies and submits the character if a copy is due. Phase 5: every system has run and every event of the tick is out.</summary>
    internal void CopyIfDue(Entity entity, long tick, long nowMs, long intervalMs)
    {
        if (entity.Save is not { Entered: true, IsLost: false } ticket) return;
        if (!ticket.SaveSoon && nowMs < ticket.NextCopyMs) return;
        if (Unsettled(ticket, entity, tick, 1) && ticket.PutOff++ < MostTicksPutOff) return;
        if (Copy(entity, ticket) is not { } state) return;
        ticket.SaveSoon = false;
        ticket.PutOff = 0;
        // The first interval is a different one for each character, so a thousand of them do not all come due together.
        ticket.NextCopyMs = nowMs + (ticket.NextCopyMs == 0 ? intervalMs / 2 + (ticket.CharacterId.GetHashCode() & int.MaxValue) % Math.Max(1, intervalMs / 2) : intervalMs);
        Submit(ticket, state, final: false);
    }

    /// <inheritdoc />
    public bool MayLeave(Entity entity, long tick)
    {
        if (entity.Save is not { Entered: true, IsLost: false } ticket) return true;
        if (ticket.LeaveAskedTick < 0) ticket.LeaveAskedTick = tick;
        // Always one tick later than asked: what the last tick's intents began is only known once its events are out.
        var waited = tick - ticket.LeaveAskedTick;
        return waited >= MostTicksPutOff || (waited >= 1 && !Unsettled(ticket, entity, tick, 2));
    }

    /// <inheritdoc />
    public void Leaving(Entity entity)
    {
        if (entity.Save is not { } ticket) return;
        if (ticket is { Entered: true, IsLost: false } && Copy(entity, ticket) is { } state) Submit(ticket, state, final: true);
        else saves.GiveUp(ticket);   // nothing of it was ever in the world
    }

    /// <inheritdoc />
    public void Dropped(Entity entity, MapChangeReason reason)
    {
        Leaving(entity);
        // No instance ever says that it left, so nobody else forgets it.
        progressions.Release(entity.Id);
        inventories.Release(entity.Id);
        savePoints.Release(entity.Id);
    }

    /// <summary>
    /// Something is under way: an event of a tick is acted on in the next one, which may raise another. At the end of a tick (phase 5) only this tick's events count (1); in its
    /// phase 3 the last one's do too (2), because what came of them is not known yet.
    /// </summary>
    private static bool Unsettled(CharacterTicket ticket, Entity entity, long tick, int ticks) =>
        ticket.UnsettledTick >= 0 && tick - ticket.UnsettledTick < ticks && string.Equals(ticket.UnsettledOn, entity.MapId, StringComparison.Ordinal);

    private void Submit(CharacterTicket ticket, CharacterState state, bool final)
    {
        var links = new List<SaveLink>(ticket.Links.Count);
        List<CharacterTicket>? landed = null;
        foreach (var (other, version) in ticket.Links)
        {
            var link = new SaveLink(other, version);
            if (link.HasLanded) (landed ??= new()).Add(other);
            else links.Add(link);
        }
        if (landed is not null) foreach (var other in landed) ticket.Links.Remove(other);
        saves.Submit(new CharacterSave(ticket, ++ticket.TakenVersion, state, final, links, ticket.PendingNotes()));   // T-806: staff changes it has, with their audit rows
    }

    /// <summary>A copy of everything that is saved (see ARCHITECTURE §3 for what is not, and why), or null when the systems have not made the character yet.</summary>
    internal CharacterState? Copy(Entity entity, CharacterTicket ticket)
    {
        if (!progressions.TryGet(entity.Id, out var progression) || !inventories.TryGet(entity.Id, out var bag)) return null;
        var nowMs = clock.NowMilliseconds;
        var utc = time.GetUtcNow().UtcDateTime;
        var dayAge = entity.AutopilotUsage?.DayAgeMs(nowMs);
        Dictionary<string, long>? itemCooldowns = null;
        foreach (var (item, until) in bag.CooldownUntilMs)
            if (until > nowMs) (itemCooldowns ??= new(StringComparer.Ordinal))[item] = until - nowMs;
        Dictionary<string, long>? skillCooldowns = null;
        if (entity.Cooldowns is { Count: > 0 } cooldowns)
            foreach (var (skill, left) in cooldowns.Running()) (skillCooldowns ??= new(StringComparer.Ordinal))[skill] = left;
        return new CharacterState
        {
            TakenAt = utc,
            MapId = entity.MapId,
            Cell = entity.Cell,
            Facing = entity.Facing,
            Hp = entity.Dead ? 0 : Math.Max(1, entity.Vitals?.Hp ?? 1),
            Sp = entity.Vitals?.Sp ?? 0,
            SavePoint = savePoints.TryGet(entity.Id, out var point) ? point : null,
            BaseLevel = progression.BaseLevel,
            BaseExp = progression.BaseExp,
            JobId = progression.JobId,
            JobLevel = progression.JobLevel,
            JobExp = progression.JobExp,
            StatPoints = progression.StatPoints,
            SkillPoints = progression.SkillPoints,
            Stats = new Dictionary<string, int>(progression.Stats, StringComparer.Ordinal),
            Skills = new Dictionary<string, int>(progression.Skills, StringComparer.Ordinal),
            ActiveQuests = progression.ActiveQuests.Count == 0 ? [] : progression.ActiveQuests.Values.Select(q => new SavedQuest(q.QuestId, q.Step, (int[])q.Counters.Clone(), q.Token)).ToArray(),
            CompletedQuests = progression.CompletedQuests.Count == 0 ? [] : progression.CompletedQuests.ToArray(),
            UnlockedJobs = progression.UnlockedJobs.Count == 0 ? [] : progression.UnlockedJobs.ToArray(),
            TurnInSequence = progression.TurnInSequence,
            OwedRewards = progression.OwedRewards.Count == 0 ? [] : progression.OwedRewards.ToArray(),
            Outfits = progression.Wardrobe.Owned.Count == 0 ? [] : progression.Wardrobe.Owned.Order(StringComparer.Ordinal).ToArray(),
            OutfitSlots = progression.Wardrobe.Chosen.Count == 0 ? NoTexts : new Dictionary<string, string>(progression.Wardrobe.Chosen, StringComparer.Ordinal),
            OutfitsHidden = progression.Wardrobe.Hidden,
            Items = bag.Entries.Count == 0 ? [] : bag.Entries.ToArray(),   // the entries themselves are immutable
            Currency = bag.Currency,
            SettledTurnIns = bag.SettledTurnIns.Count == 0 ? [] : bag.SettledTurnIns.ToArray(),
            SkillCooldowns = (IReadOnlyDictionary<string, long>?)skillCooldowns ?? NoTimes,
            ItemCooldowns = (IReadOnlyDictionary<string, long>?)itemCooldowns ?? NoTimes,
            AutopilotUsedMs = dayAge is null ? 0 : entity.AutopilotUsage!.UsedMs,
            AutopilotDayStartedAt = dayAge is { } age ? utc.AddMilliseconds(-age) : null,
            AsideItems = ticket.AsideItems,
            Aside = ticket.Aside,
        };
    }

    private static readonly IReadOnlyDictionary<string, long> NoTimes = new Dictionary<string, long>();
    private static readonly IReadOnlyDictionary<string, string> NoTexts = new Dictionary<string, string>();
}
