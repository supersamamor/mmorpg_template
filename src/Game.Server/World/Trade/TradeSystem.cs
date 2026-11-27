using System.Globalization;
using System.Text;
using System.Text.Json;
using Game.Server.World.Inventory;
using Game.Shared.Definitions;
using Game.Shared.Items;
using Game.Shared.Protocol;
using Game.Shared.Vocabulary;
using Game.Shared.World;

namespace Game.Server.World.Trade;

/// <summary>
/// Player trade on one map instance (T-903, ARCHITECTURE §6.10): asks, the two offers, the locks and confirms, and every way a trade ends. The swap itself is the inventory's
/// (<see cref="TradeSwapRequested"/>, all or nothing in one step for both, <see cref="TradeRules"/>); this system never changes a bag. Runs after the inventory.
/// </summary>
/// <remarks>
/// <para><b>Asking.</b> A player asks another on the same instance (<see cref="TradeAsk"/>; the gateway did the rate limit and the block check). Refused when either is dead, the asked one
/// is not a player here (<c>not_online</c>), farther than <c>trade.range</c>, in a trade or with an ask of its own under way (<c>busy</c>), when the asker already waits for an answer
/// (<c>ask_pending</c>), or when one is saved and the other not (<c>cannot_trade</c>). An ask expires after <c>trade.ask_timeout_ms</c> of simulated time.</para>
/// <para><b>Offers.</b> Each side offers carried entries (whole, or part of a stack), at most <c>trade.max_entries</c>, and currency it carries; worn and not-tradeable items are refused.
/// The offer keeps each entry exactly as it was offered. Any change to either offer, and any unlock, unlocks both and clears both confirms; every change raises the version.</para>
/// <para><b>Confirming.</b> Only when both are locked; a confirm names the version it saw and is refused <c>stale</c> for another. When both confirmed the same version, the swap is
/// asked for and the trade is frozen (<c>swapping</c>) until the inventory answers.</para>
/// <para><b>Ending.</b> A cancel, a disconnect (the gateway), death, a warp, leaving the world, or moving farther apart than <c>trade.range</c> ends the trade, and nothing moves. When the
/// inventory changes under an offer (an offered entry dropped, sold, used, equipped, refined), the offer follows what is carried and both sides are unlocked.</para>
/// <para><b>Logs.</b> Each swap writes one structured line (<c>[trade] {...}</c>) with both character ids and what moved, and nothing else about the players.</para>
/// </remarks>
public sealed class TradeSystem : ITickSystem
{
    private static readonly IReadOnlyCollection<Type> Owned =
        [typeof(TradeAsk), typeof(TradeAnswer), typeof(TradeOfferItem), typeof(TradeOfferCurrency), typeof(TradeLock), typeof(TradeConfirm), typeof(TradeCancel)];
    private static long _lastTrade;

    private readonly TradeBalanceDef _balance;
    private readonly InventoryRules _rules;
    private readonly IInventoryStore _store;
    private readonly TimeProvider _time;
    private readonly Dictionary<long, Ask> _asksTo = new();       // by the asked
    private readonly Dictionary<long, Ask> _asksFrom = new();     // by the asker
    private readonly Dictionary<long, OpenTrade> _trades = new(); // by each side
    private readonly Dictionary<long, OpenTrade> _byId = new();
    private readonly HashSet<long> _bagsChanged = new();
    private readonly List<(EntityId Who, string Reason)> _gone = new();
    private readonly List<TradeSwapResolved> _resolved = new();
    private MapInstance? _instance;
    private long _nowMs;

    /// <summary>Creates the trade system for one instance; <paramref name="store"/> is the inventory's (read only here), <paramref name="time"/> stamps the log line.</summary>
    public TradeSystem(TradeBalanceDef balance, InventoryRules rules, IInventoryStore store, TimeProvider? time = null)
    {
        _balance = balance ?? throw new ArgumentNullException(nameof(balance));
        _rules = rules ?? throw new ArgumentNullException(nameof(rules));
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _time = time ?? TimeProvider.System;
    }

