namespace SmartX.Shared.Telemetry;

/// <summary>
/// A binary min-heap: a complete binary tree stored in an array, where every parent's
/// priority is less than or equal to its children's. The smallest priority is therefore
/// always at index 0.
///
/// <para><b>The array layout.</b> No node objects or pointers: the tree is implied by
/// position. The node at index <c>i</c> has its children at <c>2i + 1</c> and <c>2i + 2</c>
/// and its parent at <c>(i - 1) / 2</c>. Because the tree is complete (filled level by
/// level, left to right), the array has no gaps and the height is always floor(log2 n).</para>
///
/// <para><b>Costs.</b> <see cref="Push"/> appends at the end and sifts the new item up;
/// <see cref="TryPop"/> moves the last item to the root and sifts it down. Each sift is at
/// most one swap per level, so both are O(log n). <see cref="TryPeek"/> is O(1).</para>
///
/// <para><b>Why the gateway needs one.</b> Prim's algorithm (section 4.2) repeatedly asks
/// "which link leaving the tree so far is cheapest?". Scanning every candidate link each
/// time is O(E) per step; a min-heap answers it in O(log E), which takes Prim's from
/// O(V x E) down to O(E log E).</para>
///
/// <para>Part 2's ingest queue uses .NET's built-in <c>PriorityQueue</c>; this one is
/// written out by hand so the heap mechanics are visible. Not thread-safe.</para>
/// </summary>
// Reference: Cormen, T. H. et al. (2022). Introduction to Algorithms, 4th ed., ch. 6 "Heapsort" (s. 6.1-6.5). MIT Press.
// Reference: Williams, J. W. J. (1964). Algorithm 232: Heapsort. Communications of the ACM, 7(6), 347-348.
public sealed class MinHeap<TElement, TPriority> where TPriority : IComparable<TPriority>
{
    private readonly List<(TElement Element, TPriority Priority)> _items = [];

    public int Count => _items.Count;

    /// <summary>Levels in the tree: floor(log2 n) + 1, or 0 when empty.</summary>
    public int Height => _items.Count == 0 ? 0 : (int)Math.Log2(_items.Count) + 1;

    /// <summary>Adds an item. O(log n).</summary>
    public void Push(TElement element, TPriority priority)
    {
        _items.Add((element, priority));
        SiftUp(_items.Count - 1);
    }

    /// <summary>The item with the smallest priority, without removing it. O(1).</summary>
    public bool TryPeek(out TElement element, out TPriority priority)
    {
        if (_items.Count == 0)
        {
            element = default!;
            priority = default!;
            return false;
        }

        (element, priority) = _items[0];
        return true;
    }

    /// <summary>Removes and returns the item with the smallest priority. O(log n).</summary>
    public bool TryPop(out TElement element, out TPriority priority)
    {
        if (!TryPeek(out element, out priority)) return false;

        // Move the last leaf into the root's place, shrink, then let it sink to where it belongs.
        var last = _items.Count - 1;
        _items[0] = _items[last];
        _items.RemoveAt(last);
        if (_items.Count > 0) SiftDown(0);

        return true;
    }
    //----------------------------------------------------------------------------------------------------------------------//
    // A new item at the bottom swaps with its parent while it is smaller.
    private void SiftUp(int index)
    {
        while (index > 0)
        {
            var parent = (index - 1) / 2;
            if (!Less(index, parent)) return;

            Swap(index, parent);
            index = parent;
        }
    }

    // An item at the top swaps with its smaller child while that child is smaller than it.
    private void SiftDown(int index)
    {
        while (true)
        {
            var left = 2 * index + 1;
            var right = left + 1;
            var smallest = index;

            if (left < _items.Count && Less(left, smallest)) smallest = left;
            if (right < _items.Count && Less(right, smallest)) smallest = right;
            if (smallest == index) return;

            Swap(index, smallest);
            index = smallest;
        }
    }

    private bool Less(int a, int b) => _items[a].Priority.CompareTo(_items[b].Priority) < 0;

    private void Swap(int a, int b) => (_items[a], _items[b]) = (_items[b], _items[a]);
    //----------------------------------------------------------------------------------------------------------------------//
    /// <summary>True if no parent has a larger priority than either of its children.</summary>
    public bool IsValid()
    {
        for (var i = 1; i < _items.Count; i++)
            if (Less(i, (i - 1) / 2)) return false;

        return true;
    }
}
//---------------------------DDDDDDDDDDDoooooooooooooo END OF FILE DDDDDDDDDDDoooooooooooooo---------------------------//
