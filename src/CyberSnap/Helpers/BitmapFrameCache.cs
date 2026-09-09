using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Windows.Media.Imaging;

namespace CyberSnap.Helpers;

/// <summary>
/// Bounded least-recently-used cache for decoded preview frames.
/// The trimmer scrubs back and forth over the same frames, so caching pays off,
/// but an unbounded cache pins every visited frame and OOMs on long media.
/// </summary>
internal sealed class BitmapFrameCache
{
    private readonly int _capacity;
    private readonly Dictionary<int, LinkedListNode<Entry>> _map = new();
    private readonly LinkedList<Entry> _lru = new();

    private sealed record Entry(int Index, BitmapSource Source);

    public BitmapFrameCache(int capacity)
    {
        _capacity = Math.Max(1, capacity);
    }

    public bool TryGet(int index, [NotNullWhen(true)] out BitmapSource? source)
    {
        if (_map.TryGetValue(index, out LinkedListNode<Entry>? node))
        {
            _lru.Remove(node);
            _lru.AddFirst(node);
            source = node.Value.Source;
            return true;
        }

        source = null;
        return false;
    }

    public void Add(int index, BitmapSource source)
    {
        if (_map.TryGetValue(index, out LinkedListNode<Entry>? existing))
            _lru.Remove(existing);

        _map[index] = _lru.AddFirst(new Entry(index, source));

        while (_map.Count > _capacity && _lru.Last is not null)
        {
            LinkedListNode<Entry> victim = _lru.Last;
            _lru.RemoveLast();
            _map.Remove(victim.Value.Index);
        }
    }

    public void Clear()
    {
        _map.Clear();
        _lru.Clear();
    }
}
