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

    /// <summary>
    /// Staff changed the character in this tick (T-806): <paramref name="note"/>, its audit row, lands in the transaction of the first save that has the change (the next copy), never
    /// without it and never twice. Tick thread.
    /// </summary>
    public void Note(StaffNote note)
    {
        ArgumentNullException.ThrowIfNull(note);
        note.Version = TakenVersion + 1;
        SaveSoon = true;
        lock (_notes) _notes.Add(note);
    }

    private readonly List<StaffNote> _notes = new();

    /// <summary>The notes no save has landed yet, for the next copy. Tick thread.</summary>
    internal IReadOnlyList<StaffNote> PendingNotes()
    {
        lock (_notes) return _notes.Count == 0 ? [] : _notes.ToArray();
    }

    /// <summary>Save <paramref name="version"/> landed: its notes are in the database. Any thread.</summary>
    internal void NotesLanded(long version)
    {
        List<StaffNote>? landed = null;
        lock (_notes)
            for (var i = _notes.Count - 1; i >= 0; i--)
                if (_notes[i].Version <= version)
                {
                    (landed ??= new()).Add(_notes[i]);
                    _notes.RemoveAt(i);
                }
        if (landed is not null) foreach (var note in landed) note.Settle(true);
    }

    /// <summary>The stay is over without its notes' saves (lost, or given up): their changes never existed. Any thread.</summary>
    internal void NotesLost()
    {
        StaffNote[] lost;
        lock (_notes)
        {
            lost = _notes.ToArray();
            _notes.Clear();
        }
        foreach (var note in lost) note.Settle(false);
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
/// <param name="Notes">Staff changes in it whose audit rows have not landed (T-806): the store writes those above the stored save, in the same transaction.</param>
public sealed record CharacterSave(CharacterTicket Ticket, long Version, CharacterState State, bool Final, IReadOnlyList<SaveLink> Links, IReadOnlyList<StaffNote>? Notes = null)
{
    /// <summary>The notes (none when null).</summary>
    public IReadOnlyList<StaffNote> StaffNotes => Notes ?? [];

    /// <summary>The character.</summary>
    public Guid CharacterId => Ticket.CharacterId;

    /// <summary>The stay's claim.</summary>
    public long Claim => Ticket.Claim;
}

/// <summary>
/// A staff change to a saved character in the world (T-806): its audit row and the items it made (<c>source = admin</c>), written in the transaction of the save that first has the change, so the
/// change and its row land together or not at all (rule 10). <see cref="Landed"/> says which: true once that save is in the database, false when the stay ended without it (the change is gone
/// with the unsaved time, as after a crash).
/// </summary>
public sealed class StaffNote(Game.Server.Admin.AuditDraft audit, IReadOnlyList<(long Id, string ItemId, int Quantity)>? made = null)
{
    private readonly TaskCompletionSource<bool> _landed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private long _auditId;

    /// <summary>The audit row to write.</summary>
    public Game.Server.Admin.AuditDraft Audit { get; } = audit;

    /// <summary>The entries staff made (for <c>admin_items</c>): each one's id, item and quantity when it was made.</summary>
    public IReadOnlyList<(long Id, string ItemId, int Quantity)> Made { get; } = made ?? [];

    /// <summary>The first save number that has the change.</summary>
    public long Version { get; internal set; }

    /// <summary>The audit row's id once the store wrote it (0 before, and when a retried save found it written already).</summary>
    public long AuditId
    {
        get => Interlocked.Read(ref _auditId);
        set => Interlocked.Exchange(ref _auditId, value);
    }

    /// <summary>True when the change and its row are in the database; false when they never will be.</summary>
    public Task<bool> Landed => _landed.Task;

    internal void Settle(bool landed) => _landed.TrySetResult(landed);
}
