namespace Game.Server.World.Saving;

/// <summary>How a claim ended.</summary>
public enum ClaimOutcome
{
    /// <summary>This server holds the character now; anything another stay still tries to save is refused.</summary>
    Claimed,

    /// <summary>There is no such character, or it is deleted.</summary>
    NotFound,

    /// <summary>Another server that is alive holds it.</summary>
    HeldElsewhere,
}

/// <summary>A claim: its number, the last save's number, and the state (null: the character has never been saved).</summary>
public sealed record CharacterClaim(ClaimOutcome Outcome, long Claim = 0, long Version = 0, CharacterState? State = null);

/// <summary>How saving a group ended. Anything but <see cref="Saved"/> means nothing of it was written.</summary>
public abstract record SaveResult
{
    /// <summary>Every save of the group is in the database (some may have been already).</summary>
    public sealed record Saved : SaveResult;

    /// <summary>The server no longer holds <paramref name="Characters"/>: claimed by another stay, or deleted.</summary>
    public sealed record Lost(IReadOnlyCollection<Guid> Characters) : SaveResult;

    /// <summary>Items of the group belong to other characters in the database: somebody duplicated them, or the links were not kept.</summary>
    public sealed record Duplicated(IReadOnlyCollection<long> Items) : SaveResult;
}

/// <summary>
/// Saved characters (T-702). Every method is one transaction, off any tick thread, and throws <see cref="Accounts.StoreUnavailableException"/> when the database does not answer;
/// after that it is unknown whether the transaction happened, and asking again is safe.
/// </summary>
public interface ICharacterStore
{
    /// <summary>Registers a server run: a number no other run, past or future, ever gets.</summary>
    Task<long> StartRunAsync(CancellationToken cancellation);

    /// <summary>The run is alive.</summary>
    Task BeatAsync(long run, CancellationToken cancellation);

    /// <summary>The run is over: whatever it still holds is free.</summary>
    Task EndRunAsync(long run, CancellationToken cancellation);

    /// <summary>
    /// Takes the character for <paramref name="run"/> and reads it: from here on, only this claim may save it. Refused while another live run holds it, and when it does not exist or is
    /// deleted; a character that is held cannot be deleted.
    /// </summary>
    Task<CharacterClaim> ClaimAsync(Guid character, long run, CancellationToken cancellation);

    /// <summary>Gives a character up without saving it, if <paramref name="run"/> holds it with <paramref name="claim"/> (0: whichever claim of the run; its number never arrived).</summary>
    Task ReleaseAsync(Guid character, long run, long claim, CancellationToken cancellation);

    /// <summary>
    /// Saves every one of <paramref name="group"/>, or nothing. A save whose number is not above the stored one is already there and changes nothing; one whose claim is not the
    /// stored one makes the whole group fail.
    /// </summary>
    Task<SaveResult> SaveAsync(IReadOnlyList<CharacterSave> group, CancellationToken cancellation);
}
