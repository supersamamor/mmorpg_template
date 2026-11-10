using System;
using System.Collections.Generic;

namespace Game.Client.Core.Audio;

/// <summary>
/// Memory accounting for loaded effect sounds (ARCHITECTURE §9: at most 48 MB). Each loaded file is charged its decoded size; when the total goes over the limit
/// the least recently used files are dropped. Pure bookkeeping: the scene loads and frees the streams this names. A file larger than the whole limit is still
/// kept alone (it is playing).
/// </summary>
public sealed class SoundBudget
{
    private readonly long _limit;
    private readonly Dictionary<string, LinkedListNode<Entry>> _byPath = new Dictionary<string, LinkedListNode<Entry>>(StringComparer.Ordinal);
    private readonly LinkedList<Entry> _order = new LinkedList<Entry>(); // front = most recently used

    private readonly struct Entry
    {
        public Entry(string path, long bytes)
        {
            Path = path;
            Bytes = bytes;
        }

        public string Path { get; }

        public long Bytes { get; }
    }

    /// <summary>Creates a budget of <paramref name="limitBytes"/>.</summary>
    public SoundBudget(long limitBytes) => _limit = limitBytes;

    /// <summary>The limit in bytes.</summary>
    public long Limit => _limit;

    /// <summary>Bytes charged now.</summary>
    public long Used { get; private set; }

    /// <summary>The highest <see cref="Used"/> ever reached.</summary>
    public long Peak { get; private set; }

    /// <summary>Files charged now.</summary>
    public int Count => _byPath.Count;

    /// <summary>How many files were dropped for room.</summary>
    public int Evictions { get; private set; }

    /// <summary>True when the file is charged (loaded).</summary>
    public bool Contains(string path) => _byPath.ContainsKey(path);

    /// <summary>A loaded file was used again: it becomes the most recent.</summary>
    public void Touch(string path)
    {
        if (!_byPath.TryGetValue(path, out var node)) return;
        _order.Remove(node);
        _order.AddFirst(node);
    }

    /// <summary>Charges a newly loaded file and fills <paramref name="evicted"/> with the paths to drop (cleared first).</summary>
    public void Add(string path, long bytes, List<string> evicted)
    {
        evicted.Clear();
        if (_byPath.ContainsKey(path)) return;
        _byPath[path] = _order.AddFirst(new Entry(path, bytes));
        Used += bytes;
        Peak = Math.Max(Peak, Used);
        while (Used > _limit && _order.Count > 1)
        {
            var last = _order.Last!;
            _order.RemoveLast();
            _byPath.Remove(last.Value.Path);
            Used -= last.Value.Bytes;
            Evictions++;
            evicted.Add(last.Value.Path);
        }
    }

    /// <summary>What a file costs once decoded to 16-bit PCM.</summary>
    public static long DecodedBytes(double seconds, int sampleRate, int channels) => (long)Math.Ceiling(seconds * sampleRate) * channels * 2;
}
