using Game.Client.Core.Ui;
using Godot;

namespace Game.Client.Scene;

/// <summary>
/// The player's choices on this device, in the engine's per-user storage (<see cref="UserPreferences"/>). It handles those and nothing else: the one place the
/// client writes a file (a test keeps it that way).
/// </summary>
public static class UserPrefsStore
{
    private const string Path = "user://preferences.json";

    /// <summary>Reads the stored choices; defaults if there are none or they are damaged.</summary>
    public static UserPreferences Load() =>
        Godot.FileAccess.FileExists(Path) ? UserPreferences.Parse(Godot.FileAccess.GetFileAsBytes(Path)) : new UserPreferences();

    /// <summary>Stores the choices. A failure is logged and otherwise ignored: a preference is never worth stopping the game for.</summary>
    public static void Save(UserPreferences preferences)
    {
        using var file = Godot.FileAccess.Open(Path, Godot.FileAccess.ModeFlags.Write);
        if (file is null)
        {
            GD.PrintErr($"[client] cannot store preferences: {Godot.FileAccess.GetOpenError()}");
            return;
        }
        file.StoreBuffer(preferences.ToJson());
    }
}
