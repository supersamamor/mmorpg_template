using Game.Server.Events;
using Game.Shared.Items;
using Game.Shared.Vocabulary;

namespace Game.Server.World.Trade;

// ---- intents (T-903). Every one is routed although its entity is dead, so the trade system can answer "dead" and end a trade. ----

/// <summary>A player asks <c>Target</c>, a player on its map instance, to trade. The gateway has done the rate limit and the block check.</summary>
public sealed record TradeAsk(EntityId Entity, EntityId Target) : IDeadEntityIntent;

/// <summary>A player answers the ask it has.</summary>
public sealed record TradeAnswer(EntityId Entity, bool Accept) : IDeadEntityIntent;

/// <summary>A player puts <c>Quantity</c> units of its carried entry <c>EntryId</c> in its offer (0 takes it out).</summary>
public sealed record TradeOfferItem(EntityId Entity, long EntryId, int Quantity) : IDeadEntityIntent;

/// <summary>A player sets the currency in its offer.</summary>
public sealed record TradeOfferCurrency(EntityId Entity, long Amount) : IDeadEntityIntent;

/// <summary>A player locks its offer (<c>Locked</c> true) or unlocks both (false).</summary>
public sealed record TradeLock(EntityId Entity, bool Locked) : IDeadEntityIntent;

/// <summary>A player confirms the two locked offers of <c>Version</c>.</summary>
public sealed record TradeConfirm(EntityId Entity, int Version) : IDeadEntityIntent;

/// <summary>
/// A player's ask or trade ends: it cancelled (<c>cancelled</c>), or the gateway says its connection dropped or another session took it over (<c>disconnected</c>). <c>Reason</c> is a
/// "Trade ends" term.
/// </summary>
public sealed record TradeCancel(EntityId Entity, string Reason) : IDeadEntityIntent;

// ---- events ----

/// <summary><c>TradeAsked</c>: <c>From</c> asked <c>To</c> to trade; the ask expires after <c>ExpiresInMs</c>.</summary>
public sealed record TradeAsked(EntityId From, EntityId To, long ExpiresInMs) : IGameEvent
{
    /// <inheritdoc />
    public string Name => EventNames.TradeAsked;
}

/// <summary><c>TradeNoticed</c>: news about an ask for <c>Player</c>, a "Trade notices" term, about <c>Other</c>.</summary>
public sealed record TradeNoticed(EntityId Player, string Notice, EntityId Other) : IGameEvent
{
    /// <inheritdoc />
    public string Name => EventNames.TradeNoticed;
}

/// <summary>What one side of an open trade offers and whether it locked and confirmed. Immutable.</summary>
public sealed record TradeSideView(EntityId Player, TradeOffer Offer, bool Locked, bool Confirmed);

/// <summary><c>TradeChanged</c>: trade <c>Trade</c> opened or changed; it is at <c>Version</c>. Both sides are told, each with its own side first.</summary>
public sealed record TradeChanged(long Trade, int Version, TradeSideView A, TradeSideView B) : IGameEvent
{
    /// <inheritdoc />
    public string Name => EventNames.TradeChanged;
}

/// <summary><c>TradeRefused</c>: the trade system refused <c>Intent</c> (a <c>trade_*</c> message type) of <c>Player</c> for a "Trade reasons" term; nothing changed.</summary>
public sealed record TradeRefused(EntityId Player, string Intent, string Reason) : IGameEvent
{
    /// <inheritdoc />
    public string Name => EventNames.TradeRefused;
}

/// <summary><c>TradeClosed</c>: the open trade of <c>Player</c> with <c>Partner</c> ended for a "Trade ends" term; <c>Own</c> when this side caused it.</summary>
public sealed record TradeClosed(EntityId Player, EntityId Partner, string Reason, bool Own) : IGameEvent
{
    /// <inheritdoc />
    public string Name => EventNames.TradeClosed;
}

/// <summary>
/// <c>TradeSwapRequested</c>: both sides of trade <c>Trade</c> confirmed version <c>Version</c>; the inventory swaps <c>FromA</c> and <c>FromB</c> on its next tick, all or nothing, and
/// answers with <see cref="TradeSwapResolved"/>.
/// </summary>
public sealed record TradeSwapRequested(long Trade, int Version, EntityId A, TradeOffer FromA, EntityId B, TradeOffer FromB) : IGameEvent
{
    /// <inheritdoc />
    public string Name => EventNames.TradeSwapRequested;
}

/// <summary><c>TradeSwapResolved</c>: the swap of trade <c>Trade</c> was done (<c>Reason</c> null) or refused for a "Trade ends" term about side <c>Side</c>; refused, nothing moved.</summary>
public sealed record TradeSwapResolved(long Trade, string? Reason, TradeSide Side) : IGameEvent
{
    /// <inheritdoc />
    public string Name => EventNames.TradeSwapResolved;
}

/// <summary>One entry that moved in a trade: the giver's entry id, the receiver's (the same when it moved whole), the item and the units.</summary>
public sealed record TradedEntry(long FromEntryId, long ToEntryId, string ItemId, int Quantity);

/// <summary>
/// <c>ItemsTraded</c>: <c>A</c> and <c>B</c> swapped what they offered, at <c>AtMs</c> (server clock): <c>FromA</c> went to B with <c>CurrencyFromA</c>, <c>FromB</c> to A with
/// <c>CurrencyFromB</c>. <c>CharacterA</c>, <c>CharacterB</c> are the permanent character ids (null for a development login). For quests, logs and dupe audits.
/// </summary>
public sealed record ItemsTraded(long Trade, EntityId A, EntityId B, Guid? CharacterA, Guid? CharacterB, IReadOnlyList<TradedEntry> FromA, long CurrencyFromA,
    IReadOnlyList<TradedEntry> FromB, long CurrencyFromB, long AtMs) : IGameEvent
{
    /// <inheritdoc />
    public string Name => EventNames.ItemsTraded;
}
