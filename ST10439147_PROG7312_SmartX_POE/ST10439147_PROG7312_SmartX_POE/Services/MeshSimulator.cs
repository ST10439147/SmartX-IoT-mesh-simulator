using Microsoft.AspNetCore.SignalR;
using SmartX.Api.Hubs;
using SmartX.Shared;
using SmartX.Shared.Telemetry;

namespace SmartX.Api.Services;

/// <summary>
/// Stands in for the ESP32 mesh. Seeds a realistic node population, then publishes on
/// each node's own interval, injecting the two faults the dashboard exists to surface:
/// value spikes and node disconnects.
///
/// <para>Replace this with a real MQTT subscriber and nothing downstream changes - the
/// simulator writes through the same <see cref="TelemetryStore.Ingest(in TelemetryPacket{float})"/>
/// path the HTTP ingestion endpoint uses.</para>
/// </summary>
public sealed class MeshSimulator(
    TelemetryStore store,
    AnomalyEngine anomalies,
    IHubContext<TelemetryHub> hub,
    ILogger<MeshSimulator> logger) : BackgroundService
{
    private static readonly TimeSpan Tick = TimeSpan.FromSeconds(1);

    // Ticks of history synthesised at boot so the dashboard opens with full sparklines
    // and a usable anomaly baseline instead of a flat line that fills in over ~4 minutes.
    private const int WarmupTicks = 240;

    // Deterministic seed so a demo run looks the same twice - swap for Random.Shared
    // if you want genuinely different behaviour on every boot.
    private readonly Random _random = new(7312);

    private readonly Dictionary<string, NodeState> _state = new(StringComparer.OrdinalIgnoreCase);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        SeedMesh();

        // ponytail: warmup reuses the live publish path, so backfilled packets are all
        // stamped at boot time. Sparklines plot by index so that is invisible there;
        // /history shows a compressed leading segment. Thread backdated timestamps
        // through PublishCycle if that ever matters.
        for (var warm = 1; warm <= WarmupTicks; warm++) PublishCycle(warm);

        // Drop faults the accelerated warmup happened to inject so the dashboard opens
        // on a healthy mesh; real spikes and disconnects accumulate from here.
        foreach (var node in _state.Values) node.MutedUntil = 0;
        anomalies.Clear();

        logger.LogInformation("Mesh simulator seeded {Count} nodes, warmed {Ticks} ticks", store.Sensors.Count, WarmupTicks);

        using var timer = new PeriodicTimer(Tick);
        var elapsed = WarmupTicks;

        while (await WaitForTickAsync(timer, stoppingToken))
        {
            elapsed++;

            try
            {
                PublishCycle(elapsed);

                if (elapsed % 5 == 0)
                {
                    foreach (var alert in anomalies.SweepForSilence())
                        await hub.Clients.All.SendAsync(HubEvents.AlertRaised, alert, stoppingToken);
                }

                await hub.Clients.All.SendAsync(
                    HubEvents.Tick,
                    store.Snapshots(),
                    store.Status(anomalies),
                    stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                // One bad tick must not take the whole ingest loop down with it.
                logger.LogError(ex, "Simulator tick {Tick} failed", elapsed);
            }
        }
    }

    // WaitForNextTickAsync throws OperationCanceledException on shutdown; the loop
    // just needs to end, so swallow it here rather than let it escape ExecuteAsync.
    private static async ValueTask<bool> WaitForTickAsync(PeriodicTimer timer, CancellationToken token)
    {
        try
        {
            return await timer.WaitForNextTickAsync(token);
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }

    private void PublishCycle(int elapsed)
    {
        foreach (var sensor in store.Sensors)
        {
            // A node registered through the dashboard has no seeded profile, so give it
            // one and let it start publishing. Without this, registering a sensor would
            // appear to work and then sit at zero packets forever.
            if (!_state.TryGetValue(sensor.Id, out var node))
            {
                node = Adopt(sensor);
                _state[sensor.Id] = node;
                logger.LogInformation("Adopted newly registered node {Sensor} into the simulated mesh", sensor.DisplayName);
            }

            if (elapsed % sensor.PublishIntervalSeconds != 0) continue;

            // --- disconnect injection: mute a node for a stretch so the silence timer
            // has something real to catch.
            if (node.MutedUntil > elapsed) continue;

            if (_random.NextDouble() < 0.004)
            {
                node.MutedUntil = elapsed + _random.Next(12, 30);
                logger.LogDebug("Node {Sensor} going quiet until tick {Tick}", sensor.DisplayName, node.MutedUntil);
                continue;
            }

            // --- spike injection
            var spiking = _random.NextDouble() < 0.012;

            switch (sensor.Payload)
            {
                case PayloadKind.Float:
                {
                    var value = node.NextFloat(_random, spiking);
                    store.Ingest(new TelemetryPacket<float>(sensor.Id, value, sensor.Category));

                    // A poll cycle returns however many samples the mesh delivered - the
                    // ragged rows the jagged archive exists for.
                    var burst = new float[_random.Next(1, 7)];
                    for (var i = 0; i < burst.Length; i++) burst[i] = node.Jitter(_random, value);
                    store.StageCycle(sensor.Id, burst, DateTimeOffset.UtcNow);
                    break;
                }

                case PayloadKind.Integer:
                {
                    var value = (int)Math.Round(node.NextFloat(_random, spiking));
                    store.Ingest(new TelemetryPacket<int>(sensor.Id, value, sensor.Category));
                    break;
                }

                case PayloadKind.Boolean:
                {
                    // Actuators mostly hold state; a flip is the event worth seeing.
                    if (_random.NextDouble() < 0.08) node.Flag = !node.Flag;
                    store.Ingest(new TelemetryPacket<bool>(sensor.Id, node.Flag, sensor.Category));
                    break;
                }
            }

            var alert = anomalies.Evaluate(sensor);
            if (alert is not null)
                _ = hub.Clients.All.SendAsync(HubEvents.AlertRaised, alert, CancellationToken.None);
        }
    }

    private void SeedMesh()
    {
        // Field beds
        Add("A4:CF:12:9B:04:7E", "Soil Moisture North", "Facility A/Zone 1/Sub-Zone A", SensorCategory.SoilMoisture, 2, 31, 2.5);
        Add("A4:CF:12:9B:04:7F", "Soil Moisture South", "Facility A/Zone 1/Sub-Zone A", SensorCategory.SoilMoisture, 2, 28, 2.5);
        Add("DC:A6:32:77:11:01", "Drip Line Valve A", "Facility A/Zone 1/Sub-Zone A", SensorCategory.IrrigationValve, 4);

        // Greenhouse
        Add("A4:CF:12:9B:05:10", "Greenhouse Humidity", "Facility A/Zone 1/Sub-Zone B", SensorCategory.RelativeHumidity, 3, 62, 4.0);
        Add("A4:CF:12:9B:05:11", "Greenhouse Air Temp", "Facility A/Zone 1/Sub-Zone B", SensorCategory.AirTemperature, 2, 24, 1.2);
        Add("A4:CF:12:9B:05:12", "Root Zone pH", "Facility A/Zone 1/Sub-Zone B", SensorCategory.SoilPh, 5, 6.3, 0.08);
        Add("A4:CF:12:9B:05:13", "Grow Light Level", "Facility A/Zone 1/Sub-Zone B", SensorCategory.LightIntensity, 3, 32000, 2500);
        Add("DC:A6:32:77:11:02", "Greenhouse Roof Vent", "Facility A/Zone 1/Sub-Zone B", SensorCategory.GreenhouseVent, 4);

        // Pump house and irrigation supply
        Add("B8:27:EB:41:02:AA", "Borehole Pump", "Facility A/Zone 2/Sub-Zone C", SensorCategory.PumpPower, 2, 2200, 160);
        Add("B8:27:EB:41:02:AB", "Booster Pump", "Facility A/Zone 2/Sub-Zone C", SensorCategory.PumpPower, 2, 940, 90);
        Add("A4:CF:12:9B:06:01", "Main Line Flow", "Facility A/Zone 2/Sub-Zone C", SensorCategory.WaterFlow, 2, 18, 1.5);
        Add("B8:27:EB:41:02:AD", "Rainwater Tank Level", "Facility A/Zone 2/Roof Array", SensorCategory.TankLevel, 5, 74, 2);
    }

    /// <summary>
    /// Derives a plausible signal for a node the operator registered by hand. The
    /// warning threshold is the only hint the registration gives about magnitude, so the
    /// baseline sits comfortably under it and the noise band is a fraction of it -
    /// otherwise every freshly registered node would immediately trip its own threshold.
    /// </summary>
    private static NodeState Adopt(SensorRegistration sensor) => new()
    {
        Baseline = sensor.Payload == PayloadKind.Boolean ? 0 : sensor.WarningThreshold * 0.6,
        Noise = sensor.Payload == PayloadKind.Boolean ? 0 : Math.Max(0.5, sensor.WarningThreshold * 0.04)
    };

    private void Add(
        string mac, string name, string path,
        SensorCategory category, int interval, double baseline = 0, double noise = 0)
    {
        var registered = store.Register(new SensorRegistration
        {
            MacAddress = mac,
            DisplayName = name,
            DeploymentPath = path,
            Category = category,
            PublishIntervalSeconds = interval,
            WarningThreshold = category.Spec().Threshold
        });

        _state[registered.Id] = new NodeState { Baseline = baseline, Noise = noise };
    }

    /// <summary>Per-node simulation state: where the signal sits and how far it wanders.</summary>
    private sealed class NodeState
    {
        public double Baseline;
        public double Noise;
        public bool Flag;
        public int MutedUntil;
        private double _drift;

        public float NextFloat(Random random, bool spiking)
        {
            // Slow random walk so the baseline moves like a real environment rather
            // than sitting pinned to a constant.
            _drift = Math.Clamp(_drift + (random.NextDouble() - 0.5) * Noise * 0.15, -Noise, Noise);

            var value = Baseline + _drift + (random.NextDouble() - 0.5) * Noise;
            if (spiking) value += Noise * random.Next(5, 10) * (random.NextDouble() < 0.5 ? -1 : 1);

            return (float)value;
        }

        public float Jitter(Random random, double around) => (float)(around + (random.NextDouble() - 0.5) * Noise * 0.4);
    }
}
//---------------------------DDDDDDDDDDDoooooooooooooo END OF FILE DDDDDDDDDDDoooooooooooooo---------------------------//
