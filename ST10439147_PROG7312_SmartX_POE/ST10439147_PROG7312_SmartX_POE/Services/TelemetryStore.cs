using System.Collections.Concurrent;
using SmartX.Shared.Telemetry;
using SmartX.Shared.Topology;

namespace SmartX.Api.Services;

/// <summary>
/// In-memory gateway state.
///
/// <para><b>The typed buckets are the point.</b> A float sensor and a bool sensor never
/// share a <c>List&lt;object&gt;</c> or an <c>ITelemetryPacket</c> with an
/// <c>object Value</c> property - that is the usual place a "no boxing" generic design
/// quietly starts boxing again. Each payload type gets its own dictionary of typed ring
/// buffers, and <see cref="SensorRegistration.Payload"/> says which one a node belongs
/// to. Nothing on the ingest path ever touches <see cref="object"/>.</para>
///
/// <para><b>The device registry is a dictionary.</b> <c>_sensors</c> maps a node's key to
/// its registration, and it is consulted on every packet arrival - once at the ingest
/// door and once more when the worker lands the packet. A hash lookup is O(1) on
/// average, so the cost of finding a device does not grow with the size of the mesh; a
/// <c>List</c> scan would be O(n) per packet. The key is the MAC in canonical form
/// (<see cref="KeyFor"/>), which is also the id the gateway hands back, so a node can be
/// found by either, however the MAC was typed.</para>
///
/// <para><b>Each sensor's timeline is a sorted dictionary.</b> The ring buffers hold
/// packets in the order they arrived, which is what a rolling baseline wants. A timeline
/// wants them in the order they were <em>taken</em>, and a mesh does not deliver in that
/// order. <c>_timelines</c> keeps a <see cref="SortedDictionary{TKey,TValue}"/> per sensor
/// keyed by timestamp: an insert is O(log n) wherever the key falls, and reading it back
/// is already in time order, so a late packet needs no re-sort.</para>
/// </summary>
// ponytail: in-memory only, wiped on restart. Swap the dictionaries for a time-series
// store (or even SQLite) if history has to outlive the process.
// Reference [1]: Microsoft. Generics in C#. Microsoft Learn.
//   https://learn.microsoft.com/dotnet/csharp/fundamentals/types/generics
// Reference [18]: Microsoft. ConcurrentDictionary<TKey,TValue> Class. Microsoft Learn.
//   https://learn.microsoft.com/dotnet/api/system.collections.concurrent.concurrentdictionary-2
public sealed class TelemetryStore
{
    private const int WindowPerSensor = 240;   // ~8 minutes at a 2s publish interval

    /// <summary>Readings kept on each sensor's timeline before the oldest is dropped.</summary>
    public const int TimelineCapacity = 240;

    private readonly ConcurrentDictionary<string, SensorRegistration> _sensors = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, CircularTelemetryBuffer<float>> _floats = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, CircularTelemetryBuffer<int>> _integers = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, CircularTelemetryBuffer<bool>> _booleans = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, TelemetryBatchArchive> _archives = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, List<AttachmentInfo>> _attachments = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, DateTimeOffset> _lastSeen = new(StringComparer.OrdinalIgnoreCase);

    // SortedDictionary is not thread-safe: the worker writes while requests read, so
    // every touch of one sensor's log locks that log.
    // Reference [54]: Microsoft. SortedDictionary<TKey,TValue> Class. Microsoft Learn.
    //   https://learn.microsoft.com/dotnet/api/system.collections.generic.sorteddictionary-2
    private readonly ConcurrentDictionary<string, SortedDictionary<(DateTimeOffset At, long Sequence), TimelinePoint>> _timelines = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Readings kept in each sensor's long-term history tree: a day at a 2s interval.</summary>
    public const int HistoryCapacity = 50_000;

    // The long-term history: one AVL tree per sensor, same (timestamp, sequence) key as the
    // timeline. The timeline is the short, recent window the dashboard graphs; the tree holds
    // the whole day and answers "readings between 14:00 and 16:00" without a scan.
    // Locked per sensor for the same reason as the timeline.
    private readonly ConcurrentDictionary<string, AvlTree<(DateTimeOffset At, long Sequence), TimelinePoint>> _history = new(StringComparer.OrdinalIgnoreCase);

    private long _packetsIngested;
    private long _lookups;
    private long _misses;

    public DateTimeOffset StartedAt { get; } = DateTimeOffset.UtcNow;

    public long PacketsIngested => Interlocked.Read(ref _packetsIngested);

