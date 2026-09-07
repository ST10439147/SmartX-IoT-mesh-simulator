namespace SmartX.Shared.Telemetry;

/// <summary>
/// Staging area for raw historical telemetry, sitting between the wire and the
/// optimised <c>List&lt;T&gt;</c> collections.
///
/// <para><b>Why jagged and not rectangular.</b> A poll cycle returns however many
/// packets the mesh actually delivered in that window - a congested cycle may yield
/// three readings, a quiet one forty. Rows are genuinely ragged, so <c>float[][]</c>
/// allocates exactly what each cycle needs. A <c>float[cycles, maxSamples]</c> would
/// pad every short row with wasted slots.</para>
///
/// <para><b>Why rectangular anyway, later.</b> The dashboard heat map wants a dense
/// grid with one column per slot, so <see cref="ToDenseMatrix"/> projects the jagged
/// staging area into a true multi-dimensional <c>float[,]</c> once, at read time.
/// Both array forms earn their place; neither is decoration.</para>
///
/// <para>The archive itself is a ring: cycle <c>N + capacity</c> overwrites cycle
/// <c>N</c>, so a long-running gateway holds a bounded history.</para>
/// </summary>
public sealed class TelemetryBatchArchive
{
    private readonly float[][] _cycles;
    private readonly DateTimeOffset[] _cycleStamps;
    private readonly Lock _gate = new();
    private int _head;
    private int _count;

    public TelemetryBatchArchive(int cycleCapacity = 64)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(cycleCapacity, 1);
        _cycles = new float[cycleCapacity][];
        _cycleStamps = new DateTimeOffset[cycleCapacity];
    }

    public int CycleCapacity => _cycles.Length;

    public int CycleCount
    {
        get { lock (_gate) return _count; }
    }

    /// <summary>Widest row currently retained - the column count of the dense projection.</summary>
    public int WidestCycle
    {
        get
        {
            lock (_gate)
            {
                var widest = 0;
                for (var i = 0; i < _count; i++)
                {
                    var row = _cycles[RowIndex(i)];
                    if (row.Length > widest) widest = row.Length;
                }
                return widest;
            }
        }
    }

    /// <summary>Stages one raw poll cycle. The array is copied; callers may reuse their buffer.</summary>
    public void StageCycle(ReadOnlySpan<float> readings, DateTimeOffset at)
    {
        lock (_gate)
        {
            _cycles[_head] = readings.ToArray();
            _cycleStamps[_head] = at;
            _head = (_head + 1) % _cycles.Length;
            if (_count < _cycles.Length) _count++;
        }
    }

    /// <summary>
    /// The hand-off the brief asks for: ragged raw arrays are flattened into a generic
    /// <c>List&lt;TelemetryPacket&lt;float&gt;&gt;</c>, at which point the pipeline stops
    /// dealing in bare arrays and works with typed packets only.
    /// </summary>
    public List<TelemetryPacket<float>> Flush(string sensorId, SensorCategory category)
    {
        lock (_gate)
        {
            var total = 0;
            for (var i = 0; i < _count; i++) total += _cycles[RowIndex(i)].Length;

            // Exact capacity up front - the List never has to grow and re-copy.
            var packets = new List<TelemetryPacket<float>>(total);

            for (var cycle = 0; cycle < _count; cycle++)
            {
                var index = RowIndex(cycle);
                var row = _cycles[index];
                var stamp = _cycleStamps[index];

                for (var sample = 0; sample < row.Length; sample++)
                {
                    // Samples inside a cycle are evenly spaced across that cycle.
                    var offset = TimeSpan.FromMilliseconds(sample * 50);
                    packets.Add(new TelemetryPacket<float>(sensorId, row[sample], category, stamp + offset));
                }
            }

            return packets;
        }
    }

    /// <summary>
    /// Dense rectangular projection for the heat map. Short rows are padded with
    /// <see cref="float.NaN"/> so "no sample" stays distinguishable from "sampled zero".
    /// </summary>
    public float[,] ToDenseMatrix()
    {
        lock (_gate)
        {
            var widest = 0;
            for (var i = 0; i < _count; i++)
            {
                var row = _cycles[RowIndex(i)];
                if (row.Length > widest) widest = row.Length;
            }

            var dense = new float[_count, Math.Max(widest, 1)];

            for (var cycle = 0; cycle < _count; cycle++)
            {
                var row = _cycles[RowIndex(cycle)];
                for (var sample = 0; sample < dense.GetLength(1); sample++)
                    dense[cycle, sample] = sample < row.Length ? row[sample] : float.NaN;
            }

            return dense;
        }
    }

    /// <summary>Jagged view for callers that want the ragged shape preserved, oldest cycle first.</summary>
    public float[][] ToJagged()
    {
        lock (_gate)
        {
            var rows = new float[_count][];
            for (var i = 0; i < _count; i++) rows[i] = (float[])_cycles[RowIndex(i)].Clone();
            return rows;
        }
    }

    // Caller-relative index (0 = oldest) to physical slot in the ring.
    private int RowIndex(int logical)
    {
        var start = _count == _cycles.Length ? _head : 0;
        return (start + logical) % _cycles.Length;
    }
}
