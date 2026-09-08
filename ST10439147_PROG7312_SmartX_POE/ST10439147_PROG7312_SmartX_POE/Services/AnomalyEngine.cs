using System.Collections.Concurrent;
using SmartX.Shared.Telemetry;

namespace SmartX.Api.Services;

/// <summary>
/// The engagement strategy from Part 1, Task 1, implemented: anomaly-first visual
/// encoding with a severity-ranked alert rail and suppression of non-actionable alerts.
///
/// <para>Two detectors, deliberately separate, because a spike and a disconnect are
/// opposite failures:</para>
/// <list type="bullet">
///   <item><b>Spike</b> - a value arrived and is wrong. Caught by a z-score against a
///   rolling per-sensor baseline, so a probe that normally reads 40 and suddenly reads
///   95 fires even though no absolute threshold was crossed.</item>
///   <item><b>Silence</b> - no value arrived at all. No threshold on the data can catch
///   this, because there is no data. It needs a per-node timer keyed off the interval the
///   node was flashed with.</item>
/// </list>
///
/// <para>Suppression rather than deletion: an alert kind the operator has dismissed
/// <see cref="SuppressAfterDismissals"/> times keeps firing but arrives flagged, so the
/// rail collapses it into an auditable group instead of discarding it. Hiding a genuine
/// fault is the failure mode this design has to avoid.</para>
/// </summary>
public sealed class AnomalyEngine(TelemetryStore store, ILogger<AnomalyEngine> logger)
{
    /// <summary>Deviations beyond three sigma are the classic "this is not normal noise" line.</summary>
    public const double SpikeZScore = 3.0;

    /// <summary>Below this, the baseline is not yet trustworthy and everything looks anomalous.</summary>
    public const int MinSamplesForBaseline = 20;

    /// <summary>Dismiss a kind this many times and it stops competing for the top of the rail.</summary>
    public const int SuppressAfterDismissals = 3;

    private const int MaxAlerts = 200;
    private static readonly TimeSpan Debounce = TimeSpan.FromSeconds(20);

    private readonly ConcurrentDictionary<string, Alert> _alerts = new();
    private readonly ConcurrentDictionary<string, int> _dismissals = new();
    private readonly ConcurrentDictionary<string, DateTimeOffset> _lastRaised = new();

    /// <summary>Evaluates one node against its own history. Returns an alert only if something changed.</summary>
    public Alert? Evaluate(SensorRegistration sensor)
    {
        var window = store.Window(sensor.Id, 120);
        if (window.Count < MinSamplesForBaseline) return null;

        var observed = window[^1];

        // An actuator flipping state is normal operation, not a deviation. Running a
        // z-score over a 0/1 signal makes every single valve movement a 4-sigma "spike",
        // which is the textbook way to flood a rail until the operator stops reading it.
        // State changes are reported as their own low-severity kind instead.
        if (sensor.Payload == PayloadKind.Boolean)
        {
            var previous = window[^2];
            if (Math.Abs(observed - previous) < 0.5) return null;

            return Raise(
                sensor,
                AlertKind.StateChange,
                AlertSeverity.Info,
                "actuator state transition",
                $"Switched {(observed > 0.5 ? "on" : "off")}.",
                previous, observed, 1);
        }

        var history = window.Take(window.Count - 1).ToArray();

        var mean = history.Average();
        var variance = history.Sum(v => (v - mean) * (v - mean)) / history.Length;
        var sigma = Math.Sqrt(variance);

        // A perfectly flat signal has sigma 0, which would make every z-score infinite.
        // Treat a flat line as "no evidence of a spike" and let the threshold rule speak.
        var z = sigma > 1e-6 ? Math.Abs(observed - mean) / sigma : 0;

        if (z >= SpikeZScore)
        {
            return Raise(
                sensor,
                AlertKind.Spike,
                z >= SpikeZScore * 2 ? AlertSeverity.Critical : AlertSeverity.Warning,
                $"z-score >= {SpikeZScore:0.0} against rolling {history.Length}-sample baseline",
                $"Reading {observed:0.##}{sensor.Unit} is {z:0.0} sigma off a baseline of {mean:0.##}{sensor.Unit}.",
                mean, observed, z);
        }

        if (sensor.Payload != PayloadKind.Boolean && observed > sensor.WarningThreshold)
        {
            return Raise(
                sensor,
                AlertKind.ThresholdBreach,
                AlertSeverity.Warning,
                $"value > configured threshold of {sensor.WarningThreshold:0.##}",
                $"Reading {observed:0.##}{sensor.Unit} is over the {sensor.WarningThreshold:0.##}{sensor.Unit} threshold.",
                sensor.WarningThreshold, observed, observed - sensor.WarningThreshold);
        }

        return null;
    }

