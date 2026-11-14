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
/// Numbers are anchored to the world through the camera on a canvas layer above the HUD, so their size is dp times the UI scale like the rest of the HUD.
/// All numbers are drawn by one canvas item, outlines first and then fills, each grouped by font size, so the engine batches them into a few draw
/// calls however many are up (T-220: a label each cost about two calls, up to 32 at the cap of 16, the largest part of a crowd fight's peak, §9).
/// </summary>
public sealed partial class CombatFx
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
    private readonly NumberCanvas _canvas;
    private readonly Drawn[] _drawn;
    private readonly int[] _fontSizes = new int[NumberStyles.Count];
    private int _outline;
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
        _drawn = new Drawn[session.Numbers.Capacity];
        parent.AddChild(_layer);
        _canvas = new NumberCanvas(this) { Visible = false, MouseFilter = Control.MouseFilterEnum.Ignore, Name = "Numbers" };
        _layer.AddChild(_canvas);
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
        for (var i = 0; i < _shown; i++)
        {
            var centre = _drawn[i].Centre;
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
        if (_shown == 0) return "no number drawn";
        var d = _drawn[0];
        return $"number \"{d.Text}\" centred at {d.Centre} font {_fontSizes[(int)d.Style]} alpha {d.Alpha:0.00} scale {d.Scale:0.00} in tree {_canvas.IsVisibleInTree()}; viewport {_hud.Metrics.CanvasWidth:0}x{_hud.Metrics.CanvasHeight:0}";
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
            ref readonly var slot = ref slots[i];
            if (!slot.Active) continue;
            var world = new Vector3((float)slot.X, AnchorHeight, (float)slot.Z);
            if (camera.IsPositionBehind(world)) continue;
            var t = (float)((now - slot.Start) / lifetime);
            var screen = camera.UnprojectPosition(world);
            if (screen.X < -margin || screen.Y < -margin || screen.X > canvas.X + margin || screen.Y > canvas.Y + margin) continue; // over a target outside the view: nothing to draw
            var rise = (float)(RiseDp * units) * (1f - (1f - t) * (1f - t)); // eases out
            var pop = NumberStyles.IsCrit(slot.Style) && t < 0.2f ? 1f + (0.2f - t) * 2.5f : 1f; // a crit lands with a small pop
            _drawn[_shown++] = new Drawn(
                slot.Text, slot.Style, new Vector2(screen.X + (float)(slot.Lane * LaneDp * units), screen.Y - rise), pop, t < 0.6f ? 1f : (1f - t) / 0.4f);
        }
        _canvas.Visible = _shown > 0;
        if (_shown > 0) _canvas.QueueRedraw();
    }

    /// <summary>One number to draw this frame: its text, style, centre on the canvas, crit pop scale and opacity.</summary>
    private readonly record struct Drawn(string Text, NumberStyle Style, Vector2 Centre, float Scale, float Alpha);

    /// <summary>Draws this frame's numbers: every outline, then every fill, each pass in font-size order, so equal glyph textures sit next to each other and batch.</summary>
    private void Draw(Control canvas)
    {
        var font = canvas.GetThemeDefaultFont();
        var outline = new Color(0, 0, 0, 0.9f);
        for (var pass = 0; pass < 2; pass++)
        {
            for (var style = 0; style < NumberStyles.Count; style++)
            {
                var size = _fontSizes[style];
                if (size <= 0) continue;
                for (var i = 0; i < _shown; i++)
                {
                    ref readonly var d = ref _drawn[i];
                    if ((int)d.Style != style) continue;
                    var extent = font.GetStringSize(d.Text, HorizontalAlignment.Left, -1, size);
                    var baseline = new Vector2(-extent.X / 2, font.GetAscent(size) - extent.Y / 2);
                    canvas.DrawSetTransform(d.Centre, 0, new Vector2(d.Scale, d.Scale));
                    if (pass == 0) canvas.DrawStringOutline(font, baseline, d.Text, HorizontalAlignment.Left, -1, size, _outline, new Color(outline, outline.A * d.Alpha));
                    else canvas.DrawString(font, baseline, d.Text, HorizontalAlignment.Left, -1, size, new Color(ColorOf(d.Style), d.Alpha));
                }
            }
        }
        canvas.DrawSetTransform(Vector2.Zero, 0, Vector2.One);
    }

    /// <summary>The one canvas item all numbers are drawn on.</summary>
    private sealed partial class NumberCanvas : Control
    {
        private readonly CombatFx _owner;

        public NumberCanvas(CombatFx owner) => _owner = owner;

        public override void _Draw() => _owner.Draw(this);
    }

    private void HideNumbers()
    {
        _canvas.Visible = false;
        _shown = 0;
    }

    /// <summary>The font sizes follow the UI scale: worked out again only when the player changes the size.</summary>
    private void EnsureSettings()
    {
        var scale = _hud.UnitsPerDp * _hud.Scale;
        if (scale == _settingsScale) return;
        _settingsScale = scale;
        for (var i = 0; i < NumberStyles.Count; i++) _fontSizes[i] = (int)System.Math.Round(FontDp[i] * scale);
        _outline = (int)System.Math.Max(2, System.Math.Round(4 * scale));
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
