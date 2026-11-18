using Game.Client.Core;
using Game.Client.Core.Bundle;
using Game.Client.Core.Input;
using Game.Client.Core.Ui;
using Game.Client.Core.World;
using Game.Shared.Protocol;
using Godot;

namespace Game.Client.Scene;

/// <summary>
/// The one input path for touch and mouse (ARCHITECTURE §7.3). A mouse arrives as touch index 0, so a click, a press-and-hold and the wheel go through the same
/// code as fingers. A touch that begins on the UI belongs to the UI until it ends and never reaches the world. The scripted run feeds it too, so it acts exactly as
/// a finger does. It holds no game logic: a tap asks (<see cref="ClientSession.TapGround"/>), and the server decides.
/// </summary>
public sealed class InputRouter
{
    private readonly ClientConfig _config;
    private readonly ClientSession _session;
    private readonly DisplayBundle _bundle;
    private readonly FollowCamera _camera;
    private readonly Hud _hud;
    private readonly Func<MapData?> _map;
    private readonly TapGestures _gestures;
    private readonly GroundHeights _ground;
    private int _uiTouches; // bit per touch index that began on the UI

    /// <summary>Creates the router. <paramref name="map"/> gives the map whose ground is shown, for the "nothing there to tap" check.</summary>
    public InputRouter(ClientConfig config, ClientSession session, DisplayBundle bundle, FollowCamera camera, Hud hud, Func<MapData?> map, GroundHeights ground)
    {
        _ground = ground;
        _config = config;
        _session = session;
        _bundle = bundle;
        _camera = camera;
        _hud = hud;
        _map = map;
        _gestures = new TapGestures(config);
    }

    /// <summary>Handles one input event.</summary>
    public void Handle(InputEvent @event)
    {
        var now = Clocks.View();
        switch (@event)
        {
            case InputEventScreenTouch { Pressed: true } touch:
                if (_hud.Blocks(touch.Position))
                {
                    _uiTouches |= Bit(touch.Index);
                    return;
                }
                _hud.HideInfo();
                _gestures.UnitsPerDp = _hud.UnitsPerDp;
                _gestures.Press(touch.Index, touch.Position.X, touch.Position.Y, now);
                break;
            case InputEventScreenTouch touch:
                if ((_uiTouches & Bit(touch.Index)) != 0)
                {
                    _uiTouches &= ~Bit(touch.Index);
                    return;
                }
                switch (_gestures.Release(touch.Index, touch.Position.X, touch.Position.Y, now))
                {
                    case GestureResult.Tap:
                        OnTap(new Vector2((float)_gestures.TapX, (float)_gestures.TapY), now);
                        break;
                    case GestureResult.LongPress:
                        OnLongPress(new Vector2((float)_gestures.LongPressX, (float)_gestures.LongPressY));
                        break;
                }
                break;
            case InputEventScreenDrag drag:
                if ((_uiTouches & Bit(drag.Index)) != 0) return;
                if (_gestures.Drag(drag.Index, drag.Position.X, drag.Position.Y) == GestureResult.Zoom) _camera.Zoom(_gestures.ZoomFactor);
                break;
            case InputEventMagnifyGesture magnify:
                _camera.Zoom(1 / magnify.Factor);
                break;
            case InputEventMouseButton { Pressed: true, ButtonIndex: MouseButton.WheelUp }:
                _camera.Zoom(1 / _config.CameraWheelStep);
                break;
            case InputEventMouseButton { Pressed: true, ButtonIndex: MouseButton.WheelDown }:
                _camera.Zoom(_config.CameraWheelStep);
                break;
        }
    }

    /// <summary>
    /// Once per frame. While one finger is down and still, time may turn it into a long-press; at any other time this is one comparison.
    /// </summary>
    public void Tick()
    {
        if (!_gestures.Waiting) return;
        if (_gestures.Tick(Clocks.View()) == GestureResult.LongPress)
            OnLongPress(new Vector2((float)_gestures.LongPressX, (float)_gestures.LongPressY));
    }

    /// <summary>
    /// A tap: the living monster or ground item nearest the finger within its reach (a monster wins a tie): attack it or pick it up; else the ground. It only
    /// asks; range, path, walkability and who may take an item are the server's.
    /// </summary>
    private void OnTap(Vector2 position, double now)
    {
        // A skill waiting for its target (T-502) only looks for monsters: a loot bag beside one must not take the tap.
        if (PickEntity(position, EntityKinds.Monster, _session.Ui.PendingSkill is null ? EntityKinds.Item : null) is { } id && _session.World.Find(id) is { } picked)
        {
            if (picked.Kind == EntityKinds.Item) _session.TapItem(id);
            else _session.TapEntity(id);
            return;
        }
        if (!_camera.TryTerrain(position, _ground, out var ground)) return;
        var x = TouchTargets.CellOf(ground.X);
        var y = TouchTargets.CellOf(ground.Z);
        if (_map() is { } map && !map.Inside(x, y)) return; // nothing there to tap; everything else goes to the server
        _session.TapGround(new Game.Shared.World.GridCell(x, y), now);
    }

    /// <summary>A long-press on something shows what the bundle says about it. On the ground it does nothing.</summary>
    private void OnLongPress(Vector2 position)
    {
        if (PickEntity(position, null, null) is not { } id || _session.World.Find(id) is not { } entity) return;
        _hud.ShowInfo(EntityInfo.Title(entity, _bundle), EntityInfo.Subtitle(entity), position);
    }

    /// <summary>The living entity of <paramref name="kind"/> or <paramref name="other"/> (any kind, dead included, when both are null) nearest the touch within its reach.</summary>
    private long? PickEntity(Vector2 position, string? kind, string? other)
    {
        var camera = _camera.Camera;
        var metrics = _hud.Metrics;
        var radius = TouchTargetRules.PickRadius(metrics.Platform, _config.TouchTargetDp, _hud.UnitsPerDp);
        long? best = null;
        var bestDistance = float.MaxValue;
        var entities = _session.World.Entities;
        for (var i = 0; i < entities.Count; i++)
        {
            var entity = entities[i];
            if (kind is not null && ((entity.Kind != kind && entity.Kind != other) || entity.Dead)) continue;
            var lift = entity.Kind == EntityKinds.Item ? 0.15f : 0.45f;   // a loot bag lies low; a monster's middle is about half a cell up
            var world = new Vector3((float)entity.X, _ground.At(entity.X, entity.Z) + lift, (float)entity.Z);
            if (camera.IsPositionBehind(world)) continue;
            var center = camera.UnprojectPosition(world);
            // The shape's size on screen counts too, so a close-up monster is as easy to hit as it looks.
            var top = camera.UnprojectPosition(world + Vector3.Up * 0.6f);
            var reach = Mathf.Max((float)radius, center.DistanceTo(top));
            var distance = center.DistanceTo(position);
            // Strictly nearer wins; at the same distance a monster (the list's first kind) keeps the tap.
            if (distance <= reach && (distance < bestDistance || (distance == bestDistance && entity.Kind == kind)))
            {
                bestDistance = distance;
                best = entity.Id;
            }
        }
        return best;
    }

    private static int Bit(int index) => index is >= 0 and < 31 ? 1 << index : 0;
}
