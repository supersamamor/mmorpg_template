using Game.Client.Core.Bundle;
using Godot;

namespace Game.Client.Scene;

/// <summary>The map's look. T-208's authored scenes replace <see cref="PlaceholderMap"/> behind this; the cell-to-world rule (§4) stays.</summary>
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
    /// <summary>Places it: the point on the ground and the rotation around the up axis.</summary>
    void SetPose(float x, float z, float yaw);

    /// <summary>Applies a new look.</summary>
    void SetLook(EntityLook look);

    /// <summary>Shows or hides it (the pool hides it).</summary>
    void SetVisible(bool visible);
}

/// <summary>Where visuals come from. T-207's theme loader replaces <see cref="PlaceholderEntities"/> behind this (it can read the content ids).</summary>
public interface IEntityVisualSource
{
    /// <summary>A visual for an <c>EntityKinds</c> term, from the pool if possible.</summary>
    IEntityVisual Acquire(string kind);

    /// <summary>Back to the pool.</summary>
    void Release(string kind, IEntityVisual visual);
}

/// <summary>Colours of the placeholder world. Not content: T-207 and T-208 replace these with the theme's art.</summary>
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
    public static readonly Color Player = new(0.45f, 0.80f, 0.95f);
    public static readonly Color Monster = new(0.90f, 0.35f, 0.28f);
    public static readonly Color Npc = new(0.95f, 0.85f, 0.45f);
    public static readonly Color Dead = new(0.35f, 0.33f, 0.33f);
    public static readonly Color Item = new(0.80f, 0.80f, 0.75f);
    public static readonly Color ItemMine = new(1.00f, 0.80f, 0.15f);
    public static readonly Color Marker = new(1.00f, 0.90f, 0.20f);
    public static readonly Color TargetRing = new(1.00f, 0.25f, 0.20f);
}
