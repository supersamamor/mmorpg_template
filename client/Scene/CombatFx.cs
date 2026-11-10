using System.Collections.Generic;
using Game.Client.Core;
using Game.Client.Core.Combat;
using Game.Client.Core.Theme;
using Game.Client.Core.World;
using Godot;

namespace Game.Client.Scene;

/// <summary>
/// Draws what a landed hit leaves behind (T-303, §7.6): floating damage numbers and small pooled effects at the hit, the death effect and the level-up effect.
/// Presentation only. Everything is pooled and capped, and a frame with nothing playing costs a few comparisons (§9 battery): no node is touched, nothing moves.
/// Numbers are HUD-layer labels anchored to the world through the camera, so their size is dp times the UI scale like the rest of the HUD.
/// </summary>
public sealed class CombatFx
{
    // Sizes in dp at UI scale 1.
    private static readonly double[] FontDp = { 26, 38, 22, 26, 38, 22, 18, 26, 16 };
    private const double RiseDp = 56;
    private const double LaneDp = 34;
    private const float AnchorHeight = 1.7f;
    private const float EffectHeight = 0.9f;

    private readonly ClientConfig _config;
    private readonly ClientSession _session;
    private readonly FollowCamera _camera;
    private readonly Hud _hud;
    private readonly ThemeCatalog _catalog;
    private readonly Node3D _parent;
    private readonly CanvasLayer _layer = new() { Layer = 1, Name = "DamageNumbers" };
    private readonly Label[] _labels;
    private readonly NumberStyle[] _labelStyles;
    private readonly string?[] _labelTexts;
    private readonly LabelSettings[] _settings = new LabelSettings[NumberStyles.Count];
    private readonly Dictionary<string, PackedScene?> _scenes = new();
    private readonly Dictionary<string, Stack<Fx>> _free = new();
    private readonly List<Fx> _active = new();
    private double _settingsScale = -1;
    private int _levelUpSerial;
    private int _shown;

    /// <summary>Builds the pools and listens for impacts. Call after the HUD exists so the numbers draw above it.</summary>
    public CombatFx(Node3D parent, ClientConfig config, ClientSession session, FollowCamera camera, Hud hud, ThemeCatalog catalog)
    {
        _parent = parent;
        _config = config;
        _session = session;
        _camera = camera;
        _hud = hud;
        _catalog = catalog;
        _labels = new Label[session.Numbers.Capacity];
        _labelStyles = new NumberStyle[_labels.Length];
        _labelTexts = new string?[_labels.Length];
        parent.AddChild(_layer);
        for (var i = 0; i < _labels.Length; i++)
        {
            _labels[i] = new Label
            {
                Visible = false,
                MouseFilter = Control.MouseFilterEnum.Ignore,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                AutowrapMode = TextServer.AutowrapMode.Off,
            };
            _layer.AddChild(_labels[i]);
        }
        _levelUpSerial = session.Ui.LevelUpSerial;
        session.World.Combat.Impact += OnImpact;
        // Resolve the scenes now, on the main thread (they are tiny, and headless loading must stay on it, §7.7).
        foreach (var key in new[] { EffectNames.Hit, EffectNames.Crit, EffectNames.Death, EffectNames.LevelUp }) SceneOf(key);
    }

    /// <summary>Numbers actually on screen this frame (the others float over targets outside the view and are not drawn).</summary>
    public int Shown => _shown;

    /// <summary>Numbers drawn inside the screen less an <paramref name="inset"/> share on every side (for the scripted run's screenshot, which wants a fight in view).</summary>
    public int ShownInside(float inset)
    {
        var size = _layer.GetViewport().GetVisibleRect().Size;
        var count = 0;
        for (var i = 0; i < _labels.Length; i++)
        {
            if (!_labels[i].Visible) continue;
            var centre = _labels[i].Position + _labels[i].Size / 2;
            if (centre.X > size.X * inset && centre.X < size.X * (1 - inset) && centre.Y > size.Y * inset && centre.Y < size.Y * (1 - inset)) count++;
        }
        return count;
    }

    /// <summary>Numbers drawn this frame and effects playing now (for measuring).</summary>
    public (int Numbers, int Effects) Active => (_session.Numbers.ActiveCount, _active.Count);

    /// <summary>Effects ever started, and numbers dropped for the cap (for measuring).</summary>
    public (long Effects, long NumbersDropped) Totals => (_started, _session.Numbers.DroppedForCap);

    private long _started;