    /// <inheritdoc />
    public string Name => "trade";

    /// <inheritdoc />
    public IReadOnlyCollection<Type> IntentTypes => Owned;

    /// <summary>Open trades on this instance.</summary>
    public int TradeCount => _byId.Count;

    /// <summary>Asks waiting for an answer on this instance.</summary>
    public int AskCount => _asksTo.Count;

    /// <inheritdoc />
    public void Tick(TickContext context)
    {
        if (!ReferenceEquals(_instance, context.Instance)) Setup(context);
        _nowMs = context.SimTimeMs;

        // What happened since the last tick first: the swaps' answers, then who left or died, then bags that changed under an offer.
        foreach (var resolved in _resolved) Resolve(context, resolved);
        _resolved.Clear();
        foreach (var (who, reason) in _gone) Gone(context, who, reason);
        _gone.Clear();
        foreach (var player in _bagsChanged) Follow(context, player);
        _bagsChanged.Clear();

        var intents = context.Intents;
        for (var i = 0; i < intents.Count; i++)
        {
            switch (intents[i])
            {
                case TradeAsk ask: AskFor(context, ask.Entity, ask.Target); break;
                case TradeAnswer answer: Answer(context, answer.Entity, answer.Accept); break;
                case TradeOfferItem offer: OfferItem(context, offer.Entity, offer.EntryId, offer.Quantity); break;
                case TradeOfferCurrency currency: OfferCurrency(context, currency.Entity, currency.Amount); break;
                case TradeLock locking: Lock(context, locking.Entity, locking.Locked); break;
                case TradeConfirm confirm: Confirm(context, confirm.Entity, confirm.Version); break;
                case TradeCancel cancel: Cancel(context, cancel.Entity, cancel.Reason); break;
            }
        }
        ExpireAsks(context);
        CheckOpen(context);
    }

    private void Setup(TickContext context)
    {
        _instance = context.Instance;
        var events = _instance.Events;
        events.Subscribe<EntityLeftMap>(e =>
        {
            if (e.Kind == EntityKind.Player) _gone.Add((e.Entity, e.Reason == MapChangeReason.Warp ? TradeEnds.LeftMap : TradeEnds.Left));
        });
        events.Subscribe<EntityDied>(e =>
        {
            if (e.Kind == EntityKind.Player) _gone.Add((e.Entity, TradeEnds.Dead));
        });
        events.Subscribe<InventoryChanged>(e =>
        {
            if (e.Cause != InventoryChanges.Traded && _trades.ContainsKey(e.Player.Value)) _bagsChanged.Add(e.Player.Value);
        });
        events.Subscribe<TradeSwapResolved>(e =>
        {
            if (_byId.ContainsKey(e.Trade)) _resolved.Add(e);
        });
        events.Subscribe<ItemsTraded>(e => _instance.Log.Info(LogLine(e)));
    }

    // ---- asking ----------------------------------------------------------------------------------------------------------------------------

    private void AskFor(TickContext context, EntityId from, EntityId to)
    {
        var intent = MessageTypes.TradeAsk;
        if (!Find(from, out var asker)) return;
        string? reason = null;
        if (asker.Dead) reason = TradeReasons.Dead;
        else if (from == to) reason = TradeReasons.InvalidTarget;
        else if (_trades.ContainsKey(from.Value)) reason = TradeReasons.Busy;
        else if (_asksFrom.TryGetValue(from.Value, out var mine) && mine.ExpiresAtMs > _nowMs) reason = TradeReasons.AskPending;
        else if (!Find(to, out var asked)) reason = TradeReasons.NotOnline;
        else if (asked.Kind != EntityKind.Player) reason = TradeReasons.InvalidTarget;
        else if ((asker.Save is null) != (asked.Save is null)) reason = TradeReasons.CannotTrade;
        else if (asked.Dead) reason = TradeReasons.Dead;
        else if (!InRange(asker, asked)) reason = TradeReasons.TooFar;
        else if (_trades.ContainsKey(to.Value) || _asksTo.ContainsKey(to.Value) || _asksFrom.ContainsKey(to.Value)) reason = TradeReasons.Busy;
        if (reason is not null)
        {
            context.Publish(new TradeRefused(from, intent, reason));
            return;
        }
        if (_asksTo.TryGetValue(from.Value, out var toMe)) DropAsk(context, toMe, TradeNotices.Declined);   // asking someone else answers an ask one had
        var ask = new Ask(from, to, _nowMs + _balance.AskTimeoutMs);
        _asksFrom[from.Value] = ask;
        _asksTo[to.Value] = ask;
        context.Publish(new TradeAsked(from, to, _balance.AskTimeoutMs));
        context.Publish(new TradeNoticed(from, TradeNotices.AskSent, to));
    }

