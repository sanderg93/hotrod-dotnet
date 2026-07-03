using System.Collections.Generic;

namespace HotRod.Client;

/// <summary>
/// The client-side store behind an invalidated near cache: a thread-safe map from a key (in the
/// cache's storage format) to the <see cref="MetadataValue{T}"/> a read returned. Reads populate it and
/// server-pushed change events invalidate it, so repeated reads of an unchanged key skip the server.
/// <para>
/// A read that misses inserts the <see cref="Placeholder"/> sentinel, fetches from the server, then
/// <see cref="Replace"/>s the sentinel with the fetched value. If a concurrent write or an invalidation
/// event removes the sentinel in between, the replace fails and the (now potentially stale) value is not
/// cached — this is what keeps a near-cache read from ever going backwards in time.
/// </para>
/// When a positive entry cap is set the store is bounded and evicts the least-recently-used entry once
/// it is full; a cap of zero leaves it unbounded.
/// </summary>
internal sealed class NearCache
{
    /// <summary>
    /// The sentinel stored while a miss is being resolved against the server. It is compared by
    /// reference identity, so it never collides with a real value; its fields are otherwise unused.
    /// </summary>
    internal static readonly MetadataValue<byte[]> Placeholder = new([], -1, null, null, null, null);

    private sealed class Entry
    {
        public required byte[] Key;
        public required MetadataValue<byte[]> Value;
    }

    private readonly int _maxEntries; // 0 = unbounded
    private readonly object _gate = new();
    private readonly Dictionary<byte[], LinkedListNode<Entry>> _map;
    private readonly LinkedList<Entry> _lru = new(); // most-recently-used at the front

    private long _hits;
    private long _misses;

    public NearCache(int maxEntries)
    {
        _maxEntries = maxEntries;
        _map = new Dictionary<byte[], LinkedListNode<Entry>>(ByteArrayComparer.Instance);
    }

    /// <summary>
    /// Returns the cached value for <paramref name="key"/> and counts a hit, or null (counting a miss)
    /// when the key is absent or only a <see cref="Placeholder"/> is present. A hit marks the entry as
    /// most-recently-used.
    /// </summary>
    public MetadataValue<byte[]>? Get(byte[] key)
    {
        lock (_gate)
        {
            if (_map.TryGetValue(key, out LinkedListNode<Entry>? node) && !ReferenceEquals(node.Value.Value, Placeholder))
            {
                _lru.Remove(node);
                _lru.AddFirst(node);
                _hits++;
                return node.Value.Value;
            }

            _misses++;
            return null;
        }
    }

    /// <summary>Inserts <paramref name="value"/> only if the key is absent; returns whether it was inserted.</summary>
    public bool PutIfAbsent(byte[] key, MetadataValue<byte[]> value)
    {
        lock (_gate)
        {
            if (_map.ContainsKey(key))
                return false;

            var node = new LinkedListNode<Entry>(new Entry { Key = key, Value = value });
            _lru.AddFirst(node);
            _map[key] = node;
            EvictIfNeeded();
            return true;
        }
    }

    /// <summary>
    /// Swaps <paramref name="expected"/> for <paramref name="value"/> only if the entry still holds the
    /// exact instance passed as <paramref name="expected"/> (reference identity). Returns whether it did.
    /// </summary>
    public bool Replace(byte[] key, MetadataValue<byte[]> expected, MetadataValue<byte[]> value)
    {
        lock (_gate)
        {
            if (_map.TryGetValue(key, out LinkedListNode<Entry>? node) && ReferenceEquals(node.Value.Value, expected))
            {
                node.Value.Value = value;
                return true;
            }

            return false;
        }
    }

    /// <summary>Removes the entry for <paramref name="key"/> (invalidation); returns whether one existed.</summary>
    public bool Remove(byte[] key)
    {
        lock (_gate)
        {
            if (_map.Remove(key, out LinkedListNode<Entry>? node))
            {
                _lru.Remove(node);
                return true;
            }

            return false;
        }
    }

    /// <summary>Removes the entry only if it still holds the exact <paramref name="value"/> instance (used to retract a placeholder).</summary>
    public bool Remove(byte[] key, MetadataValue<byte[]> value)
    {
        lock (_gate)
        {
            if (_map.TryGetValue(key, out LinkedListNode<Entry>? node) && ReferenceEquals(node.Value.Value, value))
            {
                _map.Remove(key);
                _lru.Remove(node);
                return true;
            }

            return false;
        }
    }

    /// <summary>Drops every entry, leaving the store empty (the hit/miss counters are kept).</summary>
    public void Clear()
    {
        lock (_gate)
        {
            _map.Clear();
            _lru.Clear();
        }
    }

    /// <summary>A consistent snapshot of the hit/miss counters and the current entry count.</summary>
    public NearCacheStatistics Snapshot()
    {
        lock (_gate)
            return new NearCacheStatistics(_hits, _misses, _map.Count);
    }

    private void EvictIfNeeded()
    {
        if (_maxEntries <= 0)
            return;

        while (_map.Count > _maxEntries)
        {
            LinkedListNode<Entry>? oldest = _lru.Last;
            if (oldest is null)
                return;
            _lru.RemoveLast();
            _map.Remove(oldest.Value.Key);
        }
    }

    /// <summary>Structural (content) equality for the byte-array keys stored in the near cache.</summary>
    private sealed class ByteArrayComparer : IEqualityComparer<byte[]>
    {
        public static readonly ByteArrayComparer Instance = new();

        public bool Equals(byte[]? x, byte[]? y) =>
            ReferenceEquals(x, y) || (x is not null && y is not null && x.AsSpan().SequenceEqual(y));

        public int GetHashCode(byte[] obj)
        {
            var hash = new HashCode();
            hash.AddBytes(obj);
            return hash.ToHashCode();
        }
    }
}
