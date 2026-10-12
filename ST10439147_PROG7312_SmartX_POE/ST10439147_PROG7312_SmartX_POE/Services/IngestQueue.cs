using SmartX.Shared.Telemetry;

namespace SmartX.Api.Services;

/// <summary>
/// One reading waiting to be ingested. The value rides as a double - float, int and
/// bool (0/1) all survive that round trip exactly - and the worker turns it back into
/// the sensor's own <c>TelemetryPacket&lt;T&gt;</c> when it lands in the store.
/// <see cref="ReceivedAt"/> is stamped at the door, so a packet that waited in a backlog
/// still reports when the node sent it, not when the gateway got round to it.
/// <see cref="Critical"/> is why it jumped the line, or null if it waited its turn.
/// </summary>
public readonly record struct QueuedPacket(
    long Sequence, string SensorId, double Value, DateTimeOffset ReceivedAt, AlertKind? Critical = null);

/// <summary>
/// The waiting line between the ingest endpoint and the store. The endpoint enqueues and
/// returns; a single background worker dequeues, so a burst from the mesh costs the nodes
/// nothing but a 202.
///
/// <para>Two lanes. Ordinary packets wait in a <see cref="Queue{T}"/> and leave in
/// arrival order (FIFO). A packet that would breach a threshold or is a spike goes into a
/// <see cref="PriorityQueue{TElement,TPriority}"/> instead, and the worker always empties
/// that lane first - so a fault is never stuck behind a backlog of healthy readings.</para>
/// </summary>
// Neither collection is thread-safe and many requests enqueue while one worker dequeues,
// so every touch goes through the lock. The semaphore counts waiting packets across both
// lanes: the worker sleeps on it instead of spinning on an empty queue.
// Reference: Microsoft. Queue<T> Class. Microsoft Learn.
//   https://learn.microsoft.com/dotnet/api/system.collections.generic.queue-1
// Reference: Microsoft. PriorityQueue<TElement,TPriority> Class. Microsoft Learn.
//   https://learn.microsoft.com/dotnet/api/system.collections.generic.priorityqueue-2
/// <summary>
/// A packet the worker has finished with. <see cref="Position"/> is the order it was
/// processed in; the packet's own Sequence is the order it arrived in. When the two
/// disagree, something jumped the line.
/// </summary>
public readonly record struct ProcessedPacket(long Position, QueuedPacket Packet, DateTimeOffset ProcessedAt);

public sealed class IngestQueue
{
    private const int RecentKept = 200;

    /// <summary>Per lane. Past this the gateway sheds load (503) rather than queueing until it runs out of memory.</summary>
    public const int MaxDepth = 10_000;

    private readonly Queue<QueuedPacket> _normal = new();

    // Lowest priority leaves first. Rank puts spikes ahead of threshold breaches; the
    // sequence number breaks ties, because PriorityQueue on its own does not promise
    // that two equal-priority packets come out in the order they went in.
    private readonly PriorityQueue<QueuedPacket, (int Rank, long Sequence)> _critical = new();

    private readonly SemaphoreSlim _available = new(0);
    private readonly Lock _gate = new();

    // The processing-order trail is itself a FIFO queue: newest in, oldest out.
    private readonly Queue<ProcessedPacket> _recent = new();

    private long _sequence;
    private long _processed;

    /// <summary>
    /// Test knob: an artificial per-packet delay the worker honours so a flood stays
    /// backed up long enough to watch. Clears itself once both lanes are empty.
    /// </summary>
    public TimeSpan Throttle { get; set; }

    public long Processed
    {
        get { lock (_gate) return _processed; }
    }

    /// <summary>The last few packets in the order the worker processed them, oldest first.</summary>
    public IReadOnlyList<ProcessedPacket> Recent
    {
        get { lock (_gate) return _recent.ToList(); }
    }

    public int Depth
    {
        get { lock (_gate) return _normal.Count; }
    }

    public int CriticalDepth
    {
        get { lock (_gate) return _critical.Count; }
    }
    //----------------------------------------------------------------------------------------------------------------------//
    /// <summary>
    /// Joins the back of the normal line, or the priority lane if <paramref name="critical"/>
    /// is set. False means that lane is full and the packet was dropped.
    /// </summary>
    public bool TryEnqueue(string sensorId, double value, AlertKind? critical = null)
    {
        lock (_gate)
        {
            if ((critical is null ? _normal.Count : _critical.Count) >= MaxDepth) return false;

            var packet = new QueuedPacket(++_sequence, sensorId, value, DateTimeOffset.UtcNow, critical);

            if (critical is null) _normal.Enqueue(packet);
            else _critical.Enqueue(packet, (critical == AlertKind.Spike ? 0 : 1, packet.Sequence));
        }

        _available.Release();
        return true;
    }
    //----------------------------------------------------------------------------------------------------------------------//
    /// <summary>Takes the next packet, waiting if there is none. Critical lane first, always.</summary>
    public async ValueTask<QueuedPacket> DequeueAsync(CancellationToken token = default)
    {
        await _available.WaitAsync(token);
        lock (_gate) return _critical.Count > 0 ? _critical.Dequeue() : _normal.Dequeue();
    }
    //----------------------------------------------------------------------------------------------------------------------//
    /// <summary>The worker reports back here once a packet is done, building the processing-order trail.</summary>
    public void MarkProcessed(in QueuedPacket packet)
    {
        lock (_gate)
        {
            _recent.Enqueue(new ProcessedPacket(++_processed, packet, DateTimeOffset.UtcNow));
            if (_recent.Count > RecentKept) _recent.Dequeue();

            if (_normal.Count + _critical.Count == 0) Throttle = TimeSpan.Zero;
        }
    }
}
//---------------------------DDDDDDDDDDDoooooooooooooo END OF FILE DDDDDDDDDDDoooooooooooooo---------------------------//