    private void Answer(TickContext context, EntityId who, bool accept)
    {
        var intent = MessageTypes.TradeAnswer;
        if (!_asksTo.TryGetValue(who.Value, out var ask) || ask.ExpiresAtMs <= _nowMs)
        {
            context.Publish(new TradeRefused(who, intent, TradeReasons.NoAsk));
            return;
        }
        Forget(ask);
        if (!accept)
        {
            context.Publish(new TradeNoticed(ask.From, TradeNotices.Declined, who));
            return;
        }
        string? reason = null;
        if (!Find(who, out var asked) || !Find(ask.From, out var asker)) reason = TradeReasons.NoAsk;
        else if (asked.Dead || asker.Dead) reason = TradeReasons.Dead;
        else if (!InRange(asker, asked)) reason = TradeReasons.TooFar;
        else if (_trades.ContainsKey(who.Value) || _trades.ContainsKey(ask.From.Value)) reason = TradeReasons.Busy;
        if (reason is not null)
        {
            context.Publish(new TradeRefused(who, intent, reason));
            context.Publish(new TradeNoticed(ask.From, TradeNotices.Expired, who));
            return;
        }
        if (_asksFrom.TryGetValue(who.Value, out var own)) DropAsk(context, own, TradeNotices.Expired);   // its own ask to someone else ends
        var trade = new OpenTrade(Interlocked.Increment(ref _lastTrade), new Side(ask.From), new Side(who));
        _trades[ask.From.Value] = trade;
        _trades[who.Value] = trade;
        _byId[trade.Id] = trade;
        Changed(context, trade);
    }

    /// <summary>Ends <paramref name="ask"/> without a trade: both are told <paramref name="notice"/> (the asked, only when the ask expired or was withdrawn).</summary>
    private void DropAsk(TickContext context, Ask ask, string notice)
    {
        Forget(ask);
        context.Publish(new TradeNoticed(ask.From, notice, ask.To));
        if (notice == TradeNotices.Expired) context.Publish(new TradeNoticed(ask.To, TradeNotices.Expired, ask.From));
    }

    private void Forget(Ask ask)
    {
        if (_asksTo.TryGetValue(ask.To.Value, out var a) && ReferenceEquals(a, ask)) _asksTo.Remove(ask.To.Value);
        if (_asksFrom.TryGetValue(ask.From.Value, out var b) && ReferenceEquals(b, ask)) _asksFrom.Remove(ask.From.Value);
    }

    private void ExpireAsks(TickContext context)
    {
        if (_asksTo.Count == 0) return;
        foreach (var ask in _asksTo.Values.Where(a => a.ExpiresAtMs <= _nowMs).ToList()) DropAsk(context, ask, TradeNotices.Expired);
    }

    // ---- offers --------------------------------------------------------------------------------------------------------------------------

