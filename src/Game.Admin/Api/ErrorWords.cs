using Game.Shared.Vocabulary;

namespace Game.Admin.Api;

/// <summary>Every refusal in words: each "Admin errors" term of the server, and what the panel itself can find wrong with a call.</summary>
public static class ErrorWords
{
    /// <summary>The panel's own: the server did not answer (not reached, or the connection broke).</summary>
    public const string Unreachable = "panel_unreachable";

    /// <summary>The panel's own: no answer within <c>Server:TimeoutMs</c>.</summary>
    public const string NoAnswerInTime = "panel_no_answer";

    /// <summary>The panel's own: an answer that is not the admin API's.</summary>
    public const string BadAnswer = "panel_bad_answer";

    /// <summary>The panel's own: the server's certificate is not trusted, or TLS failed.</summary>
    public const string Untrusted = "panel_untrusted";

    /// <summary>Added when a command's fate is unknown: it is never sent again by itself.</summary>
    public const string MayHaveHappened =
        "The panel does not know whether the server applied the command. It does not send it again: look in the audit log for the request id before you try again.";

    private static readonly IReadOnlyDictionary<string, string> Words = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        [AdminErrors.InvalidRequest] = "The server did not accept the request: a field or search value is missing, malformed or too long.",
        [AdminErrors.Unauthorized] = "The server refused the panel's service credential. Whoever runs the panel must set the same credential on both.",
        [AdminErrors.InvalidStaff] = "The server refused the staff identity of your sign-in (id or name). Ask whoever runs the panel to check the provider's claims.",
        [AdminErrors.UnknownRole] = "The server does not know your staff role. The panel and the server may be different versions.",
        [AdminErrors.Forbidden] = "Your role is not allowed to do this.",
        [AdminErrors.ReasonRequired] = "A reason is required: 1 to 500 characters.",
        [AdminErrors.NotFound] = "The server has no such endpoint or command. The panel and the server may be different versions.",
        [AdminErrors.UnknownTarget] = "There is no such account, character, support request or chat report (it may have been erased).",
        [AdminErrors.RateLimited] = "Too many requests to the server. Wait a moment.",
        [AdminErrors.Timeout] = "The game world did not answer in time. Nothing was changed.",
        [AdminErrors.Unavailable] = "The server's database could not answer now. Nothing was changed.",
        // T-806 and T-901's terms (added when the three branches were merged)
        [AdminErrors.AlreadyBanned] = "The account is banned already. Lift that ban first to set another.",
        [AdminErrors.NotBanned] = "The account has no ban in force.",
        [AdminErrors.AlreadyMuted] = "The account is muted already. Lift that mute first to set another.",
        [AdminErrors.NotMuted] = "The account has no mute in force.",
        [AdminErrors.CharacterBusy] = "Another server holds the character right now. Nothing was changed: try again once it has left the game.",
        [AdminErrors.NeverSaved] = "The character has never been saved, so there is nothing to change yet.",
        [AdminErrors.UnknownItem] = "The game's content has no such item.",
        [AdminErrors.ItemGone] = "The character no longer has that item entry (it may have been used, sold or traded).",
        [AdminErrors.NotEnough] = "The character does not have that many to take.",
        [AdminErrors.InventoryFull] = "The character's inventory has no room for the new entries.",
        [AdminErrors.CurrencyLimit] = "That would take the character's currency over its limit.",
        [AdminErrors.MaintenanceOn] = "This server is in maintenance already.",
        [AdminErrors.MaintenanceOff] = "This server is not in maintenance.",
        [AdminErrors.RequestClosed] = "That support request is closed already.",
        [AdminErrors.AccountDeleting] = "The account is waiting to be erased. Its owner can still keep it by signing in.",
        [AdminErrors.ReportClosed] = "That chat report is closed already.",
        [Unreachable] = "The game server could not be reached. Is it running with its admin API on, and is Server:Url right?",
        [NoAnswerInTime] = "The game server did not answer in time.",
        [BadAnswer] = "The server's answer was not the admin API's. Check that Server:Url points at the admin port.",
        [Untrusted] = "The server's TLS certificate is not trusted, or the secure connection failed. Check Server:CertificatePath and the server's certificate.",
    };

    /// <summary>The words for <paramref name="term"/>; a term the panel does not know (a newer server's) is named.</summary>
    public static string For(string term) => Words.TryGetValue(term, out var words) ? words : $"The server refused: {term}.";

    /// <summary>True when the panel has words of its own for <paramref name="term"/>.</summary>
    public static bool Knows(string term) => Words.ContainsKey(term);
}
