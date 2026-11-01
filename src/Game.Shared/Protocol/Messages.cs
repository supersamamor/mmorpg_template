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

    /// <summary><c>stop</c>: stop walking at the next cell, and stop auto-attacking. <c>{"type":"stop"}</c>.</summary>
    public sealed record StopMessage : ClientMessage
    {
        /// <inheritdoc />
        public override string Type => MessageTypes.Stop;
    }

    /// <summary>
    /// <c>attack</c>: auto-attack <see cref="Target"/> until it dies or leaves, or the client stops. A request: the target must be an entity the
    /// client sees, and combat may still refuse it (<see cref="RejectedMessage"/> with an "Attack reasons" term). <c>{"type":"attack","target":7}</c>.
    /// </summary>
    /// <param name="Target">Entity id of the target (a monster in v1).</param>
    public sealed record AttackMessage(long Target) : ClientMessage
    {
        /// <inheritdoc />
        public override string Type => MessageTypes.Attack;
    }

    /// <summary><c>stop_attack</c>: stop auto-attacking (a walk goes on). <c>{"type":"stop_attack"}</c>.</summary>
    public sealed record StopAttackMessage : ClientMessage
    {
        /// <inheritdoc />
        public override string Type => MessageTypes.StopAttack;
    }

    /// <summary><c>respawn</c>: a dead character asks to come back at the respawn point (ignored while alive). <c>{"type":"respawn"}</c>.</summary>
    public sealed record RespawnMessage : ClientMessage
    {
        /// <inheritdoc />
        public override string Type => MessageTypes.Respawn;
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
    /// One entity as a client sees it: <c>{"id":7,"kind":"monster","display_key":"monster.x","cell":[3,4],"facing":"e","next":[4,4],"speed":3.5,"hp":0.37}</c>.
    /// <c>display_key</c> is left out when there is none; <c>next</c> and <c>speed</c> only while walking; <c>hp</c> only for an entity that
    /// fights; <c>dead</c> only when true. A ground item (<c>kind</c> <c>item</c>, <c>display_key</c> = its item id) carries <c>quantity</c> and
    /// <c>mine</c>: <c>{"id":9,"kind":"item","display_key":"item.x","cell":[3,4],"facing":"s","quantity":2,"mine":true}</c>.
    /// </summary>
    /// <param name="Id">Entity id.</param>
    /// <param name="Kind">An <see cref="EntityKinds"/> term.</param>
    /// <param name="DisplayKey">The content id to look up in the display bundle, or null.</param>
    /// <param name="Cell">The last cell the entity fully reached.</param>
    /// <param name="Facing">A <c>Directions</c> term.</param>
    /// <param name="Next">The cell being walked to, or null when standing.</param>
    /// <param name="Speed">Cells per second while walking, 0 when standing.</param>
    /// <param name="Hp">HP as a fraction of max HP, 0–1 in steps of 0.01 (never 0 while alive, never 1 while hurt), or null for an entity
    /// without HP. Exact HP goes only to its owner (<see cref="VitalsRecord"/>).</param>
    /// <param name="Dead">True while the entity is dead.</param>
    /// <param name="Quantity">A ground item's quantity; 0 for everything else.</param>
    /// <param name="Mine">A ground item: true when this client may pick it up now (no loot priority, its own, or the priority is over); null for
    /// everything else. Who holds the priority, and until when, is never sent.</param>
    public sealed record EntityRecord(long Id, string Kind, string? DisplayKey, GridCell Cell, string Facing, GridCell? Next, double Speed,
        double? Hp = null, bool Dead = false, int Quantity = 0, bool? Mine = null);

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

        /// <summary><c>hp</c> (the rounded fraction changed).</summary>
        Hp = 32,

        /// <summary><c>dead</c> (died, or came back).</summary>
        Dead = 64,

        /// <summary><c>mine</c> (a ground item's loot priority ended).</summary>
        Mine = 128,
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
    /// <param name="Hp">New HP fraction, when <see cref="ChangedFields.Hp"/>.</param>
    /// <param name="Dead">New dead flag, when <see cref="ChangedFields.Dead"/>.</param>
    /// <param name="Mine">New pick-up flag, when <see cref="ChangedFields.Mine"/>.</param>
    public sealed record ChangeRecord(long Id, ChangedFields Fields, GridCell Cell, string? Facing, GridCell? Next, double Speed, string? DisplayKey,
        double Hp = 0, bool Dead = false, bool Mine = false);

    /// <summary>
    /// One attack seen in a tick, in a delta's <c>hits</c> as a compact array <c>[attacker, target, amount, result]</c>, e.g. <c>[42,7,13,"crit"]</c>.
    /// </summary>
    /// <param name="Attacker">Entity id of the attacker.</param>
    /// <param name="Target">Entity id of the target.</param>
    /// <param name="Amount">Damage dealt (0 on a miss).</param>
    /// <param name="Result">A "Hit results" term: <c>hit</c>, <c>crit</c> or <c>miss</c>.</param>
    public sealed record HitRecord(long Attacker, long Target, int Amount, string Result);

    /// <summary>
    /// The client's own exact HP and SP, in a snapshot's or delta's <c>vitals</c>: <c>{"hp":40,"max_hp":52,"sp":11,"max_sp":11}</c>. Only the
    /// owner gets it (others see the <c>hp</c> fraction); a delta carries it only when one of the four changed.
    /// </summary>
    public sealed record VitalsRecord(int Hp, int MaxHp, int Sp, int MaxSp);

    /// <summary>
    /// <c>snapshot</c>: everything the client sees at <see cref="Tick"/>; it replaces the client's view. A snapshot for another
    /// <see cref="MapId"/> than before means the character changed maps (a warp): load that map first.
    /// <c>{"type":"snapshot","tick":120,"map":"map.x","id":42,"entities":[...]}</c>.
    /// </summary>
    /// <param name="Tick">The map instance tick it describes.</param>
    /// <param name="MapId">The map the character is on.</param>
    /// <param name="Id">The client's own entity id (always among <see cref="Entities"/>).</param>
    /// <param name="Entities">Every visible entity, ordered by id.</param>
    /// <param name="Vitals">The client's own HP and SP, or null for a character without them.</param>
    public sealed record SnapshotMessage(long Tick, string MapId, long Id, IReadOnlyList<EntityRecord> Entities, VitalsRecord? Vitals = null) : ServerMessage
    {
        /// <inheritdoc />
        public override string Type => MessageTypes.Snapshot;
    }

    /// <summary>
    /// <c>delta</c>: one tick's changes, relative to the last snapshot or delta. Empty lists are left out.
    /// <c>{"type":"delta","tick":121,"appeared":[...],"changed":[...],"disappeared":[9],"hits":[[42,7,13,"hit"]],"vitals":{...}}</c>.
    /// </summary>
    /// <param name="Tick">The map instance tick it describes.</param>
    /// <param name="Appeared">Entities that came into view, as full records.</param>
    /// <param name="Changed">Entities in view that changed, with only their changed fields.</param>
    /// <param name="Disappeared">Ids of entities that went out of view.</param>
    /// <param name="Hits">This tick's attacks whose attacker or target is in view, in the order they were resolved; null or empty when none.</param>
    /// <param name="Vitals">The client's own HP and SP when they changed this tick, else null.</param>
    public sealed record DeltaMessage(long Tick, IReadOnlyList<EntityRecord> Appeared, IReadOnlyList<ChangeRecord> Changed, IReadOnlyList<long> Disappeared,
        IReadOnlyList<HitRecord>? Hits = null, VitalsRecord? Vitals = null)
        : ServerMessage
    {
        /// <inheritdoc />
        public override string Type => MessageTypes.Delta;
    }

    /// <summary>
    /// <c>rejected</c>: an intent was refused and nothing changed. <c>{"type":"rejected","intent":"move","reason":"no_path","cell":[30,2]}</c> or
    /// <c>{"type":"rejected","intent":"attack","reason":"target_gone","target":99}</c>.
    /// </summary>
    /// <param name="Intent">The message type that was refused (<c>move</c>, <c>attack</c>).</param>
    /// <param name="Reason">For <c>move</c> a VOCABULARY.md "Movement reasons" (<c>MoveRejected</c>) term, for <c>attack</c> an "Attack reasons" term.</param>
    /// <param name="Cell">The target cell of the refused move, if any.</param>
    /// <param name="Target">The target of the refused attack, if any.</param>
    public sealed record RejectedMessage(string Intent, string Reason, GridCell? Cell, long? Target = null) : ServerMessage
    {
        /// <inheritdoc />
        public override string Type => MessageTypes.Rejected;
    }

    /// <summary>
    /// <c>attack_stopped</c>: the client's auto-attack on <see cref="Target"/> ended. <c>{"type":"attack_stopped","target":7,"reason":"target_dead"}</c>.
    /// </summary>
    /// <param name="Target">The former target.</param>
    /// <param name="Reason">An "Attack reasons" term (<c>target_dead</c>, <c>unreachable</c>, <c>cancelled</c>, ...).</param>
    public sealed record AttackStoppedMessage(long Target, string Reason) : ServerMessage
    {
        /// <inheritdoc />
        public override string Type => MessageTypes.AttackStopped;
    }

    /// <summary>
    /// <c>exp</c>: the client's levels and EXP, after every snapshot and whenever they change.
    /// <c>{"type":"exp","base_level":2,"base_exp":5,"base_next":24,"job_level":1,"job_exp":4,"job_next":10}</c>.
    /// </summary>
    /// <param name="BaseLevel">Base level.</param>
    /// <param name="BaseExp">Base EXP into the current level.</param>
    /// <param name="BaseNext">Base EXP the current level needs to level up; 0 at the max level.</param>
    /// <param name="JobLevel">Job level.</param>
    /// <param name="JobExp">Job EXP into the current job level.</param>
    /// <param name="JobNext">Job EXP the current job level needs; 0 at the job's max level.</param>
    public sealed record ExpMessage(int BaseLevel, long BaseExp, long BaseNext, int JobLevel, long JobExp, long JobNext) : ServerMessage
    {
        /// <inheritdoc />
        public override string Type => MessageTypes.Exp;
    }

    /// <summary>
    /// <c>level_up</c>: the client reached a level, one message per level, in order; exactly one of the two fields is present.
    /// <c>{"type":"level_up","base_level":3}</c> or <c>{"type":"level_up","job_level":2}</c>.
    /// </summary>
    /// <param name="BaseLevel">The base level reached, or null.</param>
    /// <param name="JobLevel">The job level reached, or null.</param>
    public sealed record LevelUpMessage(int? BaseLevel, int? JobLevel) : ServerMessage
    {
        /// <inheritdoc />
        public override string Type => MessageTypes.LevelUp;
    }

    /// <summary><c>you_died</c>: the client's character died; it sends <c>respawn</c> to come back. <c>{"type":"you_died","killer":7}</c>.</summary>
    /// <param name="Killer">Entity id of the killer, or null.</param>
    public sealed record YouDiedMessage(long? Killer) : ServerMessage
    {
        /// <inheritdoc />
        public override string Type => MessageTypes.YouDied;
    }

    /// <summary>
    /// <c>respawned</c>: the answer to <c>respawn</c>: the character is alive again with <see cref="Hp"/> on <see cref="Cell"/> of <see cref="MapId"/>
    /// (a snapshot of that place follows). <c>{"type":"respawned","map":"map.x","cell":[12,12],"hp":26}</c>.
    /// </summary>
    /// <param name="MapId">The respawn map.</param>
    /// <param name="Cell">The respawn cell.</param>
    /// <param name="Hp">HP it came back with.</param>
    public sealed record RespawnedMessage(string MapId, GridCell Cell, int Hp) : ServerMessage
    {
        /// <inheritdoc />
        public override string Type => MessageTypes.Respawned;
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
