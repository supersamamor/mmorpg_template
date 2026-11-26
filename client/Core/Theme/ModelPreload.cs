using System;
using System.Collections.Generic;
using Game.Client.Core.Bundle;

namespace Game.Client.Core.Theme;

/// <summary>
/// Which of the bundle's models start loading at start (T-711, ADR-002 row 314). A job's model is only drawn for a character whose body is missing (row 311), so while
/// characters are drawn by their looks and every gender's body is in the theme, job models are left out: they load the first time one is needed, on a worker like any
/// model, with the character hidden until it is in (no frame waits for the disk). Everything else (monsters, NPCs, ground items) is preloaded as before.
/// </summary>
public static class ModelPreload
{
    /// <summary>
    /// The model keys to preload. <paramref name="drawAppearance"/> is the setting <c>draw_appearance</c>; <paramref name="hasBody"/> says whether a body's asset key
    /// resolves to a file in the theme chain.
    /// </summary>
    public static IReadOnlyList<string> Keys(DisplayBundle bundle, bool drawAppearance, Func<string, bool> hasBody)
    {
        var withJobs = !JobModelsUnneeded(bundle, drawAppearance, hasBody);
        var keys = new List<string>();
        foreach (var key in bundle.ModelKeys)
            if (withJobs || !bundle.IsJobModelOnly(key)) keys.Add(key);
        return keys;
    }

    /// <summary>True when no character will be drawn by its job's model: looks are drawn, the bundle has genders, and every gender's body is there.</summary>
    public static bool JobModelsUnneeded(DisplayBundle bundle, bool drawAppearance, Func<string, bool> hasBody)
    {
        if (!drawAppearance || bundle.Appearance is not { } looks || looks.Genders.Count == 0) return false;
        foreach (var gender in looks.Genders)
            if (!hasBody(gender.BodyModelAssetKey)) return false;
        return true;
    }
}