    private void OfferItem(TickContext context, EntityId who, long entryId, int quantity)
    {
        var intent = MessageTypes.TradeOffer;
        if (Open(context, who, intent) is not { } trade) return;
        var side = trade.SideOf(who);
        var index = side.Lines.FindIndex(l => l.Entry.Id == entryId);
        if (quantity == 0)
        {
            if (index < 0) return;   // nothing to take out: nothing changes
            side.Lines.RemoveAt(index);
            Changed(context, trade);
            return;
        }
        var entry = _store.TryGet(who, out var bag) ? bag.Find(entryId) : null;
        var reason = TradeRules.CanOffer(_rules, entry);
        if (reason is null && (quantity < 0 || quantity > entry!.Quantity)) reason = TradeReasons.InvalidQuantity;
        if (reason is null && index < 0 && side.Lines.Count >= _balance.MaxEntries) reason = TradeReasons.OfferFull;
        if (reason is not null)
        {
            context.Publish(new TradeRefused(who, intent, reason));
            return;
        }
        var line = new TradeLine(entry!, quantity);
        if (index >= 0)
        {
            if (side.Lines[index] == line) return;
            side.Lines[index] = line;
        }
        else side.Lines.Add(line);
        Changed(context, trade);
    }

    private void OfferCurrency(TickContext context, EntityId who, long amount)
    {
        var intent = MessageTypes.TradeCurrency;
        if (Open(context, who, intent) is not { } trade) return;
        var side = trade.SideOf(who);
        var carried = _store.TryGet(who, out var bag) ? bag.Currency : 0;
        if (amount < 0 || amount > carried)
        {
            context.Publish(new TradeRefused(who, intent, TradeReasons.InvalidQuantity));
            return;
        }
        if (side.Currency == amount) return;
        side.Currency = amount;
        Changed(context, trade);
    }

    private void Lock(TickContext context, EntityId who, bool locked)
    {
        var intent = locked ? MessageTypes.TradeLock : MessageTypes.TradeUnlock;
        if (Open(context, who, intent) is not { } trade) return;
        var side = trade.SideOf(who);
        if (locked)
        {
            if (side.Locked) return;
            side.Locked = true;
            trade.Version++;
            Publish(context, trade);
            return;
        }
        if (!trade.A.Locked && !trade.B.Locked) return;
        Changed(context, trade);
    }

    private void Confirm(TickContext context, EntityId who, int version)
    {
        var intent = MessageTypes.TradeConfirm;
        if (Open(context, who, intent) is not { } trade) return;
        if (!trade.A.Locked || !trade.B.Locked)
        {
            context.Publish(new TradeRefused(who, intent, TradeReasons.NotLocked));
            return;
        }
        if (version != trade.Version)
        {
            context.Publish(new TradeRefused(who, intent, TradeReasons.Stale));
            return;
        }
        var side = trade.SideOf(who);
        if (side.Confirmed) return;
        side.Confirmed = true;   // the version stays: the other side's confirm of the same offers still counts
        Publish(context, trade);
        if (!trade.A.Confirmed || !trade.B.Confirmed) return;
        // Nothing that ends a trade may have happened this tick before the swap is asked for: from here only the inventory's answer ends it.
        if (!Find(trade.A.Player, out var a) || !Find(trade.B.Player, out var b))
        {
            Close(context, trade, TradeEnds.Left, null);
            return;
        }
        if (a.Dead || b.Dead)
        {
            Close(context, trade, TradeEnds.Dead, a.Dead ? a.Id : b.Id);
            return;
        }
        if (!InRange(a, b))
        {
            Close(context, trade, TradeEnds.TooFar, null);
            return;
        }
        trade.Swapping = true;
        context.Publish(new TradeSwapRequested(trade.Id, trade.Version, trade.A.Player, trade.A.Offer(), trade.B.Player, trade.B.Offer()));
    }

    private void Cancel(TickContext context, EntityId who, string reason)
    {
        if (_trades.TryGetValue(who.Value, out var trade))
        {
            if (trade.Swapping)
            {
                // Both confirmed: the swap is under way and decides. A disconnect lets it finish (the character is still in the world, and saved with it).
                if (reason == TradeEnds.Cancelled) context.Publish(new TradeRefused(who, MessageTypes.TradeCancel, TradeReasons.Swapping));
                return;
            }
            Close(context, trade, reason, who);
            return;
        }
        if (_asksFrom.TryGetValue(who.Value, out var mine))
        {
            DropAsk(context, mine, TradeNotices.Expired);   // withdrawn: to the asked it is as if it expired
            return;
        }
        if (_asksTo.TryGetValue(who.Value, out var toMe))
        {
            Forget(toMe);
            context.Publish(new TradeNoticed(toMe.From, TradeNotices.Declined, who));
            return;
        }
        if (reason == TradeEnds.Cancelled) context.Publish(new TradeRefused(who, MessageTypes.TradeCancel, TradeReasons.NotTrading));
    }