    /// <summary>The deployment tree every registration path is validated against.</summary>
    public DeploymentNode Topology { get; } = SeedTopology();

    public IReadOnlyCollection<SensorRegistration> Sensors => _sensors.Values.ToList();

    /// <summary>
    /// The registry key for a MAC or id: colons, upper case. <c>a4-cf-12-9b-04-7e</c> and
    /// <c>A4:CF:12:9B:04:7E</c> are the same device and must hash to the same entry.
    /// </summary>
    public static string KeyFor(string macOrId) => macOrId.Trim().Replace('-', ':').ToUpperInvariant();

    /// <summary>The registry lookup: one hash probe by MAC or id, counted so it can be shown.</summary>
    // Reference [36]: Microsoft. Dictionary<TKey,TValue> Class. Microsoft Learn.
    //   https://learn.microsoft.com/dotnet/api/system.collections.generic.dictionary-2
    public bool TryGet(string macOrId, out SensorRegistration sensor)
    {
        // Reference [44]: Microsoft. Interlocked Class. Microsoft Learn.
        //   https://learn.microsoft.com/dotnet/api/system.threading.interlocked
        Interlocked.Increment(ref _lookups);
        if (_sensors.TryGetValue(KeyFor(macOrId), out sensor!)) return true;

        Interlocked.Increment(ref _misses);
        return false;
    }

    public bool Exists(string macOrId) => _sensors.ContainsKey(KeyFor(macOrId));

    /// <summary>Registry lookups since boot, and how many of them found no device.</summary>
    public long Lookups => Interlocked.Read(ref _lookups);

    public long Misses => Interlocked.Read(ref _misses);
    //----------------------------------------------------------------------------------------------------------------------//

    /// <summary>Registers a node and allocates its typed ring buffer. Idempotent on the MAC.</summary>
    public SensorRegistration Register(SensorRegistration sensor)
    {
        sensor.Id = KeyFor(sensor.MacAddress);
        sensor.RegisteredAt = DateTimeOffset.UtcNow;
        _sensors[sensor.Id] = sensor;

        switch (sensor.Payload)
        {
            case PayloadKind.Float:
                _floats.TryAdd(sensor.Id, new CircularTelemetryBuffer<float>(WindowPerSensor));
                _archives.TryAdd(sensor.Id, new TelemetryBatchArchive(cycleCapacity: 48));
                break;
            case PayloadKind.Integer:
                _integers.TryAdd(sensor.Id, new CircularTelemetryBuffer<int>(WindowPerSensor));
                break;
            case PayloadKind.Boolean:
                _booleans.TryAdd(sensor.Id, new CircularTelemetryBuffer<bool>(WindowPerSensor));
                break;
        }

        _attachments.TryAdd(sensor.Id, []);
        _timelines.TryAdd(sensor.Id, []);
        _history.TryAdd(sensor.Id, new());
        return sensor;
    }
    //----------------------------------------------------------------------------------------------------------------------//
    public bool Remove(string id)
    {
        id = KeyFor(id);
        _floats.TryRemove(id, out _);
        _integers.TryRemove(id, out _);
        _booleans.TryRemove(id, out _);
        _archives.TryRemove(id, out _);
        _attachments.TryRemove(id, out _);
        _lastSeen.TryRemove(id, out _);
        _timelines.TryRemove(id, out _);
        _history.TryRemove(id, out _);
        return _sensors.TryRemove(id, out _);
    }

    // ---- ingest ------------------------------------------------------------

