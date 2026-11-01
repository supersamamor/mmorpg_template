using Game.Server.World;

namespace Game.Server.Net;

/// <summary>
/// The gateway's record of one player character it put in the world (ARCHITECTURE §10): which session plays it right now, or, between
/// sessions, when its grace period ends. A character has one link from its login until it leaves the world, and at most one session at a
/// time. Changed only under the gateway's link lock; <see cref="Session"/> is also read without it on tick threads (to route
/// <c>rejected</c>).
/// </summary>
public sealed class CharacterLink(EntityId character, string? displayName, ClientSession session)
{
    private volatile ClientSession? _session = session;

    /// <summary>The character.</summary>
    public EntityId Character { get; } = character;

    /// <summary>The dev display name it logged in with (kept for the sessions that resume it).</summary>
    public string? DisplayName { get; } = displayName;

    /// <summary>The session playing it, or null while it waits in its grace period.</summary>
    public ClientSession? Session
    {
        get => _session;
        internal set => _session = value;
    }

    /// <summary>Gateway clock time at which the grace period ends; meaningful only while <see cref="Session"/> is null.</summary>
    public long GraceEndsMs { get; internal set; }

    /// <summary>True while it waits for a resume.</summary>
    public bool InGrace => _session is null;
}