    /// <summary>The open trade of <paramref name="who"/> that may change now, or null after a refusal (<c>not_trading</c>, <c>swapping</c>).</summary>
    private OpenTrade? Open(TickContext context, EntityId who, string intent)
    {
        if (!_trades.TryGetValue(who.Value, out var trade))
        {
            context.Publish(new TradeRefused(who, intent, TradeReasons.NotTrading));
            return null;
        }
        if (trade.Swapping)
        {
            context.Publish(new TradeRefused(who, intent, TradeReasons.Swapping));
            return null;
        }
        return trade;
    }

    /// <summary>An offer changed: both sides unlock, both confirms go, the version goes up, both are told.</summary>
    private static void Changed(TickContext context, OpenTrade trade)
    {
        trade.A.Locked = trade.B.Locked = false;
        trade.A.Confirmed = trade.B.Confirmed = false;
        trade.Version++;
        Publish(context, trade);
    }

    private static void Publish(TickContext context, OpenTrade trade) =>
        context.Publish(new TradeChanged(trade.Id, trade.Version, trade.A.View(), trade.B.View()));

    // ---- what happens around a trade -----------------------------------------------------------------------------------------------------

    /// <summary>
    /// <paramref name="player"/>'s bag changed under its offer (it dropped, sold, used, equipped or refined something): the offer keeps only what is still carried, as it is now, and both
    /// sides unlock when anything in it changed.
    /// </summary>
    private void Follow(TickContext context, long player)
    {
        if (!_trades.TryGetValue(player, out var trade) || trade.Swapping) return;
        var side = trade.A.Player.Value == player ? trade.A : trade.B;
        if (!_store.TryGet(side.Player, out var bag)) return;
        var changed = false;
        for (var i = side.Lines.Count - 1; i >= 0; i--)
        {
            var line = side.Lines[i];
            var now = bag.Find(line.Entry.Id);
            if (TradeRules.CanOffer(_rules, now) is not null)
            {
                side.Lines.RemoveAt(i);
                changed = true;
            }
            else if (!now!.Equals(line.Entry))
            {
                side.Lines[i] = new TradeLine(now, Math.Min(line.Quantity, now.Quantity));
                changed = true;
            }
        }
        if (side.Currency > bag.Currency)
        {
            side.Currency = bag.Currency;
            changed = true;
        }
        if (changed) Changed(context, trade);
    }

    /// <summary>A player left the instance, warped or died: its ask and its trade end (a trade whose swap is under way waits for the inventory's answer).</summary>
    private void Gone(TickContext context, EntityId who, string reason)
    {
        if (_asksFrom.TryGetValue(who.Value, out var mine)) DropAsk(context, mine, TradeNotices.Expired);
        if (_asksTo.TryGetValue(who.Value, out var toMe)) DropAsk(context, toMe, TradeNotices.Expired);
        if (_trades.TryGetValue(who.Value, out var trade) && !trade.Swapping) Close(context, trade, reason, who);
    }

    /// <summary>Every open trade whose sides are no longer both alive, here and within <c>trade.range</c> ends; nothing moves.</summary>
    private void CheckOpen(TickContext context)
    {
        if (_byId.Count == 0) return;
        foreach (var trade in _byId.Values.ToList())
        {
            if (trade.Swapping) continue;
            var hasA = Find(trade.A.Player, out var a);
            var hasB = Find(trade.B.Player, out var b);
            if (!hasA || !hasB) Close(context, trade, TradeEnds.Left, hasA ? trade.B.Player : trade.A.Player);
            else if (a.Dead || b.Dead) Close(context, trade, TradeEnds.Dead, a.Dead ? a.Id : b.Id);
            else if (!InRange(a, b)) Close(context, trade, TradeEnds.TooFar, null);
        }
    }

