using System.Numerics;

namespace SmartX.Shared.Telemetry;

/// <summary>
/// The numeric half of the telemetry model. Constrained to <c>INumber&lt;T&gt;</c>
/// (.NET generic math) so <c>+</c>, <c>-</c> and the relational operators can be
/// written once and work for <c>float</c>, <c>int</c>, <c>double</c> or <c>decimal</c>
/// alike - no per-type overloads, no <c>dynamic</c>, no boxing.
///
/// <para>Gives the gateway direct expressions instead of helper calls:</para>
/// <code>
/// var combinedLoad = meter1 + meter2;   // aggregate two smart meters
/// var drift        = probeNow - probeThen;
/// if (probeNow > 80f) RaiseAlert();     // compare straight against a bare float
/// </code>
/// </summary>
public readonly struct SensorReading<T> : IEquatable<SensorReading<T>>, IComparable<SensorReading<T>>
    where T : struct, INumber<T>
{
    public string SensorId { get; }
    public T Value { get; }
    public DateTimeOffset TimestampUtc { get; }

    public SensorReading(string sensorId, T value, DateTimeOffset timestampUtc)
    {
        SensorId = sensorId;
        Value = value;
        TimestampUtc = timestampUtc;
    }

    public SensorReading(string sensorId, T value) : this(sensorId, value, DateTimeOffset.UtcNow)
    {
    }

    /// <summary>Zero-valued reading for the same node. <c>T.Zero</c> comes from generic math.</summary>
    public SensorReading<T> Zeroed() => new(SensorId, T.Zero, TimestampUtc);

    // ---- aggregation -------------------------------------------------------
    // Meter3 = Meter1 + Meter2. The composite keeps both source ids so an
    // aggregate tile on the dashboard can still name what it is made of, and
    // takes the later timestamp because an aggregate is only as fresh as its
    // stalest input... which is the earlier one, hence Min, not Max.

    public static SensorReading<T> operator +(SensorReading<T> left, SensorReading<T> right)
        => new($"{left.SensorId}+{right.SensorId}",
               left.Value + right.Value,
               left.TimestampUtc < right.TimestampUtc ? left.TimestampUtc : right.TimestampUtc);

    public static SensorReading<T> operator -(SensorReading<T> left, SensorReading<T> right)
        => new($"{left.SensorId}-{right.SensorId}",
               left.Value - right.Value,
               left.TimestampUtc < right.TimestampUtc ? left.TimestampUtc : right.TimestampUtc);

    /// <summary>Unary negation - useful for expressing a meter that is exporting rather than drawing.</summary>
    public static SensorReading<T> operator -(SensorReading<T> reading)
        => new(reading.SensorId, -reading.Value, reading.TimestampUtc);

    // ---- equality ----------------------------------------------------------
    // Overloading == obliges us to override Equals and GetHashCode and to
    // implement IEquatable<T>, otherwise the two comparison paths disagree.

    public bool Equals(SensorReading<T> other)
        => SensorId == other.SensorId
           && Value == other.Value
           && TimestampUtc == other.TimestampUtc;

    public override bool Equals(object? obj) => obj is SensorReading<T> other && Equals(other);

    public override int GetHashCode() => HashCode.Combine(SensorId, Value, TimestampUtc);

    public static bool operator ==(SensorReading<T> left, SensorReading<T> right) => left.Equals(right);

    public static bool operator !=(SensorReading<T> left, SensorReading<T> right) => !left.Equals(right);

    // ---- relational --------------------------------------------------------
    // C# requires these in pairs: > with <, and >= with <=.
    // Ordering compares magnitude only; the id is irrelevant to "is this hotter".

    public int CompareTo(SensorReading<T> other) => Value.CompareTo(other.Value);

    public static bool operator >(SensorReading<T> left, SensorReading<T> right) => left.Value > right.Value;

    public static bool operator <(SensorReading<T> left, SensorReading<T> right) => left.Value < right.Value;

    public static bool operator >=(SensorReading<T> left, SensorReading<T> right) => left.Value >= right.Value;

    public static bool operator <=(SensorReading<T> left, SensorReading<T> right) => left.Value <= right.Value;

    // Threshold checks against a bare value, so rule code reads `reading > limit`
    // rather than wrapping the limit in a throwaway SensorReading first.

    public static bool operator >(SensorReading<T> left, T right) => left.Value > right;

    public static bool operator <(SensorReading<T> left, T right) => left.Value < right;

    public static bool operator >=(SensorReading<T> left, T right) => left.Value >= right;

    public static bool operator <=(SensorReading<T> left, T right) => left.Value <= right;

    // ---- conversion --------------------------------------------------------
    // Explicit, not implicit: an accidental widening to T would silently discard
    // the node id and the timestamp, which is exactly the bug you do not want in
    // an alerting path.

    public static explicit operator T(SensorReading<T> reading) => reading.Value;

    public override string ToString() => $"{SensorId} = {Value} @ {TimestampUtc:HH:mm:ss}";
}

/// <summary>Aggregation helpers that lean on the operators above.</summary>
public static class SensorReadingExtensions
{
    /// <summary>
    /// Total load across a set of meters, folded with <c>operator +</c>.
    /// <c>T.Zero</c> is the identity element supplied by generic math.
    /// </summary>
    public static SensorReading<T> Sum<T>(this IEnumerable<SensorReading<T>> readings)
        where T : struct, INumber<T>
    {
        var total = new SensorReading<T>("Σ", T.Zero, DateTimeOffset.UtcNow);
        var any = false;

        foreach (var reading in readings)
        {
            total = any
                ? total + reading
                : new SensorReading<T>(reading.SensorId, reading.Value, reading.TimestampUtc);
            any = true;
        }

        return total;
    }
}
