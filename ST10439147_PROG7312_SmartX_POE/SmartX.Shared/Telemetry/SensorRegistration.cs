using System.ComponentModel.DataAnnotations;

namespace SmartX.Shared.Telemetry;

/// <summary>What the node is measuring. Drives grouping and default thresholds.</summary>
public enum SensorCategory
{
    Environmental,
    PowerConsumption,
    Actuator
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

    [Required]
    public SensorCategory Category { get; set; } = SensorCategory.Environmental;

    [Required]
    public PayloadKind Payload { get; set; } = PayloadKind.Float;

    /// <summary>Publish interval the node was flashed with. Drives the silence timer.</summary>
    [Range(1, 3600, ErrorMessage = "Publish interval must be between 1 and 3600 seconds.")]
    public int PublishIntervalSeconds { get; set; } = 2;

    [Range(-100, 100000, ErrorMessage = "Warning threshold is out of the supported range.")]
    public double WarningThreshold { get; set; } = 80;

    /// <summary>
    /// Unit label shown next to the value. Left blank the gateway fills in a sensible
    /// default for the category - but a category is not a unit, and a humidity probe
    /// reporting in °C is exactly the kind of wrong that survives a demo and embarrasses
    /// you in the field. So it stays overridable.
    /// </summary>
    [StringLength(8, ErrorMessage = "Unit labels are short, e.g. °C, %, W, kPa.")]
    public string Unit { get; set; } = string.Empty;

    public DateTimeOffset RegisteredAt { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>Fallback used when no unit was supplied at registration.</summary>
    public string DefaultUnit() => Payload switch
    {
        PayloadKind.Boolean => string.Empty,
        _ => Category switch
        {
            SensorCategory.Environmental => "°C",
            SensorCategory.PowerConsumption => "W",
            _ => string.Empty
        }
    };
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
