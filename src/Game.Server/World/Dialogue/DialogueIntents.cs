namespace Game.Server.World.Dialogue;

/// <summary>
/// A player asks to talk to the NPC entity <c>Target</c>: the dialogue system walks it into <c>dialogue.talk_range</c> with a <see cref="MoveTo"/>, then starts the
/// NPC's dialogue. A new order (a move, a stop) cancels the walk. Routed although the entity is dead, so a dead player is told <c>dead</c>.
/// </summary>
public sealed record TalkTo(EntityId Entity, EntityId Target) : IDeadEntityIntent;

/// <summary>A player picks choice <c>Index</c> (the index the server showed) of its dialogue's current node, or at a node without choices continues with index 0.</summary>
public sealed record DialogueChoose(EntityId Entity, int Index) : IIntent;

/// <summary>A player ends its dialogue and any service it opened. Ignored when it has none.</summary>
public sealed record DialogueClose(EntityId Entity) : IIntent;