    // Reference [10]: Microsoft. Structure types - readonly struct and in parameters. C# language reference, Microsoft Learn.
    //   https://learn.microsoft.com/dotnet/csharp/language-reference/builtin-types/struct
    public void Ingest(in TelemetryPacket<float> packet)
    {
        if (_floats.TryGetValue(packet.SensorId, out var buffer)) buffer.Add(in packet);
        Touch(packet.SensorId, packet.TimestampUtc);
    }
    //----------------------------------------------------------------------------------------------------------------------//
    public void Ingest(in TelemetryPacket<int> packet)
    {
        if (_integers.TryGetValue(packet.SensorId, out var buffer)) buffer.Add(in packet);
        Touch(packet.SensorId, packet.TimestampUtc);
    }
    //----------------------------------------------------------------------------------------------------------------------//
    public void Ingest(in TelemetryPacket<bool> packet)
    {
        if (_booleans.TryGetValue(packet.SensorId, out var buffer)) buffer.Add(in packet);
        Touch(packet.SensorId, packet.TimestampUtc);
    }
    //----------------------------------------------------------------------------------------------------------------------//
    /// <summary>Stages a whole ragged poll cycle in the jagged archive.</summary>
    // Reference [15]: Microsoft. Span<T> Struct. Microsoft Learn.
    //   https://learn.microsoft.com/dotnet/api/system.span-1
    public void StageCycle(string sensorId, ReadOnlySpan<float> readings, DateTimeOffset at)
    {
        if (_archives.TryGetValue(sensorId, out var archive)) archive.StageCycle(readings, at);
    }
    //----------------------------------------------------------------------------------------------------------------------//
    /// <summary>
    /// Files a reading on the sensor's timeline under the time it was taken. Arrival order
    /// does not matter: the sorted dictionary puts it where its timestamp belongs.
    /// </summary>
    // The key is (timestamp, sequence): sorted by time first, and the arrival sequence keeps
    // two readings with the same timestamp from overwriting each other.
    public void LogReading(string sensorId, DateTimeOffset at, double value, long sequence)
    {
        if (!_timelines.TryGetValue(sensorId, out var log)) return;

        var point = new TimelinePoint(at, value, sequence);
        lock (log)
        {
            log[(at, sequence)] = point;

            // Bounded like the ring buffers. The first key is always the oldest reading.
            if (log.Count > TimelineCapacity) log.Remove(log.Keys.First());
        }

        LogHistory(sensorId, point);
    }
    //----------------------------------------------------------------------------------------------------------------------//
    /// <summary>
    /// Files a reading in the sensor's AVL history tree only. The live path reaches this
    /// through <see cref="LogReading"/>; the simulator calls it directly to seed a day of
    /// history at boot. O(log n) per insert, and dropping the oldest is O(log n) too.
    /// </summary>
    public void LogHistory(string sensorId, TimelinePoint point)
    {
        if (!_history.TryGetValue(sensorId, out var tree)) return;

        lock (tree)
        {
            tree.Add((point.At, point.Sequence), point);
            if (tree.Count > HistoryCapacity) tree.TryRemoveMin(out _, out _);
        }
    }
    //----------------------------------------------------------------------------------------------------------------------//
    private void Touch(string sensorId, DateTimeOffset at)
    {
        // A packet that arrives late carries an old timestamp; it must not wind the
        // node's last-seen time backwards.
        _lastSeen.AddOrUpdate(sensorId, at, (_, seen) => at > seen ? at : seen);
        Interlocked.Increment(ref _packetsIngested);
    }

    // ---- read paths --------------------------------------------------------

    public DateTimeOffset? LastSeen(string sensorId)
        => _lastSeen.TryGetValue(sensorId, out var at) ? at : null;

    public CircularTelemetryBuffer<float>? Floats(string sensorId)
        => _floats.GetValueOrDefault(sensorId);

    public CircularTelemetryBuffer<int>? Integers(string sensorId)
        => _integers.GetValueOrDefault(sensorId);

    public CircularTelemetryBuffer<bool>? Booleans(string sensorId)
        => _booleans.GetValueOrDefault(sensorId);

    public TelemetryBatchArchive? Archive(string sensorId)
        => _archives.GetValueOrDefault(sensorId);

    /// <summary>The newest <paramref name="samples"/> readings, oldest first, in timestamp order.</summary>
    public IReadOnlyList<TimelinePoint> Timeline(string sensorId, int samples)
    {
        if (!_timelines.TryGetValue(sensorId, out var log)) return [];
        lock (log) return log.Values.Skip(Math.Max(0, log.Count - Math.Max(1, samples))).ToList();
    }
    //----------------------------------------------------------------------------------------------------------------------//
    /// <summary>
    /// Every reading taken between <paramref name="from"/> and <paramref name="to"/>
    /// (inclusive), oldest first, from the sensor's AVL tree. Null if the sensor is unknown.
    /// </summary>
    // Sequence spans long.MinValue..MaxValue so every reading at the boundary timestamps is included.
    public HistoryRange? QueryHistory(string sensorId, DateTimeOffset from, DateTimeOffset to)
    {
        if (!_history.TryGetValue(sensorId, out var tree)) return null;

        lock (tree)
        {
            var points = tree.Range((from, long.MinValue), (to, long.MaxValue), out var visited);
            return new HistoryRange(sensorId, from, to, points, tree.Count, tree.Height, visited);
        }
    }
    //----------------------------------------------------------------------------------------------------------------------//

