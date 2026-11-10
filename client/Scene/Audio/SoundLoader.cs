using Game.Client.Core;
using Game.Client.Core.Audio;
using Game.Client.Core.Theme;
using Godot;

namespace Game.Client.Scene.Audio;

/// <summary>Reads the sound tables of the active theme and the default theme (ARCHITECTURE §7.7) into a <see cref="SoundCatalog"/>. Problems are logged once and never stop the client.</summary>
public static class SoundLoader
{
    /// <summary>The catalog for the theme named in the settings.</summary>
    public static SoundCatalog Load(ClientConfig config)
    {
        var layers = new List<SoundTable>();
        AddLayer(layers, config.Theme);
        if (config.Theme != ThemeLoader.DefaultTheme) AddLayer(layers, ThemeLoader.DefaultTheme);
        return new SoundCatalog(layers, path => ResourceLoader.Exists("res://" + path), message => GD.PushWarning(message));
    }

    private static void AddLayer(List<SoundTable> layers, string theme)
    {
        var path = "res://" + ThemeCatalog.PathOf(theme, "asset-manifest.json");
        if (!Godot.FileAccess.FileExists(path)) return; // ThemeLoader already warned about it
        if (SoundTable.TryParse(theme, Godot.FileAccess.GetFileAsString(path), out var table, out var error)) layers.Add(table!);
        else GD.PushWarning($"[audio] {path} has no usable sound table: {error}");
    }
}
