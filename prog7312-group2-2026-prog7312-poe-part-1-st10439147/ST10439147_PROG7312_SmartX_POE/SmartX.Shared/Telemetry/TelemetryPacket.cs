using System.Text.Json.Serialization;

namespace SmartX.Shared.Telemetry;

/// <summary>
/// The universal wrapper for a single reading off the mesh.
///
/// <para><typeparamref name="T"/> is constrained to <c>struct</c> only - not to
/// <c>INumber&lt;T&gt;</c> - because the mesh publishes floats (soil moisture), ints
/// (power wattage) <em>and</em> bools (valve state), and <c>bool</c> is not a number.
/// A single wrapper therefore carries all three without coercing anything into a
/// common numeric type and without boxing into <see cref="object"/>.</para>
///
/// <para>Declared <c>readonly struct</c> so the packet lives inline inside the ring
/// buffer's backing array and the compiler skips defensive copies when it is passed
/// by <c>in</c> reference.</para>
///
/// <para>Arithmetic lives on <see cref="SensorReading{T}"/>, which adds the
/// <c>INumber&lt;T&gt;</c> constraint. Splitting the two is deliberate: an operator
/// cannot declare constraints beyond those of its containing type, so a single type
/// cannot both accept <c>bool</c> and define <c>operator +</c>.</para>
/// </summary>
public readonly struct TelemetryPacket<T> : IEquatable<TelemetryPacket<T>> where T : struct
{
    public string SensorId { get; }
    public T Value { get; }
    public SensorCategory Category { get; }
    public DateTimeOffset TimestampUtc { get; }

    // Two public constructors, so System.Text.Json needs to be told which one rebuilds
    // the struct off the wire.
    [JsonConstructor]
    public TelemetryPacket(string sensorId, T value, SensorCategory category, DateTimeOffset timestampUtc)
    {
        SensorId = sensorId;
        Value = value;
        Category = category;
        TimestampUtc = timestampUtc;
    }

    public TelemetryPacket(string sensorId, T value, SensorCategory category)
        : this(sensorId, value, category, DateTimeOffset.UtcNow)
    {
    }

    /// <summary>Same node and instant, different reading. Used when a value is corrected.</summary>
    public TelemetryPacket<T> WithValue(T value) => new(SensorId, value, Category, TimestampUtc);

    // EqualityComparer<T>.Default dispatches to T's IEquatable<T> implementation
    // without boxing, which a plain Value.Equals(other.Value) call would not guarantee.
    public bool Equals(TelemetryPacket<T> other)
        => SensorId == other.SensorId
           && TimestampUtc == other.TimestampUtc
           && EqualityComparer<T>.Default.Equals(Value, other.Value);

    public override bool Equals(object? obj) => obj is TelemetryPacket<T> other && Equals(other);

    public override int GetHashCode() => HashCode.Combine(SensorId, Value, TimestampUtc);

    public static bool operator ==(TelemetryPacket<T> left, TelemetryPacket<T> right) => left.Equals(right);

    public static bool operator !=(TelemetryPacket<T> left, TelemetryPacket<T> right) => !left.Equals(right);

    public override string ToString() => $"{SensorId} = {Value} @ {TimestampUtc:HH:mm:ss.fff}";
}
