using Game.Client.Core;
using Game.Client.Core.Bundle;
using Game.Client.Core.Net;
using Godot;

namespace Game.Client.Scene;

/// <summary>
/// The minimum on-screen display: own HP, SP and EXP, the flash line, a target name, a banner, and one full-screen panel (login, death, stops). T-211
/// replaces the look. It redraws only when a version counter says something changed, so a frame with no news costs integer comparisons.
/// </summary>
public sealed class Hud
{
    private readonly ClientConfig _config;
    private readonly ClientSession? _session;
    private readonly DisplayBundle? _bundle;
    private readonly Label _stats = new();
    private readonly ProgressBar _hpBar = new() { ShowPercentage = false, CustomMinimumSize = new Vector2(220, 14) };
    private readonly ProgressBar _spBar = new() { ShowPercentage = false, CustomMinimumSize = new Vector2(220, 10) };
    private readonly ProgressBar _expBar = new() { ShowPercentage = false, CustomMinimumSize = new Vector2(220, 8) };
    private readonly Label _flash = new() { HorizontalAlignment = HorizontalAlignment.Center };
    private readonly Label _target = new() { HorizontalAlignment = HorizontalAlignment.Center };
    private readonly Label _banner = new() { HorizontalAlignment = HorizontalAlignment.Center };
    private readonly Label _levels = new();
    private readonly Button _logout = new() { Text = UiText.Logout, CustomMinimumSize = new Vector2(120, 64) };
    private readonly ColorRect _panel = new() { Color = new Color(0, 0, 0, 0.72f) };
    private readonly Label _panelTitle = new() { HorizontalAlignment = HorizontalAlignment.Center, AutowrapMode = TextServer.AutowrapMode.WordSmart };
    private readonly LineEdit _nameEdit = new() { CustomMinimumSize = new Vector2(320, 64), Alignment = HorizontalAlignment.Center };
    private readonly Button _panelButton = new() { CustomMinimumSize = new Vector2(320, 64) };
    private Action? _panelAction;
    private int _uiVersion = -1;
    private int _statsVersion = -1;
    private int _connectionVersion = -1;

    /// <summary>Builds the HUD under <paramref name="parent"/>. Without a session, only <see cref="ShowRefusal"/> makes sense.</summary>
    public Hud(Node parent, ClientConfig config, ClientSession? session, DisplayBundle? bundle)
    {
        _config = config;
        _session = session;
        _bundle = bundle;

        var layer = new CanvasLayer();
        parent.AddChild(layer);
        var root = new Control { MouseFilter = Control.MouseFilterEnum.Ignore };
        root.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        layer.AddChild(root);

        var statsBox = new VBoxContainer { Position = new Vector2(16, 12), MouseFilter = Control.MouseFilterEnum.Ignore };
        root.AddChild(statsBox);
        _levels.MouseFilter = Control.MouseFilterEnum.Ignore;
        statsBox.AddChild(_levels);
        statsBox.AddChild(_stats);
        foreach (var bar in new[] { _hpBar, _spBar, _expBar })
        {
            bar.MouseFilter = Control.MouseFilterEnum.Ignore;
            statsBox.AddChild(bar);
        }

        var top = new VBoxContainer { MouseFilter = Control.MouseFilterEnum.Ignore };
        top.SetAnchorsPreset(Control.LayoutPreset.TopWide);
        top.OffsetTop = 12;
        root.AddChild(top);
        top.AddChild(_banner);
        top.AddChild(_target);
        top.AddChild(_flash);

        _logout.SetAnchorsPreset(Control.LayoutPreset.TopRight);
        _logout.OffsetLeft = -136;
        _logout.OffsetRight = -16;
        _logout.OffsetTop = 12;
        _logout.OffsetBottom = 76;
        _logout.Pressed += () => _session?.Logout(Clocks.Wall());
        root.AddChild(_logout);

        _panel.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        root.AddChild(_panel);
        var center = new CenterContainer();
        center.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        _panel.AddChild(center);
        var panelBox = new VBoxContainer { CustomMinimumSize = new Vector2(520, 0) };
        panelBox.AddThemeConstantOverride("separation", 16);
        center.AddChild(panelBox);
        panelBox.AddChild(_panelTitle);
        panelBox.AddChild(_nameEdit);
        panelBox.AddChild(_panelButton);
        _panelButton.Pressed += () => _panelAction?.Invoke();
        _nameEdit.Text = config.DevName;
    }

    /// <summary>The text on screen in the panel, for the scripted run.</summary>
    public string PanelText => _panel.Visible ? _panelTitle.Text : string.Empty;

    /// <summary>True when the panel's button shows, for the scripted run.</summary>
    public bool PanelButtonShown => _panel.Visible && _panelButton.Visible;

