using System;
using System.Collections.Generic;
using System.Security.Cryptography;

namespace Game.Client.Core.Theme;

/// <summary>
/// Which textures are the same image (T-711, §9 "1 material"): every <c>.glb</c> embeds its own copy of a texture, so the engine hands out one texture object per file
/// even when an artist used one atlas everywhere. Character parts merge into one surface when their materials draw the same image, and this says which those are:
/// a key from the image's content (a hash of its bytes as imported), the same for the same pixels in any number of files. Each texture is read once and remembered by
/// its object. A texture whose content cannot be read (missing, broken, an unknown kind) has no key: it keeps a surface of its own, with one warning per texture.
/// </summary>
public sealed class ImageIdentity
{
    private readonly Dictionary<ulong, string?> _known = new Dictionary<ulong, string?>();
    private readonly HashSet<string> _distinct = new HashSet<string>(StringComparer.Ordinal);
    private readonly Action<string> _warn;

    /// <summary>Creates an identity; <paramref name="warn"/> hears once about each texture that cannot be read.</summary>
    public ImageIdentity(Action<string> warn) => _warn = warn;

    /// <summary>Textures read so far (for measuring).</summary>
    public int Read => _known.Count;

    /// <summary>Distinct images among them (for measuring: the textures that merge share one).</summary>
    public int Distinct => _distinct.Count;

    /// <summary>Milliseconds spent reading and hashing (for measuring).</summary>
    public double Milliseconds { get; private set; }

    /// <summary>
    /// The key of a texture's image, or null when it cannot be read. <paramref name="texture"/> identifies the texture object, <paramref name="name"/> names it in
    /// the warning, and <paramref name="content"/> gives its bytes (a kind prefix such as <c>"file"</c> or <c>"pixels:64x64:5"</c> and the data), or null; it is
    /// called once per texture. An exception from it counts as unreadable.
    /// </summary>
    public string? KeyOf(ulong texture, string name, Func<(string Kind, byte[] Data)?> content)
    {
        if (_known.TryGetValue(texture, out var known)) return known;
        var started = System.Diagnostics.Stopwatch.GetTimestamp();
        string? key = null;
        string? problem = null;
        try
        {
            if (content() is { } read && read.Data.Length > 0) key = Key(read.Kind, read.Data);
            else problem = "has no readable image";
        }
        catch (Exception e)
        {
            problem = "could not be read (" + e.GetType().Name + ": " + e.Message + ")";
        }
        Milliseconds += (System.Diagnostics.Stopwatch.GetTimestamp() - started) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
        if (problem is not null) _warn("[theme] texture " + name + " " + problem + "; the part keeps a surface of its own (one more draw call)");
        else _distinct.Add(key!);
        _known[texture] = key;
        return key;
    }

    /// <summary>The key of image content: its kind and a hash of its bytes.</summary>
    public static string Key(string kind, byte[] data)
    {
        var hash = SHA256.HashData(data);
        return kind + ":" + Convert.ToHexString(hash, 0, 12);
    }

    /// <summary>Forgets every texture (they are let go with the models).</summary>
    public void Clear()
    {
        _known.Clear();
        _distinct.Clear();
    }
}
