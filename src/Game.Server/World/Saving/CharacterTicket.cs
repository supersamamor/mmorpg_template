namespace Game.Server.World.Saving;

/// <summary>
/// A moment in a character's saves: its save number <paramref name="Version"/> or any later one. Whoever holds something this character gave away may only be saved when that save
/// has landed, or in the same transaction (T-702).
/// </summary>
public sealed record SaveLink(CharacterTicket Ticket, long Version)
{
    /// <summary>The save is in the database.</summary>
    public bool HasLanded => Ticket.LandedVersion >= Version;

    /// <summary>The save will never be in the database: the server lost the character first. What it gave away after its last landed save does not exist.</summary>
    public bool IsLost => Ticket.IsLost && !HasLanded;
}

/// <summary>
/// One stay of a saved character on this server (T-702): from before it is loaded until its last save has landed. The fields of the "tick thread" section belong to the tick thread
/// of the instance that holds the character (it is in one at a time); the rest are <see cref="CharacterSaves"/>'s, under its lock.
/// </summary>
/// <remarks>
/// <b>For whatever moves items between characters</b> (picking up, and T-903's trade, T-905's storage): in the tick that takes the item away, the giver's <see cref="PutDown"/> returns
/// a link; in the tick that gives it, the receiver's <see cref="DependOn"/> takes it. That is the whole contract: the saves then land giver first, or together. A trade calls both on both.
/// </remarks>
public sealed class CharacterTicket
{
    internal CharacterTicket(Guid characterId) => CharacterId = characterId;

    /// <summary>The character.</summary>
    public Guid CharacterId { get; }

    // ---- the tick thread's ----

    /// <summary>The number of the last copy made; the loaded one's to begin with.</summary>
    internal long TakenVersion;

    /// <summary>Its state is in the stores: before that there is nothing to copy.</summary>
    internal bool Entered;

    /// <summary>A copy is due now, not at the next interval.</summary>
    internal bool SaveSoon;

    /// <summary>When the next copy is due (server clock).</summary>
    internal long NextCopyMs;

    /// <summary>The tick something that spans ticks was last seen under way (an item used, a payment asked), on <see cref="UnsettledOn"/>.</summary>
    internal long UnsettledTick = -1;

    /// <summary>The map whose ticks <see cref="UnsettledTick"/> counts.</summary>
    internal string? UnsettledOn;

    /// <summary>Copies put off since the last one because something was under way.</summary>
    internal int PutOff;

    /// <summary>The tick a removal was first asked for; -1 before.</summary>
    internal long LeaveAskedTick = -1;

    /// <summary>What was set aside at loading: part of every copy.</summary>
    internal IReadOnlyList<SetAsideItem> AsideItems = [];

    /// <summary>What was set aside at loading: part of every copy.</summary>
    internal IReadOnlyList<SetAside> Aside = [];

    /// <summary>Others' saves this character's next ones depend on.</summary>
    internal readonly Dictionary<CharacterTicket, long> Links = new();

    /// <summary>This character gave something away in this tick: the link to its first copy without it, which is taken as soon as possible.</summary>
    public SaveLink PutDown()
    {
        SaveSoon = true;
        return new SaveLink(this, TakenVersion + 1);
    }

    /// <summary>This character got something another gave away: its saves wait for <paramref name="link"/>, or take it along.</summary>
    public void DependOn(SaveLink link)
    {
        ArgumentNullException.ThrowIfNull(link);
        if (ReferenceEquals(link.Ticket, this) || link.HasLanded) return;
        Links[link.Ticket] = Links.TryGetValue(link.Ticket, out var version) ? Math.Max(version, link.Version) : link.Version;
    }

    // ---- shared ----

    private long _landed;
    private volatile bool _lost;

    /// <summary>The number of the newest save known to be in the database. Any thread.</summary>
    public long LandedVersion
    {
        get => Interlocked.Read(ref _landed);
        internal set => Interlocked.Exchange(ref _landed, value);
    }

    /// <summary>The server has lost the character: nothing of it is saved any more. Any thread.</summary>
    public bool IsLost
    {
        get => _lost;
        internal set => _lost = value;
    }

    // ---- CharacterSaves', under its lock ----

    /// <summary>Which stay this is, over all servers: only the newest may save.</summary>
    internal long Claim;

    /// <summary>The newest copy that has not landed.</summary>
    internal CharacterSave? Pending;

    /// <summary>The copy being written.</summary>
    internal CharacterSave? InFlight;

    /// <summary>Not before this time (server clock): spacing, or backoff.</summary>
    internal long NotBeforeMs;

    /// <summary>Failed attempts since the last success.</summary>
    internal int Failures;

    /// <summary>Since when something of it waits to land (server clock); 0 when nothing does.</summary>
    internal long UnsavedSinceMs;

    /// <summary>The operator has been told that it is at risk.</summary>
    internal bool AtRisk;

    /// <summary>Completes when the stay is over: its last save has landed, or it was lost or given up.</summary>
    internal readonly TaskCompletionSource Gone = new(TaskCreationOptions.RunContinuationsAsynchronously);
}

/// <summary>One copy to be saved: everything the store needs, and what it depends on. Immutable.</summary>
/// <param name="Ticket">The stay it belongs to.</param>
/// <param name="Version">Its number: higher than any copy before it.</param>
/// <param name="State">The state.</param>
/// <param name="Final">The character has left: the server gives it up when this has landed.</param>
/// <param name="Links">Others' saves it must not land before.</param>
public sealed record CharacterSave(CharacterTicket Ticket, long Version, CharacterState State, bool Final, IReadOnlyList<SaveLink> Links)
{
    /// <summary>The character.</summary>
    public Guid CharacterId => Ticket.CharacterId;

    /// <summary>The stay's claim.</summary>
    public long Claim => Ticket.Claim;
}
