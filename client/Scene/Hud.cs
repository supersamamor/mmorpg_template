using System.Collections.Generic;
using Game.Client.Core;
using Game.Client.Core.Bundle;
using Game.Client.Core.Net;
using Game.Client.Core.Ui;
using Game.Client.Core.Audio;
using Game.Client.Scene.Audio;
using Game.Shared.Vocabulary;
using Game.Shared.Protocol;
using Godot;

namespace Game.Client.Scene;

/// <summary>
/// The mobile UI shell (T-211, ARCHITECTURE §7.4): own HP, SP and EXP, the target frame, the connection indicator, the hotbar, the long-press info panel, and
/// one full-screen panel (login, death, stops, refusals). Everything sits inside the device's safe area; sizes are in dp times the UI scale, and touch targets
/// never fall below the platform's minimum. Words come from <see cref="UiText"/> or the bundle, never from here. It redraws only when a version counter says
/// something changed, so a frame with no news costs integer comparisons.
/// </summary>
public sealed class Hud
{
    // Layout in dp at UI scale 1 (physical dp after the scale is applied).
    private const double EdgeMarginDp = 8;
    private const double LeftColumnDp = 150;
    private const double RightColumnDp = 104;
    private const double BodyFontDp = 16;
    private const double ButtonDp = 48;
    private const double PanelWidthDp = 420;
    private const double InfoWidthDp = 150;

    private readonly ClientConfig _config;
    private readonly ClientSession? _session;
    private readonly DisplayBundle? _bundle;
    private readonly Node _host;
    private readonly Viewport _viewport;
    private readonly Window _window;
    private readonly UserPreferences _prefs;
    private readonly Theme _theme = new();
    private readonly CanvasLayer _layer = new();
    private readonly Control _root = new() { MouseFilter = Control.MouseFilterEnum.Ignore };
    private readonly Control _safe = new() { MouseFilter = Control.MouseFilterEnum.Ignore };

