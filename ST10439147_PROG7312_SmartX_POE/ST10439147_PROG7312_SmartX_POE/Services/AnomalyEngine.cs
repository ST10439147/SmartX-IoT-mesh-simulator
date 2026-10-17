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
///
/// <para>One fault, one alert. A node that stays silent is found silent again on every
/// sweep, and a reading that stays over its threshold is over it again on every packet.
/// Two <see cref="HashSet{T}"/>s remember what is already known - the disconnected nodes
/// and the open error states - and <c>Add</c> returning false is the signal that this
/// alert has been raised before and is skipped.</para>
/// </summary>
// Reference [35]: The Independent Institute of Education (2026). PROG7312 Portfolio of Evidence Guide, Part 1. The IIE.
public sealed class AnomalyEngine(TelemetryStore store, CommandService commands, ILogger<AnomalyEngine> logger)
{
    /// <summary>Deviations beyond three sigma are the classic "this is not normal noise" line.</summary>
    // Reference [19]: Wikipedia. Standard score (z-score).
    //   https://en.wikipedia.org/wiki/Standard_score
    // Reference [20]: Wikipedia. 68-95-99.7 rule (three-sigma rule of thumb).
    //   https://en.wikipedia.org/wiki/68%E2%80%9395%E2%80%9399.7_rule
    public const double SpikeZScore = 3.0;

    /// <summary>Below this, the baseline is not yet trustworthy and everything looks anomalous.</summary>
    public const int MinSamplesForBaseline = 20;

    /// <summary>Dismiss a kind this many times and it stops competing for the top of the rail.</summary>
    public const int SuppressAfterDismissals = 3;

    private const int MaxAlerts = 200;
    private static readonly TimeSpan Debounce = TimeSpan.FromSeconds(20);

    // Reference [18]: Microsoft. ConcurrentDictionary<TKey,TValue> Class. Microsoft Learn.
    //   https://learn.microsoft.com/dotnet/api/system.collections.concurrent.concurrentdictionary-2
    private readonly ConcurrentDictionary<string, Alert> _alerts = new();
    private readonly ConcurrentDictionary<string, int> _dismissals = new();
    private readonly ConcurrentDictionary<string, DateTimeOffset> _lastRaised = new();

    // Each set answers one yes/no question - "is this node already known to be down?",
    // "is this fault already open?" - and a hash set answers it in O(1) with no value to
    // store. _disconnected holds sensor ids; _activeErrors holds "sensorId|Kind".
    // HashSet is not thread-safe and the worker, the sweep and requests all touch these,
    // so every access goes through the lock.
    // Reference: Microsoft. HashSet<T> Class. Microsoft Learn.
    //   https://learn.microsoft.com/dotnet/api/system.collections.generic.hashset-1
    private readonly HashSet<string> _disconnected = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _activeErrors = new(StringComparer.OrdinalIgnoreCase);
    // Reference [39]: Microsoft. Lock Class (System.Threading). Microsoft Learn.
    //   https://learn.microsoft.com/dotnet/api/system.threading.lock
    private readonly Lock _gate = new();
    private long _duplicatesSkipped;

    /// <summary>Nodes currently past their silence limit.</summary>
    public IReadOnlyList<string> Disconnected
    {
        get { lock (_gate) return _disconnected.Order().ToList(); }
    }

    /// <summary>Spikes and threshold breaches that are still going on, as <c>sensorId|Kind</c>.</summary>
    public IReadOnlyList<string> ActiveErrors
    {
        get { lock (_gate) return _activeErrors.Order().ToList(); }
    }

    /// <summary>Alerts not raised because the fault they describe was already in a set.</summary>
    public long DuplicatesSkipped
    {
        get { lock (_gate) return _duplicatesSkipped; }
    }
    //----------------------------------------------------------------------------------------------------------------------//
    /// <summary>
    /// True the first time a fault is seen, false for as long as it stays open.
    /// <see cref="HashSet{T}.Add"/> does the test and the insert in one step.
    /// </summary>
    private bool Open(HashSet<string> set, string key)
    {
        lock (_gate)
        {
            if (set.Add(key)) return true;

            _duplicatesSkipped++;
            return false;
        }
    }

    /// <summary>The fault has passed, so the next occurrence is a new one.</summary>
    private void Close(HashSet<string> set, string key)
    {
        lock (_gate) set.Remove(key);
    }
    //----------------------------------------------------------------------------------------------------------------------//
    /// <summary>Evaluates one node against its own history. Returns an alert only if something changed.</summary>
    // Reference [19]: Wikipedia. Standard score (z-score).
    //   https://en.wikipedia.org/wiki/Standard_score
    // Reference [21]: Wikipedia. Alarm fatigue.
    //   https://en.wikipedia.org/wiki/Alarm_fatigue
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

