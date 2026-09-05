using System;
using System.Collections.Generic;
using System.Text;

namespace SmartX.Shared.Classes.Telemetry
{
    public enum AlertSeverity
    {
        Info = 0,
        Warning = 1,
        Critical = 2
    }

    /// <summary>
    /// A spike and a disconnect are opposite problems and cannot share a detector: a spike
    /// is a value that arrived and is wrong, a silence is a value that never arrived at all.
    /// They are modelled separately for that reason.
    /// </summary>
    public enum AlertKind
    {
        Spike,
        ThresholdBreach,
        Silence,
        StateChange
    }

    /// <summary>One packet either side of the offending reading, for the drill-down inspector.</summary>
    public sealed record ContextSample(DateTimeOffset At, double Value, bool IsOffending);

    /// <summary>
    /// A finding on the alert rail. Ranked by <see cref="Severity"/> then recency; alerts of
    /// a kind the operator has repeatedly dismissed arrive with <see cref="Suppressed"/> set
    /// so the UI can collapse them into an auditable group rather than discard them.
    /// </summary>
    public sealed record Alert(
        string Id,
        string SensorId,
        string SensorName,
        string DeploymentPath,
        AlertKind Kind,
        AlertSeverity Severity,
        string Rule,
        string Message,
        double Baseline,
        double Observed,
        double Deviation,
        DateTimeOffset RaisedAt,
        bool Suppressed,
        IReadOnlyList<ContextSample> Context);

    /// <summary>Header counters for the dashboard.</summary>
    public sealed record GatewayStatus(
        string Version,
        DateTimeOffset StartedAt,
        double UptimeSeconds,
        int SensorsRegistered,
        int SensorsSilent,
        long PacketsIngested,
        double PacketsPerSecond,
        int ActiveAlerts,
        int SuppressedAlerts);
}
