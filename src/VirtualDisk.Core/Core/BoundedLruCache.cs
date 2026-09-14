using System.Diagnostics.CodeAnalysis;

namespace Qcow2Explorer.Core;

internal sealed class BoundedLruCache<TKey, TValue>
    where TKey : notnull
{
    private readonly long _maximumSize;
    private readonly Func<TValue, long> _measure;
    private readonly Dictionary<TKey, Entry> _entries = [];
    private readonly LinkedList<TKey> _recency = [];
    private readonly object _sync = new();
    private long _totalSize;

    public BoundedLruCache(long maximumSize, Func<TValue, long> measure)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumSize);
        ArgumentNullException.ThrowIfNull(measure);
        _maximumSize = maximumSize;
        _measure = measure;
    }

    internal int Count
    {
        get
        {
            lock (_sync)
            {
                return _entries.Count;
            }
        }
    }

    internal long TotalSize
    {
        get
        {
            lock (_sync)
            {
                return _totalSize;
            }
        }
    }

    public bool TryGetValue(TKey key, [MaybeNullWhen(false)] out TValue value)
    {
        lock (_sync)
        {
            if (!_entries.TryGetValue(key, out var entry))
            {
                value = default;
                return false;
            }

            Touch(entry);
            value = entry.Value;
            return true;
        }
    }

    public TValue AddOrGetExisting(TKey key, TValue value)
    {
        var size = _measure(value);
        if (size <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(value), "キャッシュ項目のサイズは正の値である必要があります。");
        }

        lock (_sync)
        {
            if (_entries.TryGetValue(key, out var existing))
            {
                Touch(existing);
                return existing.Value;
            }

            if (size > _maximumSize)
            {
                return value;
            }

            var node = _recency.AddFirst(key);
            _entries.Add(key, new Entry(value, size, node));
            _totalSize = checked(_totalSize + size);
            while (_totalSize > _maximumSize)
            {
                var leastRecent = _recency.Last
                    ?? throw new InvalidOperationException("LRUキャッシュの内部状態が不正です。");
                var removed = _entries[leastRecent.Value];
                _entries.Remove(leastRecent.Value);
                _recency.RemoveLast();
                _totalSize -= removed.Size;
            }

            return value;
        }
    }

    private void Touch(Entry entry)
    {
        _recency.Remove(entry.Node);
        _recency.AddFirst(entry.Node);
    }

    private sealed record Entry(TValue Value, long Size, LinkedListNode<TKey> Node);
}
