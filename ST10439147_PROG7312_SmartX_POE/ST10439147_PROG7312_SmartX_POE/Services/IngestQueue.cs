namespace SmartX.Api.Services;

/// <summary>
/// One reading waiting to be ingested. The value rides as a double - float, int and
/// bool (0/1) all survive that round trip exactly - and the worker turns it back into
/// the sensor's own <c>TelemetryPacket&lt;T&gt;</c> when it lands in the store.
/// <see cref="ReceivedAt"/> is stamped at the door, so a packet that waited in a backlog
/// still reports when the node sent it, not when the gateway got round to it.
/// </summary>
public readonly record struct QueuedPacket(long Sequence, string SensorId, double Value, DateTimeOffset ReceivedAt);

/// <summary>
/// The waiting line between the ingest endpoint and the store. The endpoint enqueues and
/// returns; a single background worker dequeues in arrival order (FIFO), so a burst from
/// the mesh costs the nodes nothing but a 202.
/// </summary>
// Queue<T> is not thread-safe and many requests enqueue while one worker dequeues, so
// every touch goes through the lock. The semaphore counts waiting packets: the worker
// sleeps on it instead of spinning on an empty queue.
public sealed class IngestQueue
{
    /// <summary>Past this the gateway sheds load (503) rather than queueing until it runs out of memory.</summary>
    public const int MaxDepth = 10_000;

    private readonly Queue<QueuedPacket> _normal = new();
    private readonly SemaphoreSlim _available = new(0);
    private readonly Lock _gate = new();

    private long _sequence;

    public int Depth
    {
        get { lock (_gate) return _normal.Count; }
    }
    //----------------------------------------------------------------------------------------------------------------------//
    /// <summary>Joins the back of the line. False means the queue is full and the packet was dropped.</summary>
    public bool TryEnqueue(string sensorId, double value)
    {
        lock (_gate)
        {
            if (_normal.Count >= MaxDepth) return false;
            _normal.Enqueue(new QueuedPacket(++_sequence, sensorId, value, DateTimeOffset.UtcNow));
        }

        _available.Release();
        return true;
    }
    //----------------------------------------------------------------------------------------------------------------------//
    /// <summary>Takes the packet at the front of the line, waiting if there is none.</summary>
    public async ValueTask<QueuedPacket> DequeueAsync(CancellationToken token = default)
    {
        await _available.WaitAsync(token);
        lock (_gate) return _normal.Dequeue();
    }
}
//---------------------------DDDDDDDDDDDoooooooooooooo END OF FILE DDDDDDDDDDDoooooooooooooo---------------------------//
