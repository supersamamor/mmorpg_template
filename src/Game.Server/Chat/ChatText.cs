using System.Globalization;
using System.Text;
using Game.Shared.Vocabulary;

namespace Game.Server.Chat;

/// <summary>What became of a line's text: the cleaned text, or a "Chat reasons" term (<c>empty</c>, <c>too_long</c>).</summary>
public readonly record struct CleanText(string? Text, string? Refusal);

/// <summary>
/// The text rules of a chat line (T-901). Characters that change how text is shown without being text are <b>removed</b>, not refused: most come in by accident (a pasted line), and once
/// they are gone a trick no longer works, so there is nothing to refuse. In order:
/// <list type="number">
/// <item>NFC normalisation (one form for what looks the same); text that is not well-formed UTF-16 loses the broken halves.</item>
/// <item>Removed: every format character (Unicode category Cf: zero-width space, joiners and non-joiners, the bidirectional marks, embeddings, overrides and isolates such as U+202E, the BOM,
/// word joiner, invisible operators, tags, soft hyphen), private use, unassigned code points and the "blank letters" that draw nothing (Hangul fillers U+115F, U+1160, U+3164, U+FFA0, and
/// the blank Braille pattern U+2800).</item>
/// <item>Line breaks, tabs, other control characters and every kind of space become one plain space; runs of spaces become one.</item>
/// <item>Combining marks: at most <see cref="ChatOptions.MarksPerLetter"/> after each letter, none at the start.</item>
/// <item>Trimmed; then empty is <c>empty</c>, longer than <see cref="ChatOptions.MaxLength"/> UTF-16 code units is <c>too_long</c> (never cut: the player sees why and decides).</item>
/// </list>
/// Right-to-left letters themselves stay (Arabic and Hebrew are languages); with the controls gone a line cannot reorder anything outside itself, and the client draws the name apart from
/// the text, so a line cannot change how the name looks. Pure and thread-safe.
/// </summary>
public static class ChatText
{
    /// <summary>Cleans <paramref name="raw"/> by the rules above.</summary>
    public static CleanText Clean(string raw, int maxLength, int marksPerLetter)
    {
        string normalized;
        try
        {
            normalized = raw.Normalize(NormalizationForm.FormC);
        }
        catch (ArgumentException)
        {
            normalized = raw;   // broken surrogates: the loop below drops them
        }
        var builder = new StringBuilder(Math.Min(normalized.Length, maxLength * 2 + 16));
        var marks = 0;
        var afterLetter = false;
        var pendingSpace = false;
        for (var i = 0; i < normalized.Length;)
        {
            if (Rune.DecodeFromUtf16(normalized.AsSpan(i), out var rune, out var used) != System.Buffers.OperationStatus.Done)
            {
                i += Math.Max(1, used);   // a lone surrogate
                continue;
            }
            i += used;
            switch (Kind(rune))
            {
                case Keep.Drop:
                    continue;
                case Keep.Space:
                    pendingSpace = builder.Length > 0;
                    afterLetter = false;
                    marks = 0;
                    continue;
                case Keep.Mark:
                    if (!afterLetter || marks >= marksPerLetter) continue;
                    marks++;
                    builder.Append(rune.ToString());
                    continue;
                default:
                    if (pendingSpace) builder.Append(' ');
                    pendingSpace = false;
                    afterLetter = true;
                    marks = 0;
                    builder.Append(rune.ToString());
                    if (builder.Length > maxLength * 4 + 64) return new CleanText(null, ChatReasons.TooLong);   // far too long already: no need to read on
                    continue;
            }
        }
        var text = builder.ToString();
        if (text.Length == 0) return new CleanText(null, ChatReasons.Empty);
        if (text.Length > maxLength) return new CleanText(null, ChatReasons.TooLong);
        return new CleanText(text, null);
    }

    private enum Keep
    {
        Text,
        Space,
        Mark,
        Drop,
    }

    private static Keep Kind(Rune rune)
    {
        switch (rune.Value)
        {
            case 0x115F or 0x1160 or 0x3164 or 0xFFA0 or 0x2800:   // letters (or a symbol) that draw nothing: a line of them looks empty, a name of them looks like nobody
                return Keep.Drop;
            case 0x034F:   // the combining grapheme joiner: invisible
                return Keep.Drop;
        }
        return Rune.GetUnicodeCategory(rune) switch
        {
            UnicodeCategory.Format or UnicodeCategory.PrivateUse or UnicodeCategory.OtherNotAssigned or UnicodeCategory.Surrogate => Keep.Drop,
            UnicodeCategory.Control or UnicodeCategory.SpaceSeparator or UnicodeCategory.LineSeparator or UnicodeCategory.ParagraphSeparator => Keep.Space,
            UnicodeCategory.NonSpacingMark or UnicodeCategory.EnclosingMark or UnicodeCategory.SpacingCombiningMark => Keep.Mark,
            _ => Keep.Text,
        };
    }
}
