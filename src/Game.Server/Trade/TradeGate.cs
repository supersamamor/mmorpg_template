using Game.Server.Chat;
using Game.Server.Net;
using Game.Server.Party;
using Game.Server.World;
using Game.Server.World.Trade;
using Game.Shared.Protocol;
using Game.Shared.Vocabulary;

namespace Game.Server.Trade;

/// <summary>
/// What a trade ask goes through before it reaches the tick (T-903): the per-account rate limit (<see cref="TradeOptions"/>), and the asked player's block list (T-901) and presence. An ask
/// to oneself is <c>invalid_target</c>; to a character that does not play now, or whose account blocks the asker, <c>not_online</c> (the same answer, so a block does not show, as T-902
/// does). Everything else (range, busy, dead, the instance) is the <see cref="TradeSystem"/>'s, on the tick. Thread-safe.
/// </summary>
public sealed class TradeGate(TradeOptions options, IPartyDirectory directory, Action<IIntent> enqueue, TimeProvider time)
{
    private readonly ChatLimits _limits = new(new ChatOptions { LinesPerMinute = options.AsksPerMinute, Burst = options.AskBurst }, time);

    /// <summary>Handles <c>trade_ask</c> of <paramref name="asker"/> (a character that plays now): refused at once, or queued for the tick.</summary>
    public void Ask(ChatPeer asker, long target)
    {
        var key = asker.AccountId is { } account ? ChatLimits.Of(account) : ChatLimits.Of(asker.Character);
        if (!_limits.TryTake(key, out var wait))
        {
            Refuse(asker, TradeReasons.RateLimited, wait);
            return;
        }
        var id = new EntityId(target);
        if (id == asker.Character)
        {
            Refuse(asker, TradeReasons.InvalidTarget);
            return;
        }
        if (directory.Playing(id) is not { } asked || directory.Blocks(asked, asker))
        {
            Refuse(asker, TradeReasons.NotOnline);
            return;
        }
        enqueue(new TradeAsk(asker.Character, id));
    }

    private static void Refuse(ChatPeer to, string reason, long? retryAfter = null)
    {
        var message = new TradeRefusedMessage(MessageTypes.TradeAsk, reason, retryAfter);
        to.Sink.Post(message, ProtocolJson.ToBytes(message));
    }
}
