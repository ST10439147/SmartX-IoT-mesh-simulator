using SmartX.Shared.Telemetry;

namespace SmartX.Api.Services;

public enum OperatorActionKind
{
    Search,
    Command
}

/// <summary>
/// One thing the operator did, and the alert that was on the rail when they did it.
/// <see cref="Context"/> is the "after event X" half of a prediction pair, written as
/// <c>Category|AlertKind</c> (e.g. <c>SoilMoisture|ThresholdBreach</c>) so a breach on any
/// moisture probe counts as the same event. Null when nothing was alerting.
/// </summary>
public sealed record OperatorAction(
    DateTimeOffset At,
    OperatorActionKind Kind,
    string Target,
    string? Context,
    string? ContextSensor)
{
    /// <summary>The "the operator did Y" half of the pair: <c>Search:pump</c>, <c>Command:Switch Borehole Pump on</c>.</summary>
    public string FollowUp => $"{Kind}:{Target}";
}

/// <summary>
/// The predictive action engine. Every search and command lands here with the alert
/// context of the moment it was made.
/// </summary>
// ponytail: the log keeps the last MaxLog actions only - enough to inspect what was learned.
// Persist it if predictions have to survive a restart.
public sealed class PredictionService(TelemetryStore store, AnomalyEngine anomalies)
{
    public const int MaxLog = 500;

    /// <summary>
    /// How recent an alert must be to count as the reason for an action. An alert from an
    /// hour ago still on the rail is not why the operator searched just now.
    /// </summary>
    public static readonly TimeSpan ContextWindow = TimeSpan.FromMinutes(2);

    private readonly Queue<OperatorAction> _log = new();
    private readonly Lock _gate = new();

    /// <summary>Logged actions, newest first.</summary>
    public IReadOnlyList<OperatorAction> Log
    {
        get { lock (_gate) return _log.Reverse().ToList(); }
    }
    //----------------------------------------------------------------------------------------------------------------------//
    /// <summary>Logs one action against whatever alert is driving the operator right now.</summary>
    public OperatorAction Record(OperatorActionKind kind, string target)
    {
        var (context, sensor) = CurrentContext();
        // "Pump" and "pump " are the same search; command labels are generated, so already stable.
        target = kind == OperatorActionKind.Search ? target.Trim().ToLowerInvariant() : target;
        var action = new OperatorAction(DateTimeOffset.UtcNow, kind, target, context, sensor);

        lock (_gate)
        {
            _log.Enqueue(action);
            if (_log.Count > MaxLog) _log.Dequeue();
        }

        return action;
    }
    //----------------------------------------------------------------------------------------------------------------------//
    /// <summary>
    /// The newest actionable alert inside <see cref="ContextWindow"/>. Suppressed alerts are
    /// ones the operator has said they ignore, and a valve switching is normal operation,
    /// so neither is treated as the cause of what the operator does next.
    /// </summary>
    private (string? Context, string? Sensor) CurrentContext()
    {
        var since = DateTimeOffset.UtcNow - ContextWindow;

        var alert = anomalies.Ranked(includeSuppressed: false)
            .Where(a => a.Kind != AlertKind.StateChange && a.RaisedAt >= since)
            .MaxBy(a => a.RaisedAt);

        if (alert is null || !store.TryGet(alert.SensorId, out var sensor)) return (null, null);

        return ($"{sensor.Category}|{alert.Kind}", sensor.DisplayName);
    }
}
//---------------------------DDDDDDDDDDDoooooooooooooo END OF FILE DDDDDDDDDDDoooooooooooooo---------------------------//
