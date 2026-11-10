using System.Collections.Concurrent;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.SignalR;
using SmartX.Api.Hubs;
using SmartX.Shared;
using SmartX.Shared.Telemetry;
using SmartX.Shared.Topology;

namespace SmartX.Api.Services;

/// <summary>
/// Stands in for the ESP32 mesh. Seeds a realistic node population, then publishes on
/// each node's own interval, injecting the two faults the dashboard exists to surface:
/// value spikes and node disconnects.
///
/// <para>Packets leave here the way a real node's would: as a <c>POST /api/telemetry</c>
/// to the gateway. Unplug the simulator and nothing downstream changes.</para>
/// </summary>
public sealed class MeshSimulator(
    TelemetryStore store,
    MeshNetwork mesh,
    AnomalyEngine anomalies,
    CommandService commands,
    PredictionService predictions,
    IHubContext<TelemetryHub> hub,
    IServer server,
    IHostApplicationLifetime lifetime,
    ILogger<MeshSimulator> logger) : BackgroundService
{
    private readonly HttpClient _http = new();

    private static readonly TimeSpan Tick = TimeSpan.FromSeconds(1);

    // Ticks of history synthesised at boot so the dashboard opens with full sparklines
    // and a usable anomaly baseline instead of a flat line that fills in over ~4 minutes.
    private const int WarmupTicks = 240;

    // Deterministic seed so a demo run looks the same twice - swap for Random.Shared
    // if you want genuinely different behaviour on every boot.
    private readonly Random _random = new(7312);

    private readonly Dictionary<string, NodeState> _state = new(StringComparer.OrdinalIgnoreCase);

    // Nodes an operator has unplugged, and until when. Written from a request thread and
    // read from the publish loop, hence concurrent.
    private readonly ConcurrentDictionary<string, DateTimeOffset> _unplugged = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Test trigger: the node stops publishing for <paramref name="duration"/>, as if its
    /// power had been pulled. A zero duration plugs it straight back in.
    /// </summary>
    public void Unplug(string sensorId, TimeSpan duration) => _unplugged[sensorId] = DateTimeOffset.UtcNow + duration;

    // Nodes whose reading is climbing, and by how much a second. Same threading as _unplugged.
    private readonly ConcurrentDictionary<string, double> _drifting = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Test trigger: the node's reading climbs by <paramref name="perSecond"/> units a second
    /// on top of its normal signal. Zero stops the climb and puts it back where it started.
    /// </summary>
    public void Drift(string sensorId, double perSecond)
    {
        if (perSecond == 0) _drifting.TryRemove(sensorId, out _);
        else _drifting[sensorId] = perSecond;
    }
    //----------------------------------------------------------------------------------------------------------------------//

    // Reference [22]: Microsoft. Introduction to ASP.NET Core SignalR. Microsoft Learn.
    //   https://learn.microsoft.com/aspnet/core/signalr/introduction
    // Reference [23]: Microsoft. Background tasks with hosted services in ASP.NET Core. Microsoft Learn.
    //   https://learn.microsoft.com/aspnet/core/fundamentals/host/hosted-services
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        SeedMesh();
        BackfillHistory(TimeSpan.FromHours(24));

        // Hosted services start before Kestrel is listening, so there is nothing to post
        // to yet. Wait for the server, then aim at whichever port it actually bound.
        var started = new TaskCompletionSource();
        using var registration = lifetime.ApplicationStarted.Register(started.SetResult);
        await started.Task.WaitAsync(stoppingToken);

        // Bound addresses can be wildcards ("http://[::]:8080" in the container), which
        // are not dialable - keep the scheme and port, swap the host for loopback. Plain
        // http is preferred so the loop does not depend on a trusted dev certificate.
        var bound = server.Features.Get<IServerAddressesFeature>()!.Addresses
            .Select(BindingAddress.Parse)
            .OrderBy(a => a.Scheme == Uri.UriSchemeHttps)
            .First();
        _http.BaseAddress = new Uri($"{bound.Scheme}://localhost:{bound.Port}");

        // Warmup reuses the live publish path, with each tick backdated to the second it
        // stands for, so the history reads as the last four minutes rather than one instant.
        var bootedAt = DateTimeOffset.UtcNow;
        for (var warm = 1; warm <= WarmupTicks; warm++)
            await PublishCycle(warm, stoppingToken, bootedAt.AddSeconds(warm - WarmupTicks));

        // Drop faults the accelerated warmup happened to inject so the dashboard opens
        // on a healthy mesh; real spikes and disconnects accumulate from here.
        foreach (var node in _state.Values) node.MutedUntil = 0;
        anomalies.Clear();

        logger.LogInformation("Mesh simulator seeded {Count} nodes, warmed {Ticks} ticks", store.Sensors.Count, WarmupTicks);

        // Reference [41]: Microsoft. PeriodicTimer Class. Microsoft Learn.
        //   https://learn.microsoft.com/dotnet/api/system.threading.periodictimer
        using var timer = new PeriodicTimer(Tick);
        var elapsed = WarmupTicks;

        while (await WaitForTickAsync(timer, stoppingToken))
        {
            elapsed++;

            try
            {
                await PublishCycle(elapsed, stoppingToken);

                if (elapsed % 5 == 0)
                {
                    foreach (var alert in anomalies.SweepForSilence())
                    {
                        await hub.Clients.All.SendAsync(HubEvents.AlertRaised, alert, stoppingToken);

                        if (await predictions.SuggestAsync(alert, stoppingToken) is { } suggestion)
                            await hub.Clients.All.SendAsync(HubEvents.SuggestionReady, suggestion, stoppingToken);
                    }
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
//----------------------------------------------------------------------------------------------------------------------//
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
    //----------------------------------------------------------------------------------------------------------------------//
    private async Task PublishCycle(int elapsed, CancellationToken token, DateTimeOffset? at = null)
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

            if (_unplugged.TryGetValue(sensor.Id, out var until) && until > DateTimeOffset.UtcNow) continue;

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

            // --- drift: a node an operator set climbing gains its rate for every second since it last published.
            node.Ramp = _drifting.TryGetValue(sensor.Id, out var perSecond) ? node.Ramp + perSecond * sensor.PublishIntervalSeconds : 0;

            switch (sensor.Payload)
            {
                case PayloadKind.Float:
                {
                    var value = node.NextFloat(_random, spiking);
                    await Post(sensor.Id, value, at, token);

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

                    // A pump commanded off draws nothing until the override is undone;
                    // commanded on, or left alone, it runs at its usual load.
                    if (commands.TryGetOverride(sensor.Id, out var running) && !running) value = 0;
                    await Post(sensor.Id, value, at, token);
                    break;
                }

                case PayloadKind.Boolean:
                {
                    // A manual override wins: the node holds the commanded position until
                    // it is undone. Otherwise actuators mostly hold state; a flip is the
                    // event worth seeing.
                    if (commands.TryGetOverride(sensor.Id, out var forced)) node.Flag = forced;
                    else if (_random.NextDouble() < 0.08) node.Flag = !node.Flag;
                    await Post(sensor.Id, node.Flag, at, token);
                    break;
                }
            }
        }
    }
    //----------------------------------------------------------------------------------------------------------------------//
    // The gateway evaluates anomalies as it ingests, so that no longer happens here.
    // A null timestamp leaves the gateway to stamp the packet on arrival.
    private async Task Post<T>(string sensorId, T value, DateTimeOffset? timestamp, CancellationToken token)
    {
        using var response = await _http.PostAsJsonAsync("api/telemetry", new { sensorId, value, timestamp }, token);

        // A node deleted mid-cycle answers 404; that is not worth losing the rest of the tick over.
        if (!response.IsSuccessStatusCode)
            logger.LogWarning("Gateway rejected packet from {Sensor}: {Status}", sensorId, (int)response.StatusCode);
    }
    //----------------------------------------------------------------------------------------------------------------------//
    private void SeedMesh()
    {
        // Field beds
        Add("A4:CF:12:9B:04:7E", "Soil Moisture North", "Facility A/Zone 1/Sub-Zone A", SensorCategory.SoilMoisture, 2, 31, 2.5);
        Add("A4:CF:12:9B:04:7F", "Soil Moisture South", "Facility A/Zone 1/Sub-Zone A", SensorCategory.SoilMoisture, 2, 28, 2.5);
        Add("DC:A6:32:77:11:01", "Drip Line Valve A", "Facility A/Zone 1/Sub-Zone A", SensorCategory.IrrigationValve, 4);
        Add("DC:A6:32:77:11:03", "Drip Line Valve B", "Facility A/Zone 1/Sub-Zone A", SensorCategory.IrrigationValve, 4);

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

        LinkMesh();
    }
    //----------------------------------------------------------------------------------------------------------------------//
    /// <summary>
    /// The radio links of the seeded farm, as each node would report them: who it can hear
    /// and at what RSSI (dBm, closer to zero is stronger). The gateway sits in the pump house.
    ///
    /// <para>The layout is a real mesh, not a star: only the pump house is in direct range of
    /// the gateway. The greenhouse reaches it through the main line flow meter or the roof
    /// array, and the field beds only through the greenhouse - so every far node has at
    /// least two ways home, which is what lets the mesh heal when one node drops.</para>
    /// </summary>
    private void LinkMesh()
    {
        const string gateway = MeshGraph.GatewayId;

        // Pump house: in direct range of the gateway.
        mesh.Link(gateway, "B8:27:EB:41:02:AA", -45);              // Borehole Pump
        mesh.Link(gateway, "B8:27:EB:41:02:AB", -48);              // Booster Pump
        mesh.Link(gateway, "A4:CF:12:9B:06:01", -52);              // Main Line Flow
        mesh.Link("B8:27:EB:41:02:AA", "B8:27:EB:41:02:AB", -42);
        mesh.Link("B8:27:EB:41:02:AB", "A4:CF:12:9B:06:01", -50);

        // Roof array: a weak direct hop, or a better one via the flow meter.
        mesh.Link(gateway, "B8:27:EB:41:02:AD", -63);              // Rainwater Tank Level
        mesh.Link("A4:CF:12:9B:06:01", "B8:27:EB:41:02:AD", -58);

        // Greenhouse <-> pump house / roof: the long hops that carry Zone 1 home.
        mesh.Link("A4:CF:12:9B:06:01", "A4:CF:12:9B:05:13", -67);  // flow meter - Grow Light
        mesh.Link("B8:27:EB:41:02:AD", "A4:CF:12:9B:05:13", -71);  // tank - Grow Light
        mesh.Link("B8:27:EB:41:02:AD", "DC:A6:32:77:11:02", -69);  // tank - Roof Vent

        // Inside the greenhouse.
        mesh.Link("A4:CF:12:9B:05:13", "A4:CF:12:9B:05:10", -50);  // Grow Light - Humidity
        mesh.Link("A4:CF:12:9B:05:13", "A4:CF:12:9B:05:11", -58);  // Grow Light - Air Temp
        mesh.Link("A4:CF:12:9B:05:10", "A4:CF:12:9B:05:11", -44);  // Humidity - Air Temp
        mesh.Link("A4:CF:12:9B:05:11", "A4:CF:12:9B:05:12", -47);  // Air Temp - pH
        mesh.Link("A4:CF:12:9B:05:12", "DC:A6:32:77:11:02", -53);  // pH - Roof Vent
        mesh.Link("A4:CF:12:9B:05:10", "DC:A6:32:77:11:02", -55);  // Humidity - Roof Vent

        // Greenhouse <-> field beds: two separate bridges.
        mesh.Link("A4:CF:12:9B:05:12", "A4:CF:12:9B:04:7E", -66);  // pH - Soil Moisture North
        mesh.Link("DC:A6:32:77:11:02", "DC:A6:32:77:11:01", -70);  // Roof Vent - Drip Valve A

        // Field beds.
        mesh.Link("A4:CF:12:9B:04:7E", "A4:CF:12:9B:04:7F", -55);  // Moisture North - South
        mesh.Link("A4:CF:12:9B:04:7E", "DC:A6:32:77:11:01", -49);  // Moisture North - Valve A
        mesh.Link("A4:CF:12:9B:04:7F", "DC:A6:32:77:11:03", -51);  // Moisture South - Valve B
        mesh.Link("A4:CF:12:9B:04:7F", "DC:A6:32:77:11:01", -60);  // Moisture South - Valve A
        mesh.Link("DC:A6:32:77:11:01", "DC:A6:32:77:11:03", -54);  // Valve A - Valve B
    }

    //----------------------------------------------------------------------------------------------------------------------//
    /// <summary>
    /// Seeds <paramref name="span"/> of history into every seeded node's AVL tree, ending
    /// where the warmup begins, so a range query like "14:00 to 16:00" has data from the
    /// first request (~416k readings across the seeded mesh, roughly 50 MB in memory).
    /// </summary>
    // Written straight into the store rather than posted: ~400k HTTP calls
    // would hold boot up for minutes, and the live path already proves the API route.
    // Sequence 0 marks a seeded reading; live readings always carry a sequence of 1 or more.
    private void BackfillHistory(TimeSpan span)
    {
        var end = DateTimeOffset.UtcNow.AddSeconds(-WarmupTicks);
        var seeded = 0;

        foreach (var sensor in store.Sensors)
        {
            var node = _state[sensor.Id];
            var step = TimeSpan.FromSeconds(sensor.PublishIntervalSeconds);

            for (var at = end - span; at < end; at += step)
            {
                var spiking = _random.NextDouble() < 0.012;
                double value;

                if (sensor.Payload == PayloadKind.Boolean)
                {
                    if (_random.NextDouble() < 0.08) node.Flag = !node.Flag;
                    value = node.Flag ? 1 : 0;
                }
                else if (sensor.Payload == PayloadKind.Integer) value = Math.Round(node.NextFloat(_random, spiking));
                else value = node.NextFloat(_random, spiking);

                store.LogHistory(sensor.Id, new TimelinePoint(at, value, 0));
                seeded++;
            }
        }

        logger.LogInformation("Backfilled {Count:N0} readings ({Hours}h) into the AVL history trees", seeded, span.TotalHours);
    }
    //----------------------------------------------------------------------------------------------------------------------//

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
    //----------------------------------------------------------------------------------------------------------------------//

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
    //----------------------------------------------------------------------------------------------------------------------//
    /// <summary>Per-node simulation state: where the signal sits and how far it wanders.</summary>
    private sealed class NodeState
    {
        public double Baseline;
        public double Noise;
        public bool Flag;
        public int MutedUntil;
        public double Ramp;   // offset built up by the drift trigger; zero when not drifting
        private double _drift;
        //----------------------------------------------------------------------------------------------------------------------//
        public float NextFloat(Random random, bool spiking)
        {
            // Slow random walk so the baseline moves like a real environment rather
            // than sitting pinned to a constant.
            _drift = Math.Clamp(_drift + (random.NextDouble() - 0.5) * Noise * 0.15, -Noise, Noise);

            var value = Baseline + Ramp + _drift + (random.NextDouble() - 0.5) * Noise;
            if (spiking) value += Noise * random.Next(5, 10) * (random.NextDouble() < 0.5 ? -1 : 1);

            return (float)value;
        }

        public float Jitter(Random random, double around) => (float)(around + (random.NextDouble() - 0.5) * Noise * 0.4);
    }
}
//---------------------------DDDDDDDDDDDoooooooooooooo END OF FILE DDDDDDDDDDDoooooooooooooo---------------------------//