    /// <summary>Latest value as a double for display purposes only; the stored packet keeps its real type.</summary>
    public double LatestNumeric(string sensorId)
    {
        if (_floats.TryGetValue(sensorId, out var f) && f.TryGetNewest(out var fp)) return fp.Value;
        if (_integers.TryGetValue(sensorId, out var i) && i.TryGetNewest(out var ip)) return ip.Value;
        if (_booleans.TryGetValue(sensorId, out var b) && b.TryGetNewest(out var bp)) return bp.Value ? 1 : 0;
        return double.NaN;
    }
    //----------------------------------------------------------------------------------------------------------------------//
    public IReadOnlyList<double> Window(string sensorId, int samples)
    {
        if (_floats.TryGetValue(sensorId, out var f))
            return f.TakeLast(samples).Select(p => (double)p.Value).ToList();
        if (_integers.TryGetValue(sensorId, out var i))
            return i.TakeLast(samples).Select(p => (double)p.Value).ToList();
        if (_booleans.TryGetValue(sensorId, out var b))
            return b.TakeLast(samples).Select(p => p.Value ? 1d : 0d).ToList();
        return [];
    }
    //----------------------------------------------------------------------------------------------------------------------//
    public IReadOnlyList<ContextSample> ContextAround(string sensorId, DateTimeOffset at, int span = 4)
    {
        var packets = _floats.TryGetValue(sensorId, out var f)
            ? f.ToList().Select(p => (p.TimestampUtc, (double)p.Value)).ToList()
            : _integers.TryGetValue(sensorId, out var i)
                ? i.ToList().Select(p => (p.TimestampUtc, (double)p.Value)).ToList()
                : _booleans.TryGetValue(sensorId, out var b)
                    ? b.ToList().Select(p => (p.TimestampUtc, p.Value ? 1d : 0d)).ToList()
                    : [];

        if (packets.Count == 0) return [];

        var offending = packets.FindIndex(p => p.TimestampUtc >= at);
        if (offending < 0) offending = packets.Count - 1;

        var start = Math.Max(0, offending - span);
        var end = Math.Min(packets.Count - 1, offending + span);

        return packets
            .GetRange(start, end - start + 1)
            .Select((p, index) => new ContextSample(p.TimestampUtc, p.Item2, start + index == offending))
            .ToList();
    }
    //----------------------------------------------------------------------------------------------------------------------//
    public long PacketCount(string sensorId)
    {
        if (_floats.TryGetValue(sensorId, out var f)) return f.TotalWritten;
        if (_integers.TryGetValue(sensorId, out var i)) return i.TotalWritten;
        if (_booleans.TryGetValue(sensorId, out var b)) return b.TotalWritten;
        return 0;
    }

    // ---- attachments -------------------------------------------------------

    public void AddAttachment(AttachmentInfo info)
    {
        var list = _attachments.GetOrAdd(info.SensorId, _ => []);
        lock (list) list.Add(info);
    }
    //----------------------------------------------------------------------------------------------------------------------//
    public IReadOnlyList<AttachmentInfo> Attachments(string sensorId)
    {
        if (!_attachments.TryGetValue(sensorId, out var list)) return [];
        lock (list) return list.ToList();
    }
    //----------------------------------------------------------------------------------------------------------------------//
    public AttachmentInfo? FindAttachment(string attachmentId)
    {
        foreach (var list in _attachments.Values)
        {
            lock (list)
            {
                var hit = list.FirstOrDefault(a => a.Id == attachmentId);
                if (hit is not null) return hit;
            }
        }

        return null;
    }
    //----------------------------------------------------------------------------------------------------------------------//
    /// <summary>Seed hierarchy: Facility A &gt; Zone 1..2 &gt; Sub-Zone A..B &gt; nodes.</summary>
    private static DeploymentNode SeedTopology() =>
        DeploymentNode.Create("Facility A", NodeKind.Facility,
            DeploymentNode.Create("Zone 1", NodeKind.Zone,
                DeploymentNode.Create("Sub-Zone A", NodeKind.SubZone),
                DeploymentNode.Create("Sub-Zone B", NodeKind.SubZone)),
            DeploymentNode.Create("Zone 2", NodeKind.Zone,
                DeploymentNode.Create("Sub-Zone C", NodeKind.SubZone),
                DeploymentNode.Create("Roof Array", NodeKind.SubZone)));
}
//---------------------------DDDDDDDDDDDoooooooooooooo END OF FILE DDDDDDDDDDDoooooooooooooo---------------------------//
