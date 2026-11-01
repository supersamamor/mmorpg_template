using System.Collections;

namespace Game.Content.Validation;

/// <summary>What the manifest says about one asset key beyond its existence: its <c>rig</c> and <c>attach</c> point, if any.</summary>
/// <param name="Rig">Rig name of a rigged model (<c>humanoid</c>, <c>blob</c>, ...), or null.</param>
/// <param name="Attach">Attach point of an attached model (<c>head</c>, <c>hand_r</c>, ...), or null.</param>
public sealed record AssetInfo(string? Rig, string? Attach);

/// <summary>
/// The asset keys of a theme manifest, with the <see cref="AssetInfo"/> of each. Behaves as a plain key set, so callers that only
/// need existence are unaffected; rules that need metadata ask <see cref="ValidationContext.InfoOf"/>.
/// </summary>
public sealed class AssetKeySet : IReadOnlySet<string>
{
    private readonly HashSet<string> _keys;
    private readonly IReadOnlyDictionary<string, AssetInfo> _info;

    /// <summary>Creates a set from key → info.</summary>
    public AssetKeySet(IReadOnlyDictionary<string, AssetInfo> info)
    {
        _info = info;
        _keys = new HashSet<string>(info.Keys, StringComparer.Ordinal);
    }

    /// <summary>The metadata of <paramref name="key"/>, or null when the key is not in the manifest.</summary>
    public AssetInfo? InfoOf(string key) => _info.TryGetValue(key, out var info) ? info : null;

    /// <inheritdoc />
    public int Count => _keys.Count;

    /// <inheritdoc />
    public bool Contains(string item) => _keys.Contains(item);

    /// <inheritdoc />
    public bool IsProperSubsetOf(IEnumerable<string> other) => _keys.IsProperSubsetOf(other);

    /// <inheritdoc />
    public bool IsProperSupersetOf(IEnumerable<string> other) => _keys.IsProperSupersetOf(other);

    /// <inheritdoc />
    public bool IsSubsetOf(IEnumerable<string> other) => _keys.IsSubsetOf(other);

    /// <inheritdoc />
    public bool IsSupersetOf(IEnumerable<string> other) => _keys.IsSupersetOf(other);

    /// <inheritdoc />
    public bool Overlaps(IEnumerable<string> other) => _keys.Overlaps(other);

    /// <inheritdoc />
    public bool SetEquals(IEnumerable<string> other) => _keys.SetEquals(other);

    /// <inheritdoc />
    public IEnumerator<string> GetEnumerator() => _keys.GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}