    private readonly HBoxContainer _topRow = new() { MouseFilter = Control.MouseFilterEnum.Ignore };
    private readonly VBoxContainer _left = new() { MouseFilter = Control.MouseFilterEnum.Ignore };
    private readonly VBoxContainer _center = new() { MouseFilter = Control.MouseFilterEnum.Ignore, SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
    private readonly VBoxContainer _right = new() { MouseFilter = Control.MouseFilterEnum.Ignore };
    private readonly Label _levels = Wrapped();
    private readonly Label _hpText = Wrapped();
    private readonly Label _spText = Wrapped();
    private readonly ProgressBar _hpBar = Bar(new Color(0.35f, 0.8f, 0.4f));
    private readonly ProgressBar _spBar = Bar(new Color(0.35f, 0.6f, 0.95f));
    private readonly ProgressBar _expBar = Bar(new Color(0.95f, 0.8f, 0.3f));
    private readonly Label _banner = Wrapped(HorizontalAlignment.Center);
    private readonly PanelContainer _targetFrame = new() { MouseFilter = Control.MouseFilterEnum.Ignore, Visible = false, SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
    private readonly Label _target = Wrapped(HorizontalAlignment.Center);
    private readonly Label _flash = Wrapped(HorizontalAlignment.Center);
    private readonly Label _connection = Wrapped(HorizontalAlignment.Right);
    private readonly Button _uiSize = new();
    private readonly Button _logout = new() { Text = UiText.Logout };
    private readonly Button _soundButton = new() { Text = UiText.Sound };
    private readonly CenterContainer _soundCenter = new() { MouseFilter = Control.MouseFilterEnum.Ignore, Visible = false };
    private readonly PanelContainer _soundPanel = new() { MouseFilter = Control.MouseFilterEnum.Stop };
    private readonly GridContainer _soundGrid = new() { Columns = 2, MouseFilter = Control.MouseFilterEnum.Ignore };
    private readonly Button _soundMute = new();
    private readonly Button _soundClose = new() { Text = UiText.Close };
    private readonly Button[] _soundBus = new Button[BusMixer.Buses.Length];

    private readonly VBoxContainer _bottom = new() { MouseFilter = Control.MouseFilterEnum.Ignore };
    private readonly GridContainer _hotbar = new() { MouseFilter = Control.MouseFilterEnum.Ignore };
    private readonly List<Button> _slots = new();
    private readonly HotbarModel? _hotbarModel;

    private readonly PanelContainer _info = new() { MouseFilter = Control.MouseFilterEnum.Ignore, Visible = false };
    private readonly Label _infoTitle = Wrapped();
    private readonly Label _infoSubtitle = Wrapped();

    private readonly ColorRect _panel = new() { Color = new Color(0, 0, 0, 0.72f) };
    private readonly CenterContainer _panelCenter = new() { MouseFilter = Control.MouseFilterEnum.Ignore };
    private readonly VBoxContainer _panelBox = new() { MouseFilter = Control.MouseFilterEnum.Ignore };
    private readonly Label _panelTitle = Wrapped(HorizontalAlignment.Center);
    private readonly LineEdit _nameEdit = new() { Alignment = HorizontalAlignment.Center };
    private readonly Button _panelButton = new();
    private readonly List<Control> _interactive = new();

    private Action? _panelAction;
    private int _uiVersion = -1;
    private int _statsVersion = -1;
    private int _connectionVersion = -1;
    private int _hotbarVersion = -1;
    private int _infoToken;
    private bool _infoPlacePending;
    private int _infoPlaceFrames;
    private Vector2 _infoAnchor;
    private ScreenMetrics _metrics;
    private bool _hasMetrics;
    private double _scale = 1;
    private Vector2 _lastCanvas;
    private Vector2I _lastWindow;
    private Rect2I _lastSafe;

    /// <summary>Builds the HUD under <paramref name="parent"/>. Without a session, only <see cref="ShowRefusal"/> makes sense.</summary>
    public Hud(Node parent, ClientConfig config, ClientSession? session, DisplayBundle? bundle)
    {
        _config = config;
        _session = session;
        _bundle = bundle;
        _host = parent;
        _viewport = parent.GetViewport();
        _window = parent.GetWindow();
        _hotbarModel = session?.Hotbar;
        _prefs = UserPrefsStore.Load();
        _scale = _prefs.EffectiveUiScale(config);

        parent.AddChild(_layer);
        _root.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        _root.Theme = _theme;
        _layer.AddChild(_root);
        _root.AddChild(_safe);

        _left.AddChild(_levels);
        _left.AddChild(_hpText);
        _left.AddChild(_hpBar);
        _left.AddChild(_spText);
        _left.AddChild(_spBar);
        _left.AddChild(_expBar);

        _banner.Visible = false;
        _flash.Visible = false;
        _targetFrame.AddChild(_target);
        _center.AddChild(_banner);
        _center.AddChild(_targetFrame);

        _right.AddChild(_connection);
        _right.AddChild(_uiSize);
        _right.AddChild(_soundButton);
        _soundButton.SizeFlagsHorizontal = Control.SizeFlags.ShrinkEnd;
        _soundButton.Pressed += () => { Tap(); SetSoundPanel(!_soundCenter.Visible); };
        _right.AddChild(_logout);
        _logout.SizeFlagsHorizontal = Control.SizeFlags.ShrinkEnd;
        _uiSize.SizeFlagsHorizontal = Control.SizeFlags.ShrinkEnd;
        _uiSize.Pressed += () => { Tap(); CycleScale(); };
        _logout.Pressed += () => { Tap(); _session?.Logout(Clocks.Wall()); };
        _logout.Visible = false;

        _topRow.SetAnchorsPreset(Control.LayoutPreset.TopWide);
        _topRow.AddChild(_left);
        _topRow.AddChild(_center);
        _topRow.AddChild(_right);
        _safe.AddChild(_topRow);
        BuildSoundPanel();

        // The bottom edge: the one-line flash above the hotbar, both centred, with the whole safe width to wrap in.
        _bottom.AnchorLeft = 0;
        _bottom.AnchorRight = 1;
        _bottom.AnchorTop = 1;
        _bottom.AnchorBottom = 1;
        _bottom.GrowVertical = Control.GrowDirection.Begin;
        _bottom.AddChild(_flash);
        _hotbar.SizeFlagsHorizontal = Control.SizeFlags.ShrinkCenter;
        _bottom.AddChild(_hotbar);
        _safe.AddChild(_bottom);
        for (var i = 0; _hotbarModel is not null && i < _hotbarModel.SlotCount; i++)
        {
            var slot = new Button { ToggleMode = false, AutowrapMode = TextServer.AutowrapMode.WordSmart, ClipText = false };
            var index = i;
            slot.Pressed += () => OnSlotPressed(index, slot);
            _slots.Add(slot);
            _hotbar.AddChild(slot);
            _interactive.Add(slot);
        }

        _infoTitle.HorizontalAlignment = HorizontalAlignment.Left;
        var infoBox = new VBoxContainer { MouseFilter = Control.MouseFilterEnum.Ignore };
        infoBox.AddChild(_infoTitle);
        infoBox.AddChild(_infoSubtitle);
        _info.AddChild(infoBox);
        _safe.AddChild(_info);

        _panel.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        _root.AddChild(_panel);
        _panel.AddChild(_panelCenter);
        _panelCenter.AddChild(_panelBox);
        _panelBox.AddChild(_panelTitle);
        _panelBox.AddChild(_nameEdit);
        _panelBox.AddChild(_panelButton);
        _panelButton.Pressed += () => _panelAction?.Invoke();
        _nameEdit.Text = config.DevName;
        _interactive.Add(_logout);
        _interactive.Add(_uiSize);
        _interactive.Add(_soundButton);
        _interactive.Add(_nameEdit);
        _interactive.Add(_panelButton);

        ApplyLayout();
        _viewport.SizeChanged += ApplyLayout;
    }

    /// <summary>The text on screen in the panel, for the scripted run.</summary>
    public string PanelText => _panel.Visible ? _panelTitle.Text : string.Empty;

    /// <summary>True when the panel's button shows, for the scripted run.</summary>
    public bool PanelButtonShown => _panel.Visible && _panelButton.Visible;

    /// <summary>The metrics the layout was last made for.</summary>
    public ScreenMetrics Metrics => _metrics;

    /// <summary>The UI scale in use.</summary>
    public double Scale => _scale;

    /// <summary>Canvas units in one dp, for the input code's thresholds.</summary>
    public double UnitsPerDp => _hasMetrics ? _metrics.UnitsPerDp : 1;

    /// <summary>The info panel shows (for the scripted run).</summary>
    public bool InfoShown => _info.Visible;

    /// <summary>The info panel's two lines (for the scripted run).</summary>
    public string InfoText => _infoTitle.Text + "|" + _infoSubtitle.Text;

    /// <summary>The info panel's size now (for the scripted run).</summary>
    public Vector2 InfoSize => _info.Size;

    /// <summary>The slot buttons, in order (for the scripted run).</summary>
    public IReadOnlyList<Button> HotbarSlots => _slots;

    /// <summary>The size button and the logout button, for the scripted run.</summary>
    public Button UiSizeButton => _uiSize;

    /// <summary>How many times a hotbar slot was pressed, and what the last press came to (for the scripted run).</summary>
    public int HotbarPresses { get; private set; }

    /// <summary>What the last hotbar press came to.</summary>
    public HotbarPress LastHotbarPress { get; private set; }

    /// <summary>How many nodes the HUD is made of (for measuring).</summary>
    public int NodeCount
    {
        get
        {
            var count = 0;
            foreach (var _ in Walk(_layer)) count++;
            return count;
        }
    }

    /// <summary>Fills the HUD with the longest words it has to show, so a layout check sees the worst case. <see cref="ForceRefresh"/> takes them away.</summary>
    public void ShowSamples(string longName)
    {
        SetText(_banner, UiText.Notice(ConnectionNotice.Reconnecting));
        SetText(_flash, UiText.HitYou(longName, 123456, HitResults.Crit));
        _target.Text = longName;
        _targetFrame.Visible = true;
        _levels.Text = UiText.Exp(99, 123456789, 987654321);
        _hpText.Text = UiText.Hp(99999, 99999);
        _spText.Text = UiText.Sp(99999, 99999);
        SetText(_connection, UiText.Connection(ConnectionPhase.Connecting));
        _logout.Visible = true;
        ShowInfo(longName, UiText.EntityKind(EntityKinds.Item, true), new Vector2((float)_metrics.CanvasWidth, (float)_metrics.CanvasHeight));
    }

    /// <summary>Draws everything again from the model on the next <see cref="Refresh"/>.</summary>
    public void ForceRefresh()
    {
        _uiVersion = -1;
        _statsVersion = -1;
        _connectionVersion = -1;
        _hotbarVersion = -1;
    }

    /// <summary>Presses the panel's button, as a tap on it would.</summary>
    public void PressPanelButton() => _panelAction?.Invoke();

    /// <summary>Shows or hides the whole HUD (only to measure what it costs).</summary>
    public void SetVisible(bool visible) => _layer.Visible = visible;

    /// <summary>A permanent refusal: a bundle this build cannot use (§5.4). There is no way forward from it except an update.</summary>
    public void ShowRefusal(string text)
    {
        _logout.Visible = false;
        ShowPanel(text, false, null, null);
    }

    /// <summary>
    /// True if a canvas position is over something that takes touches (a button, a field, or the full-screen panel). Such a touch never reaches the world.
    /// </summary>
    public bool Blocks(Vector2 position)
    {
        if (_panel.Visible) return true;
        for (var i = 0; i < _interactive.Count; i++)
        {
            var control = _interactive[i];
            if (control.IsVisibleInTree() && control.GetGlobalRect().HasPoint(position)) return true;
        }
        return false;
    }

    /// <summary>Shows the info panel near <paramref name="at"/> (a canvas position), kept inside the safe area, for a few seconds.</summary>
    public void ShowInfo(string title, string subtitle, Vector2 at)
    {
        _infoTitle.Text = title;
        _infoSubtitle.Text = subtitle;
        _infoSubtitle.Visible = subtitle.Length > 0;
        // Its height is only known once the containers have wrapped the text at this width, so it is placed on the next frame (Refresh), invisible until then.
        _info.ResetSize();
        _info.Modulate = new Color(1, 1, 1, 0);
        _info.Visible = true;
        _infoAnchor = at;
        _infoPlacePending = true;
        _infoPlaceFrames = 0;

        var token = ++_infoToken;
        _host.GetTree().CreateTimer(_config.InfoPanelSeconds).Timeout += () =>
        {
            if (token == _infoToken) _info.Visible = false;
        };
    }

    private void PlaceInfo()
    {
        // The first frame lets the labels wrap, the second shrinks the panel to the wrapped text; then it can be placed (and shown).
        if (_infoPlaceFrames++ == 0)
        {
            _info.ResetSize();
            return;
        }
        _infoPlacePending = false;
        var size = _info.Size;
        var safe = _metrics.Safe;
        var gap = (float)(EdgeMarginDp * _scale * UnitsPerDp);
        var x = Mathf.Clamp(_infoAnchor.X + gap - (float)safe.X, 0, Mathf.Max(0, (float)safe.Width - size.X));
        var y = Mathf.Clamp(_infoAnchor.Y - size.Y - gap - (float)safe.Y, 0, Mathf.Max(0, (float)safe.Height - size.Y));
        _info.Position = new Vector2(x, y);
        _info.Modulate = Colors.White;
    }

    /// <summary>Hides the info panel (any touch elsewhere does).</summary>
    public void HideInfo()
    {
        if (!_info.Visible) return;
        _infoToken++;
        _info.Visible = false;
    }

    /// <summary>The audio system, whose volumes the sound panel sets. Null until the world exists.</summary>
    public AudioDriver? Audio { get; set; }

    /// <summary>The sound panel is open.</summary>
    public bool SoundPanelShown => _soundCenter.Visible;

    /// <summary>Opens or closes the sound panel.</summary>
    public void SetSoundPanel(bool open)
    {
        _soundCenter.Visible = open;
        RefreshSoundButtons();
    }

    /// <summary>The sound panel's buttons: the mute switch and one volume button per bus (for the scripted run).</summary>
    public IReadOnlyList<Button> SoundButtons => new List<Button>(_soundBus) { _soundMute };

    private void Tap() => Audio?.PlayUi(AudioCategories.UiTap);

    private void BuildSoundPanel()
    {
        _soundCenter.SetAnchorsPreset(Control.LayoutPreset.TopLeft);
        _soundCenter.AddChild(_soundPanel);
        _soundPanel.AddChild(_soundGrid);
        _safe.AddChild(_soundCenter);
        _soundGrid.AddChild(_soundMute);
        _soundGrid.AddChild(_soundClose);
        _soundMute.Pressed += () =>
        {
            Tap();
            if (Audio is null) return;
            Audio.Director.Mixer.Muted = !Audio.Director.Mixer.Muted;
            Audio.SavePreferences();
            RefreshSoundButtons();
        };
        _soundClose.Pressed += () => { Tap(); SetSoundPanel(false); };
        var names = new[] { "Music", "Effects", "Interface", "Ambience" };
        for (var i = 0; i < _soundBus.Length; i++)
        {
            var bus = BusMixer.Buses[i];
            var name = names[i];
            var button = _soundBus[i] = new Button();
            _soundGrid.AddChild(button);
            _interactive.Add(button);
            button.Pressed += () =>
            {
                if (Audio is null) return;
                var mixer = Audio.Director.Mixer;
                mixer.SetVolume(bus, NextVolume(mixer.Volume(bus)));
                Audio.SavePreferences();
                RefreshSoundButtons();
                Tap();
            };
            button.Text = UiText.BusVolume(name, 1);
            button.SetMeta("bus_name", name);
        }
        _interactive.Add(_soundMute);
        _interactive.Add(_soundPanel);
        _interactive.Add(_soundClose);
    }

    /// <summary>The next step of a volume button: 0, 25, 50, 75, 100 per cent, then back to 0.</summary>
    public static double NextVolume(double volume) => volume >= 0.99 ? 0 : Math.Floor(volume * 4 + 0.001) / 4 + 0.25;

    private void RefreshSoundButtons()
    {
        if (Audio is null) return;
        var mixer = Audio.Director.Mixer;
        _soundMute.Text = UiText.Mute(mixer.Muted);
        for (var i = 0; i < _soundBus.Length; i++) _soundBus[i].Text = UiText.BusVolume((string)_soundBus[i].GetMeta("bus_name"), mixer.Volume(BusMixer.Buses[i]));
    }

    /// <summary>The player cycles the UI size: the next step, stored in the user's preferences.</summary>
    public void CycleScale() => SetScale(UiScale.Next(_scale, _config.UiScaleMin, _config.UiScaleMax, _config.UiScaleStep), true);

    /// <summary>Uses a UI scale (clamped to the configured range); stores it as the player's choice when <paramref name="remember"/> is set.</summary>
    public void SetScale(double scale, bool remember)
    {
        _scale = UiScale.Clamp(scale, _config.UiScaleMin, _config.UiScaleMax, _config.UiScaleDefault);
        if (remember)
        {
            _prefs.UiScale = _scale;
            UserPrefsStore.Save(_prefs);
        }
        ApplyLayout();
    }

    /// <summary>Lays everything out for the screen the engine reports now.</summary>
    public void ApplyLayout()
    {
        _lastCanvas = _viewport.GetVisibleRect().Size;
        _lastWindow = _window.Size;
        _lastSafe = ScreenProbe.OsSafeArea();
        ApplyMetrics(ScreenProbe.Read(_viewport, _window, _config), _scale);
    }

    /// <summary>Lays everything out for given metrics and scale (the scripted run tries many screens this way). Sizes are dp times the scale.</summary>
    public void ApplyMetrics(ScreenMetrics metrics, double scale)
    {
        _metrics = metrics;
        _hasMetrics = true;
        _scale = scale;
        var unit = metrics.UnitsPerDp;
        float Dp(double dp) => (float)(dp * scale * unit);
        float Target(double dp) => (float)(TouchTargetRules.TargetDp(metrics.Platform, dp, scale) * unit);

        _theme.DefaultFontSize = Mathf.Max(8, Mathf.RoundToInt(Dp(BodyFontDp)));
        var safe = metrics.Safe;
        _safe.Position = new Vector2((float)safe.X, (float)safe.Y);
        _safe.Size = new Vector2((float)safe.Width, (float)safe.Height);

        var margin = Dp(EdgeMarginDp);
        _topRow.OffsetLeft = margin;
        _topRow.OffsetRight = -margin;
        _topRow.OffsetTop = margin;
        _topRow.AddThemeConstantOverride("separation", Mathf.RoundToInt(margin));
        _left.CustomMinimumSize = new Vector2(Dp(LeftColumnDp), 0);
        _right.CustomMinimumSize = new Vector2(Dp(RightColumnDp), 0);
        _left.AddThemeConstantOverride("separation", Mathf.RoundToInt(Dp(2)));
        _right.AddThemeConstantOverride("separation", Mathf.RoundToInt(Dp(4)));
        _center.AddThemeConstantOverride("separation", Mathf.RoundToInt(Dp(4)));
        _hpBar.CustomMinimumSize = new Vector2(0, Dp(10));
        _spBar.CustomMinimumSize = new Vector2(0, Dp(8));
        _expBar.CustomMinimumSize = new Vector2(0, Dp(6));

        var buttonHeight = Target(ButtonDp);
        _logout.CustomMinimumSize = new Vector2(Dp(RightColumnDp), buttonHeight);
        _uiSize.CustomMinimumSize = new Vector2(Dp(RightColumnDp), buttonHeight);
        _soundButton.CustomMinimumSize = new Vector2(Dp(RightColumnDp), buttonHeight);
        _soundGrid.AddThemeConstantOverride("h_separation", Mathf.RoundToInt(Dp(8)));
        _soundGrid.AddThemeConstantOverride("v_separation", Mathf.RoundToInt(Dp(8)));
        _soundCenter.Position = Vector2.Zero; // inside _safe, which is already at the safe area
        _soundCenter.Size = new Vector2((float)safe.Width, (float)safe.Height);
        foreach (var button in _soundGrid.GetChildren()) ((Control)button).CustomMinimumSize = new Vector2(Dp(140), buttonHeight);
        _uiSize.Text = UiText.UiSize(scale);

        // The hotbar: physical dp, so its slots are never below the platform's smallest target, and a narrow screen takes more rows.
        if (_hotbarModel is not null)
        {
            var grid = HotbarLayout.Compute(_hotbarModel.SlotCount, _config.HotbarRows, _config.HotbarSlotDp * scale, TouchTargetRules.MinTarget(metrics.Platform),
                _config.HotbarGapDp * scale, metrics.SafeWidthDp - 2 * EdgeMarginDp * scale);
            _hotbar.Columns = Mathf.Max(1, grid.Columns);
            _hotbar.AddThemeConstantOverride("h_separation", Mathf.RoundToInt(Dp(_config.HotbarGapDp)));
            _hotbar.AddThemeConstantOverride("v_separation", Mathf.RoundToInt(Dp(_config.HotbarGapDp)));
            var side = (float)(grid.SlotDp * unit);
            for (var i = 0; i < _slots.Count; i++) _slots[i].CustomMinimumSize = new Vector2(side, side);
        }

        _bottom.OffsetLeft = margin;
        _bottom.OffsetRight = -margin;
        _bottom.OffsetBottom = -margin;
        _bottom.AddThemeConstantOverride("separation", Mathf.RoundToInt(Dp(4)));

        // The wrap width of the info panel's text is fixed up front, so its height is right the first time it is laid out.
        _infoTitle.CustomMinimumSize = new Vector2(Dp(InfoWidthDp), 0);
        _infoSubtitle.CustomMinimumSize = new Vector2(Dp(InfoWidthDp), 0);

        var panelRect = _panelCenter;
        panelRect.Position = new Vector2((float)safe.X, (float)safe.Y);
        panelRect.Size = new Vector2((float)safe.Width, (float)safe.Height);
        var panelWidth = Mathf.Min(Dp(PanelWidthDp), (float)safe.Width - 2 * margin);
        _panelBox.CustomMinimumSize = new Vector2(panelWidth, 0);
        _panelBox.AddThemeConstantOverride("separation", Mathf.RoundToInt(Dp(12)));
        _nameEdit.CustomMinimumSize = new Vector2(0, Target(56));
        _panelButton.CustomMinimumSize = new Vector2(0, Target(56));

        _info.Visible = false;
        _hotbarVersion = -1;
    }

    /// <summary>Redraws what changed. Allocates only when something did.</summary>
    public void Refresh()
    {
        // The safe area can change without the window doing so (turning the phone over, left to right). A phone only: a desktop's is its monitor.
        if (_viewport.GetVisibleRect().Size != _lastCanvas || _window.Size != _lastWindow || ScreenProbe.OsSafeArea() != _lastSafe) ApplyLayout();
        if (_infoPlacePending) PlaceInfo();
        if (_session is null) return;
        var connection = _session.Connection;
        var ui = _session.Ui;
        var world = _session.World;

        var panelChanged = false;
        if (_connectionVersion != connection.Version)
        {
            _connectionVersion = connection.Version;
            panelChanged = true;
            SetText(_banner, connection.Phase switch
            {
                ConnectionPhase.Backoff or ConnectionPhase.Connecting or ConnectionPhase.AwaitingWelcome when connection.Notice != ConnectionNotice.None => UiText.Notice(connection.Notice),
                _ => string.Empty,
            });
            SetText(_connection, UiText.Connection(connection.Phase));
            _connection.AddThemeColorOverride("font_color", connection.Phase == ConnectionPhase.Online ? new Color(0.55f, 0.9f, 0.55f) : new Color(1f, 0.8f, 0.4f));
            _logout.Visible = connection.Phase == ConnectionPhase.Online;
        }
        if (_uiVersion != ui.Version)
        {
            _uiVersion = ui.Version;
            panelChanged = true;
            SetText(_flash, ui.Flash);
            var targetName = ui.Target is { } id ? _bundle!.NameOf(world.Find(id)?.DisplayKey) : string.Empty;
            _target.Text = targetName;
            _targetFrame.Visible = targetName.Length > 0;
        }
        if (panelChanged) RefreshPanel();

        if (_statsVersion != world.StatsVersion)
        {
            _statsVersion = world.StatsVersion;
            if (world.Vitals is { } vitals)
            {
                _hpText.Text = UiText.Hp(vitals.Hp, vitals.MaxHp);
                _spText.Text = UiText.Sp(vitals.Sp, vitals.MaxSp);
                SetBar(_hpBar, vitals.Hp, vitals.MaxHp);
                SetBar(_spBar, vitals.Sp, vitals.MaxSp);
            }
            if (world.Exp is { } exp)
            {
                _levels.Text = UiText.Exp(exp.BaseLevel, exp.BaseExp, exp.BaseNext);
                SetBar(_expBar, exp.BaseExp, exp.BaseNext);
            }
        }

        if (_hotbarModel is not null && _hotbarVersion != _hotbarModel.Version) RefreshHotbar();
    }

    /// <summary>
    /// What does not fit: controls that stick out of the safe area, or are smaller than the text or children inside them need, and touch targets under the
    /// platform's minimum. Empty when all is well. Call it a frame after a layout, so the containers have sorted themselves.
    /// </summary>
    public List<string> FindProblems()
    {
        var problems = new List<string>();
        var safe = _metrics.Safe;
        var area = new Rect2((float)safe.X - 0.6f, (float)safe.Y - 0.6f, (float)safe.Width + 1.2f, (float)safe.Height + 1.2f);
        var minimum = TouchTargetRules.MinTarget(_metrics.Platform) * _metrics.UnitsPerDp - 0.6;
        foreach (var node in Walk(_root))
        {
            if (node is not Control control || !control.IsVisibleInTree() || control == _root || control == _safe || control == _panel || control == _panelCenter) continue;
            var rect = control.GetGlobalRect();
            var name = $"{control.GetType().Name}\"{(control as Label)?.Text ?? (control as Button)?.Text ?? string.Empty}\"";
            if (!area.Encloses(rect)) problems.Add($"{name} {Describe(rect)} is outside the safe area {Describe(area)}");
            var wanted = control.GetCombinedMinimumSize();
            if (rect.Size.X + 0.6f < wanted.X || rect.Size.Y + 0.6f < wanted.Y) problems.Add($"{name} {Describe(rect)} is smaller than the {wanted} it needs");
            if (control is Button or LineEdit && (rect.Size.X < minimum || rect.Size.Y < minimum))
                problems.Add($"{name} is {rect.Size.X / _metrics.UnitsPerDp:0}x{rect.Size.Y / _metrics.UnitsPerDp:0} dp, under the {TouchTargetRules.MinTarget(_metrics.Platform):0} minimum");
        }
        return problems;
    }

    private static string Describe(Rect2 rect) => $"({rect.Position.X:0},{rect.Position.Y:0} {rect.Size.X:0}x{rect.Size.Y:0})";

    private static IEnumerable<Node> Walk(Node node)
    {
        yield return node;
        foreach (var child in node.GetChildren())
            foreach (var below in Walk(child)) yield return below;
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
        if (!_panel.Visible) HideInfo();
        _panel.Visible = true;
    }

    private void RefreshHotbar()
    {
        _hotbarVersion = _hotbarModel!.Version;
        for (var i = 0; i < _slots.Count; i++)
        {
            var view = _hotbarModel.Slot(i);
            var number = (i + 1).ToString(System.Globalization.CultureInfo.InvariantCulture);
            _slots[i].Text = view.IsEmpty ? number : number + "\n" + _bundle!.NameOf(view.DisplayKey);
            _slots[i].Modulate = RestColor(view);
        }
    }

    private static Color RestColor(HotbarSlotView view) => view.IsEmpty ? new Color(1, 1, 1, 0.6f) : view.Usable ? Colors.White : new Color(1, 1, 1, 0.4f);

    /// <summary>A slot was pressed: the model decides what it comes to; the client sends nothing yet (T-502 binds the activator).</summary>
    private void OnSlotPressed(int index, Button slot)
    {
        Tap();
        LastHotbarPress = _hotbarModel!.Press(index);
        HotbarPresses++;
        // Feedback for every press, empty or not.
        slot.Modulate = new Color(1.6f, 1.6f, 1.6f, 1);
        slot.CreateTween().TweenProperty(slot, "modulate", RestColor(_hotbarModel.Slot(index)), 0.18);
    }

    private static void SetText(Label label, string text)
    {
        label.Text = text;
        label.Visible = text.Length > 0;
    }

    private static void SetBar(ProgressBar bar, long value, long max)
    {
        bar.MaxValue = max > 0 ? max : 1;
        bar.Value = value;
    }

    private static Label Wrapped(HorizontalAlignment alignment = HorizontalAlignment.Left) =>
        new() { HorizontalAlignment = alignment, AutowrapMode = TextServer.AutowrapMode.WordSmart, MouseFilter = Control.MouseFilterEnum.Ignore };

    private static ProgressBar Bar(Color fill)
    {
        var bar = new ProgressBar { ShowPercentage = false, MouseFilter = Control.MouseFilterEnum.Ignore };
        bar.AddThemeStyleboxOverride("fill", new StyleBoxFlat { BgColor = fill });
        bar.AddThemeStyleboxOverride("background", new StyleBoxFlat { BgColor = new Color(0, 0, 0, 0.55f) });
        return bar;
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
