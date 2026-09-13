using System.ComponentModel.DataAnnotations;

namespace SmartX.Shared.Telemetry;

/// <summary>
/// The specific farm device a node is. There is deliberately no catch-all bucket: the
/// category alone fixes the payload type, unit and default threshold (see
/// <see cref="SensorCategoryExtensions.Spec"/>), so a generic "float sensor" cannot exist.
/// </summary>
public enum SensorCategory
{
    SoilMoisture,
    SoilPh,
    AirTemperature,
    RelativeHumidity,
    LightIntensity,
    WaterFlow,
    TankLevel,
    PumpPower,
    IrrigationValve,
    GreenhouseVent
}

public static class SensorCategoryExtensions
{
    /// <summary>
    /// The one definition of each category. Thresholds are upper limits, because that is
    /// the only direction the anomaly engine checks.
    /// </summary>
    public static (string Label, PayloadKind Payload, string Unit, double Threshold) Spec(this SensorCategory category) => category switch
    {
        SensorCategory.SoilMoisture     => ("Soil moisture",     PayloadKind.Float,   "%VWC",  45),     // waterlogged root zone
        SensorCategory.SoilPh           => ("Soil pH",           PayloadKind.Float,   "pH",    7.5),    // alkaline nutrient lock-out
        SensorCategory.AirTemperature   => ("Air temperature",   PayloadKind.Float,   "°C",    32),     // crop heat stress
        SensorCategory.RelativeHumidity => ("Relative humidity", PayloadKind.Float,   "%RH",   85),     // fungal disease risk
        SensorCategory.LightIntensity   => ("Light intensity",   PayloadKind.Integer, "lux",   55000),  // leaf scorch
        SensorCategory.WaterFlow        => ("Irrigation flow",   PayloadKind.Float,   "L/min", 30),     // burst line
        SensorCategory.TankLevel        => ("Water tank level",  PayloadKind.Integer, "%",     95),     // about to overflow
        SensorCategory.PumpPower        => ("Pump power draw",   PayloadKind.Integer, "W",     3000),   // stalled or dry-running pump
        SensorCategory.IrrigationValve  => ("Irrigation valve",  PayloadKind.Boolean, "",      0),      // state, not a level: no threshold
        SensorCategory.GreenhouseVent   => ("Greenhouse vent",   PayloadKind.Boolean, "",      0),
        _ => throw new ArgumentOutOfRangeException(nameof(category), category, "Not an agricultural sensor category.")
    };
}

/// <summary>
/// Which CLR value type the node publishes. This is what lets the gateway pick the
/// correctly typed <see cref="TelemetryPacket{T}"/> bucket instead of dropping every
/// reading into a shared <c>List&lt;object&gt;</c> and boxing it.
/// </summary>
public enum PayloadKind
{
    Float,
    Integer,
    Boolean
}

/// <summary>A sensor profile as submitted by the dashboard and stored by the gateway.</summary>
public sealed class SensorRegistration
{
    /// <summary>Assigned by the gateway on registration; the MAC is the natural key.</summary>
    public string Id { get; set; } = string.Empty;

    [Required(ErrorMessage = "A device MAC address is required.")]
    [RegularExpression(
        "^([0-9A-Fa-f]{2}[:-]){5}[0-9A-Fa-f]{2}$",
        ErrorMessage = "MAC must be six hex pairs separated by ':' or '-', e.g. A4:CF:12:9B:04:7E.")]
    public string MacAddress { get; set; } = string.Empty;

    [Required(ErrorMessage = "A friendly name helps operators identify the node.")]
    [StringLength(48, MinimumLength = 3)]
    public string DisplayName { get; set; } = string.Empty;

    /// <summary>
    /// Slash-delimited deployment path, e.g. <c>Facility A/Zone 1/Sub-Zone B/node-3</c>.
    /// Validated recursively against the deployment tree before the node is accepted.
    /// </summary>
    [Required(ErrorMessage = "A deployment path is required.")]
    [RegularExpression(
        @"^[^/]+(/[^/]+){1,6}$",
        ErrorMessage = "Path must be at least Facility/Zone, segments separated by '/'.")]
    public string DeploymentPath { get; set; } = string.Empty;

    // Enum-typed JSON still accepts a bare number, so (SensorCategory)99 is rejected here
    // at the trust boundary rather than blowing up in Spec() on the ingest path.
    [Required]
    [EnumDataType(typeof(SensorCategory), ErrorMessage = "Pick one of the agricultural sensor categories.")]
    public SensorCategory Category { get; set; } = SensorCategory.SoilMoisture;

    /// <summary>Derived from <see cref="Category"/>; a soil moisture probe cannot be registered as a bool.</summary>
    public PayloadKind Payload => Category.Spec().Payload;

    /// <summary>Publish interval the node was flashed with. Drives the silence timer.</summary>
    [Range(1, 3600, ErrorMessage = "Publish interval must be between 1 and 3600 seconds.")]
    public int PublishIntervalSeconds { get; set; } = 2;

    [Range(-100, 100000, ErrorMessage = "Warning threshold is out of the supported range.")]
    public double WarningThreshold { get; set; } = SensorCategory.SoilMoisture.Spec().Threshold;

    /// <summary>
    /// Derived from <see cref="Category"/> too. A humidity probe reporting in °C is exactly
    /// the kind of wrong that survives a demo and embarrasses you in the field, so the unit
    /// is 
    /// </summary>
    public string Unit => Category.Spec().Unit;

    public DateTimeOffset RegisteredAt { get; set; } = DateTimeOffset.UtcNow;
}

/// <summary>Everything the dashboard needs to draw one tile in the sensor grid.</summary>
public sealed record SensorSnapshot(
    string Id,
    string DisplayName,
    string MacAddress,
    string DeploymentPath,
    SensorCategory Category,
    PayloadKind Payload,
    string Unit,
    double LatestValue,
    bool LatestBool,
    DateTimeOffset? LastSeen,
    double Baseline,
    long PacketsReceived,
    bool IsSilent,
    IReadOnlyList<double> Spark);
//---------------------------DDDDDDDDDDDoooooooooooooo END OF FILE DDDDDDDDDDDoooooooooooooo---------------------------//