    /// <summary>Where the first drawn number is and what it looks like (for the scripted run's log).</summary>
    public string Describe()
    {
        for (var i = 0; i < _labels.Length; i++)
            if (_labels[i].Visible)
                return $"label {i} \"{_labels[i].Text}\" at {_labels[i].Position} size {_labels[i].Size} modulate {_labels[i].Modulate} font {_labels[i].LabelSettings?.FontSize} in tree {_labels[i].IsVisibleInTree()}; viewport {_hud.Metrics.CanvasWidth:0}x{_hud.Metrics.CanvasHeight:0}";
        return "no label visible";
    }

    /// <summary>A hit landed: show its effect. (The number is started by the session, from the same event.)</summary>
    private void OnImpact(in HitImpact impact)
    {
        if (impact.Kind == HitKind.Miss) return;
        // Fights this client is not in only take half the effect pool, so a crowd cannot crowd out the player's own hits (and the draw calls stay down).
        if (impact.Side == HitSide.Other && _active.Count >= _config.MaxEffects / 2) return;
        Spawn(impact.Kind == HitKind.Crit ? EffectNames.Crit : EffectNames.Hit, impact.X, impact.Z, impact.At, _config.EffectSeconds, 1f);
    }

    /// <summary>A death became visible: play the death effect where the body lies.</summary>
    public void OnDeath(float x, float y, float z) => Spawn(EffectNames.Death, x, z, Clocks.View(), _config.LongEffectSeconds, 1.4f, y);

    /// <summary>Once a frame, after the session and presenter ran. Does nothing when no number or effect is playing and no level was gained.</summary>
    public void Update(double now, GroundHeights ground)
    {
        var ui = _session.Ui;
        if (ui.LevelUpSerial != _levelUpSerial)
        {
            _levelUpSerial = ui.LevelUpSerial;
            if (_session.World.Own is { } own) Spawn(EffectNames.LevelUp, own.X, own.Z, now, _config.LongEffectSeconds, 2f, ground.At(own.X, own.Z));
        }
        UpdateEffects(now);
        UpdateNumbers(now);
    }

    // ---------------- numbers ----------------

    private void UpdateNumbers(double now)
    {
        var pool = _session.Numbers;
        if (!pool.Update(now))
        {
            if (_shown > 0) HideNumbers();
            return;
        }
        EnsureSettings();
        var slots = pool.Slots;
        var camera = _camera.Camera;
        var units = _hud.UnitsPerDp * _hud.Scale;
        var lifetime = pool.Lifetime;
        var canvas = _hud.Metrics.CanvasWidth > 0 ? new Vector2((float)_hud.Metrics.CanvasWidth, (float)_hud.Metrics.CanvasHeight) : _layer.GetViewport().GetVisibleRect().Size;
        var margin = (float)(LaneDp * 3 * units);
        _shown = 0;
        for (var i = 0; i < slots.Length; i++)
        {
            var label = _labels[i];
            ref readonly var slot = ref slots[i];
            if (!slot.Active)
            {
                if (label.Visible) label.Visible = false;
                continue;
            }
            var world = new Vector3((float)slot.X, AnchorHeight, (float)slot.Z);
            if (camera.IsPositionBehind(world))
            {
                if (label.Visible) label.Visible = false;
                continue;
            }
            var t = (float)((now - slot.Start) / lifetime);
            var screen = camera.UnprojectPosition(world);
            if (screen.X < -margin || screen.Y < -margin || screen.X > canvas.X + margin || screen.Y > canvas.Y + margin)
            {
                if (label.Visible) label.Visible = false; // over a target outside the view: nothing to draw
                continue;
            }
            var rise = (float)(RiseDp * units) * (1f - (1f - t) * (1f - t)); // eases out
            var size = (float)(FontDp[(int)slot.Style] * units);
            if (!ReferenceEquals(_labelTexts[i], slot.Text))
            {
                _labelTexts[i] = slot.Text; // the engine's Text getter allocates, so the last text is kept here
                label.Text = slot.Text;
            }
            if (!label.Visible || _labelStyles[i] != slot.Style)
            {
                _labelStyles[i] = slot.Style;
                label.LabelSettings = _settings[(int)slot.Style];
            }
            var width = size * 6f;
            var height = size * 1.6f;
            label.Size = new Vector2(width, height);
            label.PivotOffset = new Vector2(width / 2, height / 2);
            label.Position = new Vector2(screen.X - width / 2 + (float)(slot.Lane * LaneDp * units), screen.Y - rise - height / 2);
            var pop = NumberStyles.IsCrit(slot.Style) && t < 0.2f ? 1f + (0.2f - t) * 2.5f : 1f; // a crit lands with a small pop
            label.Scale = new Vector2(pop, pop);
            label.Modulate = new Color(1, 1, 1, t < 0.6f ? 1f : (1f - t) / 0.4f);
            label.Visible = true;
            _shown++;
        }
    }

