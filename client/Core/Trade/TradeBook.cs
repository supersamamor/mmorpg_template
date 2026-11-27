using System;
using Game.Shared.Protocol;
using Game.Shared.Vocabulary;

namespace Game.Client.Core.Trade;

/// <summary>An ask this character has: who asks, and when it ends (seconds of the client's wall clock).</summary>
public sealed record TradeAskView(long From, string Name, double EndsAt);

/// <summary>
/// The trade as the client holds it (T-903): the ask it was sent, the open trade's two offers exactly as the server last sent them (<c>trade</c>), and why the last one ended. In memory
/// only, emptied at every welcome (a resume or a takeover ends any trade on the server). The client decides nothing: what is in an offer, who is in range and whether a swap happened are
/// the server's; it only sends intents. A confirm names the version of the offers drawn, so offers that changed since are refused <c>stale</c>. Pure; the HUD redraws when
/// <see cref="Version"/> moves.
/// </summary>
public sealed class TradeBook
{
    /// <summary>Moves on every change.</summary>
    public int Version { get; private set; }

    /// <summary>The ask waiting for an answer, or null.</summary>
    public TradeAskView? Ask { get; private set; }

    /// <summary>The open trade as the server last sent it, or null.</summary>
    public TradeMessage? Open { get; private set; }

    /// <summary>The words of how the last trade ended, until the next one opens.</summary>
    public string? Ended { get; private set; }

    /// <summary>The last notice, refusal or end, as words (shown once in the flash line).</summary>
    public string? Notice { get; private set; }

    /// <summary>Moves when <see cref="Notice"/> is new.</summary>
    public int NoticeVersion { get; private set; }

    /// <summary>True when both sides locked and this side has not confirmed: Confirm may be pressed.</summary>
    public bool CanConfirm => Open is { } t && t.Mine.Locked && t.Theirs.Locked && !t.Mine.Confirmed;

    /// <summary>An ask came; it ends <c>expires_in</c> seconds from <paramref name="wallNow"/>.</summary>
    public void Apply(TradeAskedMessage asked, double wallNow)
    {
        Ask = new TradeAskView(asked.From, asked.Name, wallNow + asked.ExpiresInSeconds);
        Version++;
    }

    /// <summary>The open trade as it is now (it opened, or either side changed it).</summary>
    public void Apply(TradeMessage trade)
    {
        Open = trade;
        Ask = null;
        Ended = null;
        Version++;
    }

    /// <summary>News about an ask: its words; an expired ask is gone.</summary>
    public void Apply(TradeNoticeMessage notice)
    {
        if (notice.Notice == TradeNotices.Expired && Ask is { } ask && ask.Name == notice.Name) Ask = null;
        Say(UiText.TradeNotice(notice.Notice, notice.Name));
    }

    /// <summary>The trade ended: the window closes and says why.</summary>
    public void Apply(TradeClosedMessage closed)
    {
        Open = null;
        Ended = UiText.TradeClosed(closed.Reason, closed.Name, closed.Own);
        Say(Ended);
    }

    /// <summary>A refusal: its words; an answer refused leaves no ask to answer.</summary>
    public void Apply(TradeRefusedMessage refused)
    {
        if (refused.Intent == MessageTypes.TradeAnswer) Ask = null;
        Say(UiText.TradeRefused(refused.Reason, refused.RetryAfterSeconds));
    }

    /// <summary>The ask was answered (the answer is on its way): it is no longer shown.</summary>
    public void Answered()
    {
        Ask = null;
        Version++;
    }

    /// <summary>The ask is past its end on the client's clock (the server's word follows).</summary>
    public void Expire(double wallNow)
    {
        if (Ask is { } ask && wallNow >= ask.EndsAt) Answered();
    }

    /// <summary>Forgets everything (a welcome: the server ended any trade of the session before).</summary>
    public void Clear()
    {
        Ask = null;
        Open = null;
        Ended = null;
        Notice = null;
        Version++;
    }

    private void Say(string words)
    {
        Notice = words;
        NoticeVersion++;
        Version++;
    }
}
