using System;
using System.Collections.Generic;
using Game.Shared.World;

namespace Game.Shared.Protocol
{
    // Message contracts shared by the server gateway and the Godot client (ARCHITECTURE §10 has the JSON shape of each). Plain data, no
    // I/O and no serializer attributes: the JSON mapping is written by hand (Game.Server/Net/ProtocolJson), so neither side needs
    // runtime reflection. Display data only (CLAUDE.md rule 11): no stats, AI, drops or paths beyond the next cell.

    /// <summary>A message a client sends. <see cref="Type"/> is its <see cref="MessageTypes"/> name.</summary>
    public abstract record ClientMessage
    {
        /// <summary>The <c>type</c> field.</summary>
        public abstract string Type { get; }
    }

    /// <summary>
    /// <c>hello</c>: the first message of a session. It carries exactly one of <see cref="Name"/> (a new login) and <see cref="Resume"/> (take
    /// back a character after a drop, ARCHITECTURE §10). <c>{"type":"hello","version":1,"name":"Ada","content":"ab12..."}</c> or
    /// <c>{"type":"hello","version":1,"resume":"Qk3x...","content":"ab12..."}</c>.
    /// </summary>
    /// <param name="Version">The client's <see cref="ProtocolVersion"/>.</param>
    /// <param name="Name">Display name for the dev login stub (no password), or null when resuming. Real accounts replace this (T-701).</param>
    /// <param name="Content">The content hash of the client's bundle (ARCHITECTURE §5.4), or null when the client has none. A hash that differs from the server's ends in <c>bye</c> <c>update_required</c>; a missing one is allowed only in development.</param>
    /// <param name="Resume">The resume token from the last <see cref="WelcomeMessage"/>, or null for a new login. Single use: a successful resume
    /// returns a new one; an unknown, used or expired token ends in <c>bye</c> <c>resume_failed</c>.</param>
    public sealed record HelloMessage(int Version, string? Name, string? Content = null, string? Resume = null) : ClientMessage
    {
        /// <inheritdoc />
        public override string Type => MessageTypes.Hello;
    }

    /// <summary><c>move</c>: walk to <see cref="Cell"/>. A request: the server may still refuse it (<see cref="RejectedMessage"/>). <c>{"type":"move","cell":[12,9]}</c>.</summary>
    /// <param name="Cell">Target cell on the current map.</param>
    public sealed record MoveMessage(GridCell Cell) : ClientMessage
    {
        /// <inheritdoc />
        public override string Type => MessageTypes.Move;
    }

    /// <summary><c>stop</c>: stop walking at the next cell. <c>{"type":"stop"}</c>.</summary>
    public sealed record StopMessage : ClientMessage
    {
        /// <inheritdoc />
        public override string Type => MessageTypes.Stop;
    }

    /// <summary>
    /// <c>logout</c>: leave the game on purpose. The server answers <c>bye</c> <c>logout</c>, closes, and removes the character at once, with no
    /// grace period; its resume token stops working. <c>{"type":"logout"}</c>.
    /// </summary>
    public sealed record LogoutMessage : ClientMessage
    {
        /// <inheritdoc />
        public override string Type => MessageTypes.Logout;
    }

    /// <summary><c>ping</c>: keep-alive; the server answers with <see cref="PongMessage"/> carrying the same number. <c>{"type":"ping","seq":7}</c>.</summary>
    /// <param name="Seq">Any number the client picks (a counter or its clock).</param>
    public sealed record PingMessage(long Seq) : ClientMessage
    {
        /// <inheritdoc />
        public override string Type => MessageTypes.Ping;
    }

    /// <summary>A message the server sends. <see cref="Type"/> is its <see cref="MessageTypes"/> name.</summary>
    public abstract record ServerMessage
    {
        /// <summary>The <c>type</c> field.</summary>
        public abstract string Type { get; }
    }

    /// <summary>
    /// <c>welcome</c>: the login worked and the character is in the world. A <see cref="SnapshotMessage"/> follows.
    /// <c>{"type":"welcome","version":1,"id":42,"map":"map.x","tick_rate":10,"content":"ab12...","token":"Qk3x..."}</c>.
    /// </summary>
    /// <param name="Version">The server's <see cref="ProtocolVersion"/>.</param>
    /// <param name="Id">The client's own entity id (the <c>id</c> of its record in snapshots and deltas).</param>
    /// <param name="MapId">The map the character is on.</param>
    /// <param name="TickRate">Server ticks per second; snapshots and deltas carry tick numbers at this rate.</param>
    /// <param name="Content">The server's content hash (the client bundle's <c>content_hash</c>), or null on a server that has none.</param>
    /// <param name="Token">The resume token for this character (ARCHITECTURE §10): keep it in memory only and send it in <c>hello</c> <c>resume</c>
    /// to take the character back after a drop. Every welcome carries a new one; the one used to resume no longer works.</param>
    public sealed record WelcomeMessage(int Version, long Id, string MapId, int TickRate, string? Content = null, string? Token = null) : ServerMessage
    {
        /// <inheritdoc />
        public override string Type => MessageTypes.Welcome;
    }