    private void HideNumbers()
    {
        for (var i = 0; i < _labels.Length; i++) _labels[i].Visible = false;
        _shown = 0;
    }

    /// <summary>The label styles follow the UI scale: rebuilt only when the player changes the size.</summary>
    private void EnsureSettings()
    {
        var scale = _hud.UnitsPerDp * _hud.Scale;
        if (scale == _settingsScale) return;
        _settingsScale = scale;
        for (var i = 0; i < NumberStyles.Count; i++)
        {
            var style = (NumberStyle)i;
            _settings[i] = new LabelSettings
            {
                FontSize = (int)System.Math.Round(FontDp[i] * scale),
                FontColor = ColorOf(style),
                OutlineSize = (int)System.Math.Max(2, System.Math.Round(4 * scale)),
                OutlineColor = new Color(0, 0, 0, 0.9f),
            };
        }
        for (var i = 0; i < _labels.Length; i++)
            if (_labels[i].Visible) _labels[i].LabelSettings = _settings[(int)_labelStyles[i]];
    }

    private static Color ColorOf(NumberStyle style)
    {
        if (NumberStyles.IsTaken(style)) return NumberStyles.IsCrit(style) ? new Color(1f, 0.35f, 0.25f) : NumberStyles.IsMiss(style) ? new Color(0.8f, 0.85f, 0.95f) : new Color(1f, 0.55f, 0.5f);
        if (NumberStyles.IsOther(style)) return new Color(0.82f, 0.82f, 0.82f);
        return NumberStyles.IsCrit(style) ? new Color(1f, 0.82f, 0.15f) : NumberStyles.IsMiss(style) ? new Color(0.8f, 0.85f, 0.95f) : Colors.White;
    }

    // ---------------- effects ----------------

    private void Spawn(string key, double x, double z, double now, double seconds, float scale, float? groundY = null)
    {
        var pooledKey = _catalog.ResolveEffect(key, out var resolved) ? resolved : null;
        if (pooledKey is null) return;
        if (_active.Count >= _config.MaxEffects) Retire(0); // the oldest ends early
        if (!_free.TryGetValue(pooledKey, out var free)) _free[pooledKey] = free = new Stack<Fx>();
        Fx fx;
        if (free.Count > 0)
        {
            fx = free.Pop();
        }
        else
        {
            var scene = SceneOf(key);
            if (scene is null) return;
            var node = scene.Instantiate<Node3D>();
            _parent.AddChild(node);
            fx = new Fx(node, pooledKey);
        }
        fx.Start = now;
        fx.Seconds = seconds;
        fx.Scale = scale;
        fx.Node.Position = new Vector3((float)x, (groundY ?? 0f) + EffectHeight, (float)z);
        fx.Node.Visible = true;
        _active.Add(fx);
        _started++;
        Apply(fx, 0f);
    }

    private PackedScene? SceneOf(string key)
    {
        if (_scenes.TryGetValue(key, out var cached)) return cached;
        PackedScene? scene = null;
        if (_catalog.ResolveEffect(key, out var path)) scene = ResourceLoader.Load<PackedScene>("res://" + path);
        _scenes[key] = scene;
        return scene;
    }

    private void UpdateEffects(double now)
    {
        for (var i = _active.Count - 1; i >= 0; i--)
        {
            var fx = _active[i];
            var t = (float)((now - fx.Start) / fx.Seconds);
            if (t >= 1f)
            {
                Retire(i);
                continue;
            }
            Apply(fx, t);
        }
    }

    // A pop: it swells quickly and shrinks away, so it needs no transparency and no animation player.
    private static void Apply(Fx fx, float t)
    {
        var size = fx.Scale * (t < 0.3f ? 0.4f + t / 0.3f * 0.8f : 1.2f * (1f - (t - 0.3f) / 0.7f));
        size = Mathf.Max(size, 0.001f);
        fx.Node.Scale = new Vector3(size, size, size);
    }

    private void Retire(int index)
    {
        var fx = _active[index];
        _active.RemoveAt(index);
        fx.Node.Visible = false;
        _free[fx.Key].Push(fx);
    }

    private sealed class Fx
    {
        public Fx(Node3D node, string key)
        {
            Node = node;
            Key = key;
        }

        public Node3D Node { get; }
        public string Key { get; }
        public double Start;
        public double Seconds;
        public float Scale;
    }
}
