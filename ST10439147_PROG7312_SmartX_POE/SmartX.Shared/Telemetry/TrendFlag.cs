namespace SmartX.Shared.Telemetry;

/// <summary>
/// A device still under its threshold but climbing toward it fast enough to cross soon.
/// <see cref="Current"/> is the fitted value at the newest sample, not the raw reading, so
/// one noisy packet does not move the projection.
/// </summary>
public sealed record TrendFlag(
    string SensorId,
    string SensorName,
    string Unit,
    double Current,
    double Threshold,
    double SlopePerSecond,
    double SecondsToThreshold);
//---------------------------DDDDDDDDDDDoooooooooooooo END OF FILE DDDDDDDDDDDoooooooooooooo---------------------------//
