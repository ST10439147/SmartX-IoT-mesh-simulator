using SmartX.Shared.Telemetry;

namespace SmartX.Api.Services;

/// <summary>Projects gateway state into the flat shapes the dashboard renders.</summary>
public static class SnapshotFactory
{
    /// <summary>Samples kept in each tile's sparkline. Small multiples, shared scale.</summary>
    public const int SparkSamples = 40;

    public static SensorSnapshot Snapshot(this TelemetryStore store, SensorRegistration sensor)
    {
        var window = store.Window(sensor.Id, SparkSamples);
        var lastSeen = store.LastSeen(sensor.Id);
        var latest = store.LatestNumeric(sensor.Id);

        var silent = lastSeen is not null
                     && DateTimeOffset.UtcNow - lastSeen.Value > TimeSpan.FromSeconds(sensor.PublishIntervalSeconds * 3);

        return new SensorSnapshot(
            Id: sensor.Id,
            DisplayName: sensor.DisplayName,
            MacAddress: sensor.MacAddress,
            DeploymentPath: sensor.DeploymentPath,
            Category: sensor.Category,
            Payload: sensor.Payload,
            Unit: sensor.Unit,
            LatestValue: double.IsNaN(latest) ? 0 : latest,
            // Only meaningful for actuators; leaving it computed for every payload put a
            // confusing "latestBool": true next to every float reading on the wire.
            LatestBool: sensor.Payload == PayloadKind.Boolean && latest > 0.5,
            LastSeen: lastSeen,
            Baseline: window.Count > 0 ? window.Average() : 0,
            PacketsReceived: store.PacketCount(sensor.Id),
            IsSilent: silent,
            Spark: window);
    }

    public static IReadOnlyList<SensorSnapshot> Snapshots(this TelemetryStore store)
        => store.Sensors.OrderBy(s => s.DeploymentPath).Select(store.Snapshot).ToList();

    public static GatewayStatus Status(this TelemetryStore store, AnomalyEngine anomalies)
    {
        var uptime = (DateTimeOffset.UtcNow - store.StartedAt).TotalSeconds;
        var snapshots = store.Snapshots();

        return new GatewayStatus(
            Version: "Part 1 / Task 2",
            StartedAt: store.StartedAt,
            UptimeSeconds: uptime,
            SensorsRegistered: snapshots.Count,
            SensorsSilent: snapshots.Count(s => s.IsSilent),
            PacketsIngested: store.PacketsIngested,
            PacketsPerSecond: uptime > 1 ? store.PacketsIngested / uptime : 0,
            ActiveAlerts: anomalies.ActiveCount,
            SuppressedAlerts: anomalies.SuppressedCount);
    }
}
//---------------------------DDDDDDDDDDDoooooooooooooo END OF FILE DDDDDDDDDDDoooooooooooooo---------------------------//
