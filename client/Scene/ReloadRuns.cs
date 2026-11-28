using System.Diagnostics;
using Game.Client.Core;
using Game.Client.Core.Net;
using Game.Shared.Protocol;
using Godot;

namespace Game.Client.Scene;

/// <summary>
/// The scripted hot-reload run (T-801), <c>--e2e-reload</c>, against a development server started with <c>--hot-reload</c> on a content copy: enter the world with the dev login,
/// ask the script to change a display name in the copy (<c>@ reload-edit</c>), and wait for the server's <c>content_changed</c>. The new hash must differ from this build's bundle,
/// the HUD's announcement line must say the content changed and the game must be restarted (v1 does not take a bundle while it plays), and the character must still be playing:
/// a reload re-enters it on its cell, it is not put out. Then it walks one cell, to show the server still answers it.
/// </summary>
public sealed class ReloadRuns
{
    private readonly Main _main;
    private readonly Func<Task> _shot;

    /// <summary>Prepares the run.</summary>
    public ReloadRuns(Main main, Func<Task> shot)
    {
        _main = main;
        _shot = shot;
    }

    private ClientSession Session => _main.Session!;

    /// <summary>The run.</summary>
    public async Task RunAsync()
    {
        await FramesAsync(3);
        Session.Login(_main.Config.DevName, Clocks.Wall());
        await UntilAsync(() => Session.Connection.Phase == ConnectionPhase.Online && Session.World.Own is not null && _main.LoadedMap == Session.World.MapId, 20, "entering the world");
        var own = Session.World.OwnId;
        var cell = Session.World.Own!.Cell;
        var ownHash = _main.Bundle!.ContentHash;
        Print($"reload: in the world as {own} on {Session.World.MapId} at {cell}; bundle hash {ownHash}; asking the script to change a display name");

        ContentChangedMessage? changed = null;
        Session.ContentChangedReceived += m => changed = m;
        Print("@ reload-edit");
        await UntilAsync(() => changed is not null, 60, "content_changed from the server");
        Print($"reload: content_changed, new hash {changed!.Content}");
        Check(changed.Content != ownHash, "the new hash differs from this build's bundle");
        await FramesAsync(3);
        var line = _main.Hud!.AnnouncementText;
        Print($"reload: the HUD says \"{line}\"");
        Check(line == UiText.ContentChanged, "the HUD says the content changed and the game must be restarted");
        await _shot();

        // Still playing: the server re-entered the character on its cell (a same-map warp), it did not put it out.
        await SecondsAsync(1);
        Check(Session.Connection.Phase == ConnectionPhase.Online, "still online after the reload");
        Check(Session.World.OwnId == own && Session.World.Own is not null, "the same character, still in the world");
        var target = new Game.Shared.World.GridCell(cell.X + 1, cell.Y);
        if (!_main.Bundle.TryGetMap(Session.World.MapId!, out var map) || map is null || !map.IsWalkable(target.X, target.Y)) target = new Game.Shared.World.GridCell(cell.X - 1, cell.Y);
        Session.TapGround(target, Clocks.View());
        await UntilAsync(() => Session.World.Own is { } me && me.Cell.X == target.X && me.Cell.Y == target.Y && !me.IsWalking, 15, "a walk after the reload");
        Print($"reload: walked to {target} after the reload; the line stays: \"{_main.Hud.AnnouncementText}\"");
        Check(_main.Hud.AnnouncementText == UiText.ContentChanged, "the notice stays until the game is restarted");
    }

    private async Task FramesAsync(int count)
    {
        for (var i = 0; i < count; i++) await _main.ToSignal(_main.GetTree(), SceneTree.SignalName.ProcessFrame);
    }

    private async Task SecondsAsync(double seconds)
    {
        var clock = Stopwatch.StartNew();
        while (clock.Elapsed.TotalSeconds < seconds) await FramesAsync(1);
    }

    private async Task UntilAsync(Func<bool> condition, double seconds, string what)
    {
        var clock = Stopwatch.StartNew();
        while (!condition())
        {
            if (clock.Elapsed.TotalSeconds > seconds)
                throw new TimeoutException($"timed out after {seconds} s waiting for {what}; phase {Session.Connection.Phase}, notice {Session.Connection.Notice}");
            await FramesAsync(1);
        }
    }

    private static void Check(bool condition, string what)
    {
        if (!condition) throw new InvalidOperationException("check failed: " + what);
    }

    private static void Print(string line) => GD.Print("[e2e] " + line);
}
