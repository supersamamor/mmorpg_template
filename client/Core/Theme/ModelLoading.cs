using System;

namespace Game.Client.Core.Theme;

/// <summary>
/// Whether models may load on worker threads. Godot's headless display server uses the dummy renderer, which keeps its meshes in an allocator that is not thread-safe, so
/// creating them on more than one thread corrupts its memory and the process crashes, usually at exit (T-218). The real renderers' allocators are thread-safe. So: threads
/// with a real renderer, the main thread without one. It is told by the display server's name because the rendering driver's name does not show it: headless still reports
/// the project's driver (<c>vulkan</c>).
/// </summary>
public static class ModelLoading
{
    /// <summary>The name of the display server that has no renderer.</summary>
    public const string HeadlessDisplayServer = "headless";

    /// <summary>True when worker threads may load models.</summary>
    /// <param name="displayServer">The name of the current display server.</param>
    /// <param name="forceThreaded">Use threads even so, to check whether a new engine version still crashes (setting <c>threaded_load_without_renderer</c>).</param>
    public static bool Threaded(string displayServer, bool forceThreaded) =>
        forceThreaded || !string.Equals(displayServer, HeadlessDisplayServer, StringComparison.OrdinalIgnoreCase);
}
