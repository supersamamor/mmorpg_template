using System;
using System.Collections.Generic;

namespace Game.Client.Core.Ui;

/// <summary>
/// The skill bar's slot text (T-222, ADR-002 rows 217 and 220). A slot shows its number (and while a cooldown runs, the whole seconds left) on the first line and
/// the skill's name below. The name wraps only between words: the font is the largest step, from the slot's size down, at which every word fits the slot's width,
/// so a long name never breaks mid-word. The text is set again only when what it says changes (another skill, or a whole second of cooldown), never every frame.
/// Pure: the width of a word comes in as a function, so it is tested without the engine.
/// </summary>
public static class HotbarText
{
    /// <summary>The text of slot <paramref name="index"/> (0-based): its number, the whole seconds left while <paramref name="secondsLeft"/> is above 0, and the name.</summary>
    public static string Compose(int index, string? name, int secondsLeft)
    {
        var number = (index + 1).ToString(System.Globalization.CultureInfo.InvariantCulture);
        if (name is null) return number;
        var head = secondsLeft > 0 ? number + "  " + UiText.CooldownLeft(secondsLeft) : number;
        return head + "\n" + name;
    }

    /// <summary>Whole seconds of a cooldown as the slot shows them: 2.1 s shows 3, 0 shows nothing.</summary>
    public static int WholeSeconds(double secondsLeft) => secondsLeft > 0 ? (int)Math.Ceiling(secondsLeft) : 0;

    /// <summary>
    /// The largest font size from <paramref name="largest"/> down to <paramref name="smallest"/> at which each word of <paramref name="name"/> is no wider than
    /// <paramref name="available"/>; <paramref name="smallest"/> if none is. <paramref name="width"/> measures a word at a font size.
    /// </summary>
    public static int FitFontSize(string name, Func<string, int, double> width, double available, int largest, int smallest)
    {
        var words = name.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
        for (var size = largest; size > smallest; size--)
        {
            var fits = true;
            foreach (var word in words)
            {
                if (width(word, size) <= available) continue;
                fits = false;
                break;
            }
            if (fits) return size;
        }
        return smallest;
    }
}

/// <summary>
/// Remembers what each skill bar slot shows, so the HUD sets a slot's text only when it would read differently (T-222, ADR-002 row 220): the cooldown sweep moves
/// every frame, the text once a second.
/// </summary>
public sealed class HotbarTextCache
{
    private readonly List<(string? Name, int Seconds, bool Set)> _shown = new List<(string?, int, bool)>();

    /// <summary>True when slot <paramref name="index"/> should get new text for <paramref name="name"/> with <paramref name="seconds"/> whole seconds left; remembers it.</summary>
    public bool Changed(int index, string? name, int seconds)
    {
        while (_shown.Count <= index) _shown.Add((null, 0, false));
        var old = _shown[index];
        if (old.Set && old.Name == name && old.Seconds == seconds) return false;
        _shown[index] = (name, seconds, true);
        return true;
    }

    /// <summary>Forgets everything, so every slot is written again (after a layout or UI scale change).</summary>
    public void Clear() => _shown.Clear();
}
