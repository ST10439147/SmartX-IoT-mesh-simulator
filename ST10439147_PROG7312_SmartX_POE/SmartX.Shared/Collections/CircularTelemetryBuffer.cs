using System.Collections;

namespace SmartX.Shared.Telemetry;

/// <summary>
/// Fixed-capacity ring buffer of <see cref="TelemetryPacket{T}"/>, backed by a single
/// pre-allocated array.
///
/// <para>Why a custom structure rather than <c>List&lt;T&gt;</c>: thousands of ESP32
/// nodes publishing every couple of seconds is an unbounded stream, and a
/// <c>List&lt;T&gt;</c> would grow until the gateway died. Retaining the last N packets
/// per node bounds memory at <c>nodes x capacity</c> with no reallocation and no copying
/// - <see cref="Add"/> is O(1) forever, where <c>List.RemoveAt(0)</c> would be O(n) on
/// every single packet.</para>
///
/// <para>The window it keeps is also exactly what the anomaly detector needs for its
/// rolling mean and standard deviation, so one structure serves both jobs.</para>
/// </summary>
// Reference [1]: Microsoft. Generics in C#. Microsoft Learn.
//   https://learn.microsoft.com/dotnet/csharp/fundamentals/types/generics
// Reference [12]: Wikipedia. Circular buffer.
//   https://en.wikipedia.org/wiki/Circular_buffer
public sealed class CircularTelemetryBuffer<T> : IEnumerable<TelemetryPacket<T>> where T : struct
{
    private readonly TelemetryPacket<T>[] _items;
    // Reference [39]: Microsoft. Lock Class (System.Threading). Microsoft Learn.
    //   https://learn.microsoft.com/dotnet/api/system.threading.lock
    private readonly Lock _gate = new();
    private int _head;   // next slot to write
    private int _count;

    public CircularTelemetryBuffer(int capacity)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 1);
        _items = new TelemetryPacket<T>[capacity];
    }

    public int Capacity => _items.Length;

    public int Count
    {
        get { lock (_gate) return _count; }
    }

    /// <summary>Lifetime packet count, including those already overwritten.</summary>
    public long TotalWritten { get; private set; }

    public bool IsEmpty => Count == 0;
    //----------------------------------------------------------------------------------------------------------------------//
    /// <summary>
    /// Overwrites the oldest packet once full. Taken by <c>in</c> so the struct is
    /// passed by reference rather than copied on the way in.
    /// </summary>
    // ponytail: one lock for the whole ring. Ingest is per-sensor so contention is
    // already sharded by having one buffer per node; split further only if a single
    // node ever outruns one core.
    // Reference [10]: Microsoft. Structure types - readonly struct and in parameters. C# language reference, Microsoft Learn.
    //   https://learn.microsoft.com/dotnet/csharp/language-reference/builtin-types/struct
    // Reference [12]: Wikipedia. Circular buffer.
    //   https://en.wikipedia.org/wiki/Circular_buffer
    public void Add(in TelemetryPacket<T> packet)
    {
        lock (_gate)
        {
            _items[_head] = packet;
            _head = (_head + 1) % _items.Length;
            if (_count < _items.Length) _count++;
            TotalWritten++;
        }
    }
    //----------------------------------------------------------------------------------------------------------------------//
    /// <summary>Index 0 is the oldest retained packet, <c>Count - 1</c> the newest.</summary>
    public TelemetryPacket<T> this[int index]
    {
        get
        {
            lock (_gate)
            {
                if ((uint)index >= (uint)_count)
                    throw new ArgumentOutOfRangeException(nameof(index), index, $"Buffer holds {_count} packets.");

                // Oldest sits at _head when full, at 0 while still filling.
                var start = _count == _items.Length ? _head : 0;
                return _items[(start + index) % _items.Length];
            }
        }
    }
    //----------------------------------------------------------------------------------------------------------------------//
    public bool TryGetNewest(out TelemetryPacket<T> packet)
    {
        lock (_gate)
        {
            if (_count == 0)
            {
                packet = default;
                return false;
            }

            packet = _items[(_head - 1 + _items.Length) % _items.Length];
            return true;
        }
    }
    //----------------------------------------------------------------------------------------------------------------------//
    /// <summary>
    /// Snapshot into a generic <c>List&lt;T&gt;</c> - the hand-off point where the raw
    /// ingestion window becomes an ordinary collection for querying and serialisation.
    /// </summary>
    public List<TelemetryPacket<T>> ToList()
    {
        lock (_gate)
        {
            var copy = new List<TelemetryPacket<T>>(_count);
            var start = _count == _items.Length ? _head : 0;
            for (var i = 0; i < _count; i++)
                copy.Add(_items[(start + i) % _items.Length]);
            return copy;
        }
    }
    //----------------------------------------------------------------------------------------------------------------------//
    /// <summary>Most recent <paramref name="count"/> packets, oldest first.</summary>
    public List<TelemetryPacket<T>> TakeLast(int count)
    {
        var all = ToList();
        if (count >= all.Count) return all;
        return all.GetRange(all.Count - count, count);
    }
    //----------------------------------------------------------------------------------------------------------------------//
    public void Clear()
    {
        lock (_gate)
        {
            Array.Clear(_items);
            _head = 0;
            _count = 0;
        }
    }
    //----------------------------------------------------------------------------------------------------------------------//
    // Enumerates a snapshot, so a packet arriving mid-loop cannot invalidate the walk.
    public IEnumerator<TelemetryPacket<T>> GetEnumerator() => ToList().GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}
//---------------------------DDDDDDDDDDDoooooooooooooo END OF FILE DDDDDDDDDDDoooooooooooooo---------------------------//
