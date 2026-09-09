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
/// </summary>
// ponytail: in-memory only, wiped on restart. Swap the dictionaries for a time-series
// store (or even SQLite) if history has to outlive the process.
public sealed class TelemetryStore
{
    private const int WindowPerSensor = 240;   // ~8 minutes at a 2s publish interval

    private readonly ConcurrentDictionary<string, SensorRegistration> _sensors = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, CircularTelemetryBuffer<float>> _floats = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, CircularTelemetryBuffer<int>> _integers = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, CircularTelemetryBuffer<bool>> _booleans = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, TelemetryBatchArchive> _archives = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, List<AttachmentInfo>> _attachments = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, DateTimeOffset> _lastSeen = new(StringComparer.OrdinalIgnoreCase);

    private long _packetsIngested;

    public DateTimeOffset StartedAt { get; } = DateTimeOffset.UtcNow;

    public long PacketsIngested => Interlocked.Read(ref _packetsIngested);

    /// <summary>The deployment tree every registration path is validated against.</summary>
    public DeploymentNode Topology { get; } = SeedTopology();

    public IReadOnlyCollection<SensorRegistration> Sensors => _sensors.Values.ToList();

    public bool TryGet(string id, out SensorRegistration sensor) => _sensors.TryGetValue(id, out sensor!);

    public bool Exists(string id) => _sensors.ContainsKey(id);

    /// <summary>Registers a node and allocates its typed ring buffer. Idempotent on the MAC.</summary>
    public SensorRegistration Register(SensorRegistration sensor)
    {
        sensor.Id = sensor.MacAddress.Replace('-', ':').ToUpperInvariant();
        sensor.RegisteredAt = DateTimeOffset.UtcNow;
        if (string.IsNullOrWhiteSpace(sensor.Unit)) sensor.Unit = sensor.DefaultUnit();
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
        return sensor;
    }

    public bool Remove(string id)
    {
        _floats.TryRemove(id, out _);
        _integers.TryRemove(id, out _);
        _booleans.TryRemove(id, out _);
        _archives.TryRemove(id, out _);
        _attachments.TryRemove(id, out _);
        _lastSeen.TryRemove(id, out _);
        return _sensors.TryRemove(id, out _);
    }

    // ---- ingest ------------------------------------------------------------

    public void Ingest(in TelemetryPacket<float> packet)
    {
        if (_floats.TryGetValue(packet.SensorId, out var buffer)) buffer.Add(in packet);
        Touch(packet.SensorId, packet.TimestampUtc);
    }

    public void Ingest(in TelemetryPacket<int> packet)
    {
        if (_integers.TryGetValue(packet.SensorId, out var buffer)) buffer.Add(in packet);
        Touch(packet.SensorId, packet.TimestampUtc);
    }

    public void Ingest(in TelemetryPacket<bool> packet)
    {
        if (_booleans.TryGetValue(packet.SensorId, out var buffer)) buffer.Add(in packet);
        Touch(packet.SensorId, packet.TimestampUtc);
    }

    /// <summary>Stages a whole ragged poll cycle in the jagged archive.</summary>
    public void StageCycle(string sensorId, ReadOnlySpan<float> readings, DateTimeOffset at)
    {
        if (_archives.TryGetValue(sensorId, out var archive)) archive.StageCycle(readings, at);
    }

    private void Touch(string sensorId, DateTimeOffset at)
    {
        _lastSeen[sensorId] = at;
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

    /// <summary>Latest value as a double for display purposes only; the stored packet keeps its real type.</summary>
    public double LatestNumeric(string sensorId)
    {
        if (_floats.TryGetValue(sensorId, out var f) && f.TryGetNewest(out var fp)) return fp.Value;
        if (_integers.TryGetValue(sensorId, out var i) && i.TryGetNewest(out var ip)) return ip.Value;
        if (_booleans.TryGetValue(sensorId, out var b) && b.TryGetNewest(out var bp)) return bp.Value ? 1 : 0;
        return double.NaN;
    }

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

    public IReadOnlyList<AttachmentInfo> Attachments(string sensorId)
    {
        if (!_attachments.TryGetValue(sensorId, out var list)) return [];
        lock (list) return list.ToList();
    }

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