    /// <summary>Presses the panel's button, as a tap on it would.</summary>
    public void PressPanelButton() => _panelAction?.Invoke();

    /// <summary>A permanent refusal: a bundle this build cannot use (§5.4). There is no way forward from it except an update.</summary>
    public void ShowRefusal(string text)
    {
        _logout.Visible = false;
        ShowPanel(text, false, null, null);
    }

    /// <summary>Redraws what changed. Allocates only when something did.</summary>
    public void Refresh()
    {
        if (_session is null) return;
        var connection = _session.Connection;
        var ui = _session.Ui;
        var world = _session.World;

        var panelChanged = false;
        if (_connectionVersion != connection.Version)
        {
            _connectionVersion = connection.Version;
            panelChanged = true;
            _banner.Text = connection.Phase switch
            {
                ConnectionPhase.Backoff or ConnectionPhase.Connecting or ConnectionPhase.AwaitingWelcome when connection.Notice != ConnectionNotice.None => UiText.Notice(connection.Notice),
                _ => string.Empty,
            };
            _logout.Visible = connection.Phase == ConnectionPhase.Online;
        }
        if (_uiVersion != ui.Version)
        {
            _uiVersion = ui.Version;
            panelChanged = true;
            _flash.Text = ui.Flash;
            _target.Text = ui.Target is { } id ? _bundle!.NameOf(world.Find(id)?.DisplayKey) : string.Empty;
        }
        if (panelChanged) RefreshPanel();

        if (_statsVersion != world.StatsVersion)
        {
            _statsVersion = world.StatsVersion;
            if (world.Vitals is { } vitals)
            {
                _stats.Text = UiText.Hp(vitals.Hp, vitals.MaxHp) + "   " + UiText.Sp(vitals.Sp, vitals.MaxSp);
                SetBar(_hpBar, vitals.Hp, vitals.MaxHp);
                SetBar(_spBar, vitals.Sp, vitals.MaxSp);
            }
            if (world.Exp is { } exp)
            {
                _levels.Text = UiText.Exp(exp.BaseLevel, exp.BaseExp, exp.BaseNext);
                SetBar(_expBar, exp.BaseExp, exp.BaseNext);
            }
        }
    }

    private void RefreshPanel()
    {
        var session = _session!;
        var connection = session.Connection;
        switch (connection.Phase)
        {
            case ConnectionPhase.LoginScreen:
                ShowPanel(UiText.Notice(connection.Notice), true, UiText.Play, () => session.Login(_nameEdit.Text.Trim(), Clocks.Wall()));
                return;
            case ConnectionPhase.Stopped when connection.Notice == ConnectionNotice.Replaced:
                ShowPanel(UiText.Notice(connection.Notice), false, UiText.BackToLogin, () => session.Login(_nameEdit.Text.Trim(), Clocks.Wall()));
                return;
            case ConnectionPhase.Stopped:
                ShowPanel(UiText.Notice(connection.Notice), false, _config.StoreUrl.Length > 0 ? UiText.GoToStore : null,
                    () => OS.ShellOpen(_config.StoreUrl));
                return;
            case ConnectionPhase.Connecting or ConnectionPhase.AwaitingWelcome when session.World.MapId is null:
                ShowPanel(connection.Notice != ConnectionNotice.None ? UiText.Notice(connection.Notice) : UiText.Connecting, false, null, null);
                return;
        }
        if (session.Ui.Dead && connection.Phase == ConnectionPhase.Online)
        {
            ShowPanel(UiText.YouDied, false, UiText.Respawn, session.Respawn);
            return;
        }
        _panel.Visible = false;
    }

    private void ShowPanel(string title, bool nameField, string? buttonText, Action? action)
    {
        _panelTitle.Text = title;
        _nameEdit.Visible = nameField;
        _panelButton.Visible = buttonText is not null;
        _panelButton.Text = buttonText ?? string.Empty;
        _panelAction = action;
        _panel.Visible = true;
    }

    private static void SetBar(ProgressBar bar, long value, long max)
    {
        bar.MaxValue = max > 0 ? max : 1;
        bar.Value = value;
    }
}

/// <summary>The two clocks (see <see cref="ClientSession"/>).</summary>
public static class Clocks
{
    /// <summary>Seconds on a clock that keeps running while the app is suspended. Godot's tick counter may stop then, which would hide a grace period's end.</summary>
    public static double Wall() => DateTime.UtcNow.Ticks / (double)TimeSpan.TicksPerSecond;

    /// <summary>Seconds on Godot's smooth monotonic clock.</summary>
    public static double View() => Time.GetTicksUsec() / 1_000_000.0;
}