        if (StoppedByOperator(sensor)) return null;

        var history = RunningBaseline(sensor, window.Take(window.Count - 1));
        if (history.Length < MinSamplesForBaseline) return null;

        var (mean, z) = ZScore(history, observed);

        var spikeKey = $"{sensor.Id}|{AlertKind.Spike}";
        var breachKey = $"{sensor.Id}|{AlertKind.ThresholdBreach}";

        if (z < SpikeZScore) Close(_activeErrors, spikeKey);
        if (observed <= sensor.WarningThreshold) Close(_activeErrors, breachKey);

        if (z >= SpikeZScore)
        {
            if (!Open(_activeErrors, spikeKey)) return null;

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
            if (!Open(_activeErrors, breachKey)) return null;

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
    //----------------------------------------------------------------------------------------------------------------------//
    /// <summary>
    /// Triage at the door, before the packet is ingested: would this reading be a spike
    /// or a threshold breach? The ingest queue uses the answer to let it jump the line.
    /// Same two rules as <see cref="Evaluate"/>, run against the history already stored.
    /// </summary>
    public AlertKind? Classify(SensorRegistration sensor, double value)
    {
        // An actuator flipping is normal operation (see Evaluate), never critical.
        if (sensor.Payload == PayloadKind.Boolean || StoppedByOperator(sensor)) return null;

        var history = RunningBaseline(sensor, store.Window(sensor.Id, 120));
        if (history.Length >= MinSamplesForBaseline && ZScore(history, value).Z >= SpikeZScore)
            return AlertKind.Spike;

        return value > sensor.WarningThreshold ? AlertKind.ThresholdBreach : null;
    }
    //----------------------------------------------------------------------------------------------------------------------//
    /// <summary>A pump the operator switched off reads 0 W because it was told to - that is not a fault.</summary>
    private bool StoppedByOperator(SensorRegistration sensor)
        => sensor.Category == SensorCategory.PumpPower && commands.TryGetOverride(sensor.Id, out var running) && !running;

    /// <summary>
    /// The readings a spike is measured against. A pump's 0 W readings are time it spent
    /// stopped, not its running load; left in, they drag the mean down and restarting the
    /// pump looks like a spike. Only the history is filtered - a pump that drops to 0 W
    /// without being told to still stands out against it.
    /// </summary>
    private static double[] RunningBaseline(SensorRegistration sensor, IEnumerable<double> history)
        => sensor.Category == SensorCategory.PumpPower ? history.Where(v => v > 0).ToArray() : history.ToArray();
    //----------------------------------------------------------------------------------------------------------------------//
    private static (double Mean, double Z) ZScore(IReadOnlyCollection<double> history, double observed)
    {
        var mean = history.Average();
        var variance = history.Sum(v => (v - mean) * (v - mean)) / history.Count;
        var sigma = Math.Sqrt(variance);

        // A perfectly flat signal has sigma 0, which would make every z-score infinite.
        // Treat a flat line as "no evidence of a spike" and let the threshold rule speak.
        return (mean, sigma > 1e-6 ? Math.Abs(observed - mean) / sigma : 0);
    }
    //----------------------------------------------------------------------------------------------------------------------//
    /// <summary>
    /// Silence sweep. A node is considered lost once it has missed three publish
    /// intervals - one missed interval is jitter, three is a disconnect.
    ///
    /// <para>The sweep runs every few seconds and a dead node fails it every time. The
    /// disconnect set is what turns that into a single alert: the node goes in when it
    /// is first found silent and comes out when it is heard from again. The one thing a
    /// repeat sweep may still do is escalate that same alert from warning to critical.</para>
    /// </summary>
    /// <param name="at">The time to sweep as of. Defaults to now; the self-check passes its own.</param>
    public IReadOnlyList<Alert> SweepForSilence(DateTimeOffset? at = null)
    {
        var raised = new List<Alert>();
        var now = at ?? DateTimeOffset.UtcNow;
        var sensors = store.Sensors;

        // A deleted node can never reconnect or recover, so nothing else would take it out.
        var registered = sensors.Select(s => s.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        lock (_gate)
        {
            _disconnected.IntersectWith(registered);
            _activeErrors.RemoveWhere(key => !registered.Contains(key[..key.IndexOf('|')]));
        }

        foreach (var sensor in sensors)
        {
            var lastSeen = store.LastSeen(sensor.Id);
            if (lastSeen is null) continue;

            var quietFor = now - lastSeen.Value;
            var allowed = TimeSpan.FromSeconds(sensor.PublishIntervalSeconds * 3);

            if (quietFor <= allowed)
            {
                Close(_disconnected, sensor.Id);
                continue;
            }

            var severity = quietFor > allowed * 3 ? AlertSeverity.Critical : AlertSeverity.Warning;
            var message = $"Node has been silent for {quietFor.TotalSeconds:0}s. Expected a packet every {sensor.PublishIntervalSeconds}s.";

            // Already in the set: this outage has its alert. Do not raise another.
            if (!Open(_disconnected, sensor.Id))
            {
                var open = _alerts.Values.FirstOrDefault(a =>
                    a.SensorId == sensor.Id && a.Kind == AlertKind.Silence && a.Severity < severity);

                if (open is not null)
                {
                    var escalated = open with { Severity = severity, Message = message, Observed = quietFor.TotalSeconds };
                    _alerts[open.Id] = escalated;
                    raised.Add(escalated);
                }

                continue;
            }

            // The set already guarantees one alert per outage, so no debounce: a node
            // that drops, returns and drops again inside the window is two outages.
            var alert = Raise(
                sensor,
                AlertKind.Silence,
                severity,
                $"no packet for more than 3 x {sensor.PublishIntervalSeconds}s publish interval",
                message,
                sensor.PublishIntervalSeconds, quietFor.TotalSeconds, quietFor.TotalSeconds - allowed.TotalSeconds,
                debounce: false);

            if (alert is not null) raised.Add(alert);
        }

        return raised;
    }
    //----------------------------------------------------------------------------------------------------------------------//
    private Alert? Raise(
        SensorRegistration sensor,
        AlertKind kind,
        AlertSeverity severity,
        string rule,
        string message,
        double baseline,
        double observed,
        double deviation,
        bool debounce = true)
    {
        var key = $"{sensor.Id}|{kind}";

        // Debounce: a reading that flaps across a limit is one entry on the rail, not one per crossing.
        if (debounce && _lastRaised.TryGetValue(key, out var last) && DateTimeOffset.UtcNow - last < Debounce)
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
    //----------------------------------------------------------------------------------------------------------------------//
    /// <summary>Rail order: severity first, then most recent. Suppressed alerts sink to the bottom.</summary>
    public IReadOnlyList<Alert> Ranked(bool includeSuppressed = true) =>
        _alerts.Values
            .Where(a => includeSuppressed || !a.Suppressed)
            .OrderBy(a => a.Suppressed)
            .ThenByDescending(a => a.Severity)
            .ThenByDescending(a => a.RaisedAt)
            .ToList();
    //----------------------------------------------------------------------------------------------------------------------//
    public int ActiveCount => _alerts.Values.Count(a => !a.Suppressed);

    public int SuppressedCount => _alerts.Values.Count(a => a.Suppressed);
    //----------------------------------------------------------------------------------------------------------------------//
    /// <summary>
    /// Dismissing removes this alert and counts against its kind. Past the threshold the
    /// kind is auto-suppressed for that node - the alert-fatigue feedback loop.
    /// </summary>
    // Reference [21]: Wikipedia. Alarm fatigue.
    //   https://en.wikipedia.org/wiki/Alarm_fatigue
    public bool Dismiss(string alertId)
    {
        if (!_alerts.TryRemove(alertId, out var alert)) return false;

        var key = $"{alert.SensorId}|{alert.Kind}";
        var count = _dismissals.AddOrUpdate(key, 1, (_, existing) => existing + 1);

        if (count == SuppressAfterDismissals)
            logger.LogInformation("Suppressing {Kind} alerts for {Sensor} after {Count} dismissals", alert.Kind, alert.SensorName, count);

        return true;
    }
    //----------------------------------------------------------------------------------------------------------------------//
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
    //----------------------------------------------------------------------------------------------------------------------//
    public void Clear()
    {
        _alerts.Clear();

        lock (_gate)
        {
            _disconnected.Clear();
            _activeErrors.Clear();
        }
    }

    private void Evict()
    {
        if (_alerts.Count <= MaxAlerts) return;

        foreach (var stale in _alerts.Values.OrderBy(a => a.RaisedAt).Take(_alerts.Count - MaxAlerts))
            _alerts.TryRemove(stale.Id, out _);
    }
}
//---------------------------DDDDDDDDDDDoooooooooooooo END OF FILE DDDDDDDDDDDoooooooooooooo---------------------------//