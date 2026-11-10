using Game.Client.Core.Bundle;
using Godot;

namespace Game.Client.Scene;

/// <summary>The map's look: <see cref="ThemeMapView"/> shows the theme's scene and falls back to <see cref="PlaceholderMap"/>; the cell-to-world rule (§4) stays.</summary>
public interface IMapView
{
    /// <summary>Shows <paramref name="map"/>, replacing what was shown.</summary>
    void Load(MapData map);
}

/// <summary>How an entity should look now.</summary>
/// <param name="Own">It is the client's own character.</param>
/// <param name="Dead">It is dead.</param>
/// <param name="Mine">A ground item this client may take.</param>
public readonly record struct EntityLook(bool Own, bool Dead, bool Mine);

/// <summary>One entity on screen. Pooled: setting a pose allocates nothing.</summary>
public interface IEntityVisual
{
    /// <summary>True for a rigged model with animations: only these count toward the level-of-detail and animation-rate limits (§9).</summary>
    bool Animated { get; }

    /// <summary>Places it: the point on the ground, the ground's height there (visual only, §4) and the rotation around the up axis.</summary>
    void SetPose(float x, float y, float z, float yaw);

    /// <summary>Applies a new look.</summary>
    void SetLook(EntityLook look);

    /// <summary>Shows or hides it (the pool hides it).</summary>
    void SetVisible(bool visible);

    /// <summary>The level of detail: 0 for the nearest characters, 1 for the rest. A model with one level ignores it.</summary>
    void SetDetail(int lod);

    /// <summary>What the entity is doing, every frame: whether it is walking, and counters that go up on each attack made and each hit taken.</summary>
    void SetMotion(bool walking, int attackCount, int hitCount);

    /// <summary>Fades a corpse out: 0 is whole, 1 is gone (it also sinks a little). Anything else puts it back to 0.</summary>
    void SetFade(float amount);

    /// <summary>Moves the animation forward. At the reduced rate the time collects and is applied every <paramref name="reducedEvery"/> frames.</summary>
    void Animate(double dt, bool fullRate, long frame, int reducedEvery);
}

/// <summary>Where visuals come from: the active theme (<see cref="ThemedEntities"/>), through asset keys.</summary>
public interface IEntityVisualSource
{
    /// <summary>
    /// A visual for an entity: <paramref name="kind"/> is an <c>EntityKinds</c> term and <paramref name="displayKey"/> the content id the server sent. The model comes
    /// from the bundle and the theme; the visual may fill in a little later if its model is still loading.
    /// </summary>
    IEntityVisual Acquire(string kind, string? displayKey);

    /// <summary>Back to the pool.</summary>
    void Release(IEntityVisual visual);

    /// <summary>Seconds from the start of an entity's attack clip to its impact, from the theme (the model's, else its rig's, else the client default).</summary>
    double ImpactDelayOf(Game.Client.Core.World.EntityView attacker);

    /// <summary>Once a frame: finishes loads and creates the instances that were put off to spare the frame.</summary>
    void Update();

    /// <summary>Counts up whenever a visual finished loading, so the presenter plans levels of detail again.</summary>
    int Version { get; }
}

/// <summary>Colours of the placeholder world. Not content: T-208 replaces these with the theme's art.</summary>
internal static class PlaceholderColors
{
    public static readonly Color GroundA = new(0.36f, 0.52f, 0.31f);
    public static readonly Color GroundB = new(0.33f, 0.48f, 0.29f);
    public static readonly Color SafeA = new(0.46f, 0.56f, 0.38f);
    public static readonly Color SafeB = new(0.43f, 0.53f, 0.36f);
    public static readonly Color Blocked = new(0.16f, 0.27f, 0.42f);
    public static readonly Color Wall = new(0.30f, 0.27f, 0.25f);
    public static readonly Color WallTop = new(0.42f, 0.38f, 0.35f);
    public static readonly Color Warp = new(0.55f, 0.30f, 0.80f);
    public static readonly Color Background = new(0.07f, 0.08f, 0.10f);
    public static readonly Color Own = new(0.25f, 0.65f, 1.00f);
    public static readonly Color ItemMine = new(1.00f, 0.80f, 0.15f);
    public static readonly Color Marker = new(1.00f, 0.90f, 0.20f);
    public static readonly Color TargetRing = new(1.00f, 0.25f, 0.20f);
}