    /// <summary>
    /// Silence sweep. A node is considered lost once it has missed three publish
    /// intervals - one missed interval is jitter, three is a disconnect.
    /// </summary>
    public IReadOnlyList<Alert> SweepForSilence()
    {
        var raised = new List<Alert>();
        var now = DateTimeOffset.UtcNow;

        foreach (var sensor in store.Sensors)
        {
            var lastSeen = store.LastSeen(sensor.Id);
            if (lastSeen is null) continue;

            var quietFor = now - lastSeen.Value;
            var allowed = TimeSpan.FromSeconds(sensor.PublishIntervalSeconds * 3);
            if (quietFor <= allowed) continue;

            var alert = Raise(
                sensor,
                AlertKind.Silence,
                quietFor > allowed * 3 ? AlertSeverity.Critical : AlertSeverity.Warning,
                $"no packet for more than 3 x {sensor.PublishIntervalSeconds}s publish interval",
                $"Node has been silent for {quietFor.TotalSeconds:0}s. Expected a packet every {sensor.PublishIntervalSeconds}s.",
                sensor.PublishIntervalSeconds, quietFor.TotalSeconds, quietFor.TotalSeconds - allowed.TotalSeconds);

            if (alert is not null) raised.Add(alert);
        }

        return raised;
    }

    private Alert? Raise(
        SensorRegistration sensor,
        AlertKind kind,
        AlertSeverity severity,
        string rule,
        string message,
        double baseline,
        double observed,
        double deviation)
    {
        var key = $"{sensor.Id}|{kind}";

        // Debounce: a sustained fault is one entry on the rail, not one per packet.
        if (_lastRaised.TryGetValue(key, out var last) && DateTimeOffset.UtcNow - last < Debounce)
            return null;

        _lastRaised[key] = DateTimeOffset.UtcNow;

        var suppressed = _dismissals.GetValueOrDefault(key) >= SuppressAfterDismissals;

        var alert = new Alert(
            Id: Guid.NewGuid().ToString("N"),
            SensorId: sensor.Id,
            SensorName: sensor.DisplayName,
            DeploymentPath: sensor.DeploymentPath,
            Kind: kind,
            Severity: severity,
            Rule: rule,
            Message: message,
            Baseline: baseline,
            Observed: observed,
            Deviation: deviation,
            RaisedAt: DateTimeOffset.UtcNow,
            Suppressed: suppressed,
            Context: store.ContextAround(sensor.Id, DateTimeOffset.UtcNow));

        _alerts[alert.Id] = alert;
        Evict();

        logger.LogInformation("{Severity} {Kind} on {Sensor}: {Message}", severity, kind, sensor.DisplayName, message);
        return alert;
    }

    /// <summary>Rail order: severity first, then most recent. Suppressed alerts sink to the bottom.</summary>
    public IReadOnlyList<Alert> Ranked(bool includeSuppressed = true) =>
        _alerts.Values
            .Where(a => includeSuppressed || !a.Suppressed)
            .OrderBy(a => a.Suppressed)
            .ThenByDescending(a => a.Severity)
            .ThenByDescending(a => a.RaisedAt)
            .ToList();

    public int ActiveCount => _alerts.Values.Count(a => !a.Suppressed);

    public int SuppressedCount => _alerts.Values.Count(a => a.Suppressed);

    /// <summary>
    /// Dismissing removes this alert and counts against its kind. Past the threshold the
    /// kind is auto-suppressed for that node - the alert-fatigue feedback loop.
    /// </summary>
    public bool Dismiss(string alertId)
    {
        if (!_alerts.TryRemove(alertId, out var alert)) return false;

        var key = $"{alert.SensorId}|{alert.Kind}";
        var count = _dismissals.AddOrUpdate(key, 1, (_, existing) => existing + 1);

        if (count == SuppressAfterDismissals)
            logger.LogInformation("Suppressing {Kind} alerts for {Sensor} after {Count} dismissals", alert.Kind, alert.SensorName, count);

        return true;
    }

    /// <summary>Undo suppression for a node and kind, so a collapsed group can be re-opened.</summary>
    public void Restore(string sensorId, AlertKind kind)
    {
        _dismissals.TryRemove($"{sensorId}|{kind}", out _);

        foreach (var (id, alert) in _alerts)
        {
            if (alert.SensorId == sensorId && alert.Kind == kind)
                _alerts[id] = alert with { Suppressed = false };
        }
    }

    public void Clear() => _alerts.Clear();

    private void Evict()
    {
        if (_alerts.Count <= MaxAlerts) return;

        foreach (var stale in _alerts.Values.OrderBy(a => a.RaisedAt).Take(_alerts.Count - MaxAlerts))
            _alerts.TryRemove(stale.Id, out _);
    }
}
//---------------------------DDDDDDDDDDDoooooooooooooo END OF FILE DDDDDDDDDDDoooooooooooooo---------------------------//