    /// <summary>The inventory answered a swap: done, or refused (nothing moved, the trade ends with the reason, about the side it names).</summary>
    private void Resolve(TickContext context, TradeSwapResolved resolved)
    {
        if (!_byId.TryGetValue(resolved.Trade, out var trade)) return;
        var cause = resolved.Side switch
        {
            TradeSide.A => trade.A.Player,
            TradeSide.B => trade.B.Player,
            _ => (EntityId?)null,
        };
        Close(context, trade, resolved.Reason ?? TradeEnds.Done, cause);
    }

    /// <summary>Ends <paramref name="trade"/> for both, <paramref name="cause"/> (if any) being the side that caused it.</summary>
    private void Close(TickContext context, OpenTrade trade, string reason, EntityId? cause)
    {
        _byId.Remove(trade.Id);
        _trades.Remove(trade.A.Player.Value);
        _trades.Remove(trade.B.Player.Value);
        context.Publish(new TradeClosed(trade.A.Player, trade.B.Player, reason, cause == trade.A.Player));
        context.Publish(new TradeClosed(trade.B.Player, trade.A.Player, reason, cause == trade.B.Player));
    }

    // ---- helpers -----------------------------------------------------------------------------------------------------------------------------

    private bool Find(EntityId id, out Entity entity) => _instance!.TryGetEntity(id, out entity!);

    private bool InRange(Entity a, Entity b) => GridDistance.Chebyshev(a.Cell, b.Cell) <= _balance.Range;

    /// <summary>The support and audit line of one swap: both character ids, what moved (entry ids, items, quantities, currency) and when. Nothing else about the players.</summary>
    internal string LogLine(ItemsTraded e)
    {
        var buffer = new System.Buffers.ArrayBufferWriter<byte>();
        using (var w = new Utf8JsonWriter(buffer))
        {
            w.WriteStartObject();
            w.WriteNumber("trade", e.Trade);
            w.WriteString("at", _time.GetUtcNow().UtcDateTime.ToString("O", CultureInfo.InvariantCulture));
            w.WriteString("a", e.CharacterA?.ToString() ?? "entity:" + e.A.Value.ToString(CultureInfo.InvariantCulture));
            w.WriteString("b", e.CharacterB?.ToString() ?? "entity:" + e.B.Value.ToString(CultureInfo.InvariantCulture));
            Moved(w, "a_gave", e.FromA);
            w.WriteNumber("a_currency", e.CurrencyFromA);
            Moved(w, "b_gave", e.FromB);
            w.WriteNumber("b_currency", e.CurrencyFromB);
            w.WriteEndObject();
        }
        return "[trade] " + Encoding.UTF8.GetString(buffer.WrittenSpan);

        static void Moved(Utf8JsonWriter w, string name, IReadOnlyList<TradedEntry> entries)
        {
            w.WriteStartArray(name);
            foreach (var entry in entries)
            {
                w.WriteStartObject();
                w.WriteNumber("from", entry.FromEntryId);
                w.WriteNumber("to", entry.ToEntryId);
                w.WriteString("item", entry.ItemId);
                w.WriteNumber("quantity", entry.Quantity);
                w.WriteEndObject();
            }
            w.WriteEndArray();
        }
    }

    private sealed record Ask(EntityId From, EntityId To, long ExpiresAtMs);

    private sealed class Side(EntityId player)
    {
        public readonly EntityId Player = player;
        public readonly List<TradeLine> Lines = new();
        public long Currency;
        public bool Locked;
        public bool Confirmed;

        public TradeOffer Offer() => new(Lines.ToArray(), Currency);

        public TradeSideView View() => new(Player, Offer(), Locked, Confirmed);
    }

    private sealed class OpenTrade(long id, Side a, Side b)
    {
        public readonly long Id = id;
        public readonly Side A = a;
        public readonly Side B = b;
        public int Version;
        public bool Swapping;

        public Side SideOf(EntityId player) => A.Player == player ? A : B;
    }
}