    /// <summary>
    /// One entity as a client sees it: <c>{"id":7,"kind":"monster","display_key":"monster.x","cell":[3,4],"facing":"e","next":[4,4],"speed":3.5}</c>.
    /// <c>display_key</c> is left out when there is none; <c>next</c> and <c>speed</c> only while walking.
    /// </summary>
    /// <param name="Id">Entity id.</param>
    /// <param name="Kind">An <see cref="EntityKinds"/> term.</param>
    /// <param name="DisplayKey">The content id to look up in the display bundle, or null.</param>
    /// <param name="Cell">The last cell the entity fully reached.</param>
    /// <param name="Facing">A <c>Directions</c> term.</param>
    /// <param name="Next">The cell being walked to, or null when standing.</param>
    /// <param name="Speed">Cells per second while walking, 0 when standing.</param>
    public sealed record EntityRecord(long Id, string Kind, string? DisplayKey, GridCell Cell, string Facing, GridCell? Next, double Speed);

    /// <summary>The fields of a <see cref="ChangeRecord"/> that are present (VOCABULARY.md "View fields").</summary>
    [Flags]
    public enum ChangedFields
    {
        /// <summary>None.</summary>
        None = 0,

        /// <summary><c>cell</c>.</summary>
        Cell = 1,

        /// <summary><c>facing</c>.</summary>
        Facing = 2,

        /// <summary><c>next</c> (present and null means the walk stopped).</summary>
        Next = 4,

        /// <summary><c>speed</c>.</summary>
        Speed = 8,

        /// <summary><c>display_key</c> (present and null means it was cleared).</summary>
        DisplayKey = 16,
    }

    /// <summary>
    /// An entity already in view that changed: its id plus only the changed fields, e.g. <c>{"id":7,"cell":[4,4],"next":[5,4]}</c> or
    /// <c>{"id":7,"cell":[5,4],"next":null,"speed":0}</c>. Fields not in <see cref="Fields"/> keep their old value (and are default here).
    /// </summary>
    /// <param name="Id">Entity id.</param>
    /// <param name="Fields">Which of the other fields are present.</param>
    /// <param name="Cell">New cell, when <see cref="ChangedFields.Cell"/>.</param>
    /// <param name="Facing">New facing, when <see cref="ChangedFields.Facing"/>.</param>
    /// <param name="Next">New next cell (null: stopped), when <see cref="ChangedFields.Next"/>.</param>
    /// <param name="Speed">New speed, when <see cref="ChangedFields.Speed"/>.</param>
    /// <param name="DisplayKey">New display key, when <see cref="ChangedFields.DisplayKey"/>.</param>
    public sealed record ChangeRecord(long Id, ChangedFields Fields, GridCell Cell, string? Facing, GridCell? Next, double Speed, string? DisplayKey);

    /// <summary>
    /// <c>snapshot</c>: everything the client sees at <see cref="Tick"/>; it replaces the client's view. A snapshot for another
    /// <see cref="MapId"/> than before means the character changed maps (a warp): load that map first.
    /// <c>{"type":"snapshot","tick":120,"map":"map.x","id":42,"entities":[...]}</c>.
    /// </summary>
    /// <param name="Tick">The map instance tick it describes.</param>
    /// <param name="MapId">The map the character is on.</param>
    /// <param name="Id">The client's own entity id (always among <see cref="Entities"/>).</param>
    /// <param name="Entities">Every visible entity, ordered by id.</param>
    public sealed record SnapshotMessage(long Tick, string MapId, long Id, IReadOnlyList<EntityRecord> Entities) : ServerMessage
    {
        /// <inheritdoc />
        public override string Type => MessageTypes.Snapshot;
    }

    /// <summary>
    /// <c>delta</c>: one tick's changes, relative to the last snapshot or delta. Empty lists are left out.
    /// <c>{"type":"delta","tick":121,"appeared":[...],"changed":[...],"disappeared":[9]}</c>.
    /// </summary>
    /// <param name="Tick">The map instance tick it describes.</param>
    /// <param name="Appeared">Entities that came into view, as full records.</param>
    /// <param name="Changed">Entities in view that changed, with only their changed fields.</param>
    /// <param name="Disappeared">Ids of entities that went out of view.</param>
    public sealed record DeltaMessage(long Tick, IReadOnlyList<EntityRecord> Appeared, IReadOnlyList<ChangeRecord> Changed, IReadOnlyList<long> Disappeared)
        : ServerMessage
    {
        /// <inheritdoc />
        public override string Type => MessageTypes.Delta;
    }

    /// <summary>
    /// <c>rejected</c>: an intent was refused and nothing changed. <c>{"type":"rejected","intent":"move","reason":"no_path","cell":[30,2]}</c>.
    /// </summary>
    /// <param name="Intent">The message type that was refused (<c>move</c>).</param>
    /// <param name="Reason">A VOCABULARY.md "Movement reasons" (<c>MoveRejected</c>) term.</param>
    /// <param name="Cell">The target cell of the refused move, if any.</param>
    public sealed record RejectedMessage(string Intent, string Reason, GridCell? Cell) : ServerMessage
    {
        /// <inheritdoc />
        public override string Type => MessageTypes.Rejected;
    }

    /// <summary><c>pong</c>: answer to <see cref="PingMessage"/>. <c>{"type":"pong","seq":7}</c>.</summary>
    /// <param name="Seq">The ping's number.</param>
    public sealed record PongMessage(long Seq) : ServerMessage
    {
        /// <inheritdoc />
        public override string Type => MessageTypes.Pong;
    }

    /// <summary><c>bye</c>: the last message before the server closes the connection. <c>{"type":"bye","reason":"timeout"}</c>.</summary>
    /// <param name="Reason">A <see cref="ByeReasons"/> term.</param>
    public sealed record ByeMessage(string Reason) : ServerMessage
    {
        /// <inheritdoc />
        public override string Type => MessageTypes.Bye;
    }
}
