using Game.Client.Core;
using Game.Client.Core.Theme;
using Godot;

namespace Game.Client.Scene;

/// <summary>
/// Mounts the active theme (ARCHITECTURE §7.7): reads <c>res://themes/&lt;theme&gt;/asset-manifest.json</c> and, behind it, the default theme's. In development
/// <c>res://themes</c> is a directory junction to the repository's <c>themes/</c> (<c>client/mount-themes.ps1</c>); in an export the files are packed with the game.
/// </summary>
public static class ThemeLoader
{
    /// <summary>The theme every other theme falls back to.</summary>
    public const string DefaultTheme = "default";

    private const string ManifestFile = "asset-manifest.json";

    /// <summary>The catalog for the theme named in the settings. Problems are logged once and never stop the client: a theme that cannot be read just means fallbacks.</summary>
    public static ThemeCatalog Load(ClientConfig config)
    {
        var layers = new List<ThemeManifest>();
        AddLayer(layers, config.Theme);
        if (config.Theme != DefaultTheme) AddLayer(layers, DefaultTheme);
        if (layers.Count == 0) GD.PushError("[theme] no theme could be read; every model will be the built-in marker");
        else GD.Print($"[client] theme '{config.Theme}': {layers.Count} manifest(s), {layers[0].Count} keys in the first");
        return new ThemeCatalog(layers, path => ResourceLoader.Exists("res://" + path), message => GD.PushWarning(message));
    }

    private static void AddLayer(List<ThemeManifest> layers, string theme)
    {
        var path = "res://" + ThemeCatalog.PathOf(theme, ManifestFile);
        if (!Godot.FileAccess.FileExists(path))
        {
            GD.PushWarning($"[theme] theme '{theme}' has no {ManifestFile} at {path}");
            return;
        }
        if (ThemeManifest.TryParse(theme, Godot.FileAccess.GetFileAsString(path), out var manifest, out var error)) layers.Add(manifest!);
        else GD.PushWarning($"[theme] {path} is not a usable manifest: {error}");
    }
}
