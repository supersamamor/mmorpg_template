namespace Game.Client.Core.Theme;

/// <summary>
/// The combat effects the client plays, by name (VOCABULARY "Effect names"). A name is not an asset key: the theme manifest's <c>fallbacks</c> table maps each
/// name to a <c>vfx.</c> asset key, so which scene plays is theme data and no asset key is written in code (CLAUDE.md rule 3). A theme that leaves a name out, or
/// whose file is missing, gets the next name in the chain (<see cref="DefaultFallback"/>), then no effect.
/// </summary>
public static class EffectNames
{
    /// <summary>A hit landing.</summary>
    public const string Hit = "effect.hit";

    /// <summary>A critical hit landing.</summary>
    public const string Crit = "effect.crit";

    /// <summary>Something dying.</summary>
    public const string Death = "effect.death";

    /// <summary>The player's character gaining a level.</summary>
    public const string LevelUp = "effect.level_up";

    /// <summary>The name to try when <paramref name="name"/> cannot be shown, or null at the end of the chain.</summary>
    public static string? DefaultFallback(string name) => name switch
    {
        Crit => Hit,
        LevelUp => Crit,
        Death => Hit,
        _ => null,
    };
}
