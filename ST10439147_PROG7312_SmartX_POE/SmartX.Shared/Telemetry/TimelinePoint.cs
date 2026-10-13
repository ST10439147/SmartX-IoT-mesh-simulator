namespace SmartX.Shared.Telemetry;

/// <summary>
/// One reading on a sensor's timeline. <see cref="At"/> is when the node took it, which is
/// the order the timeline is kept in; <see cref="Sequence"/> is the order it reached the
/// gateway in. When the two disagree, the packet arrived late and was slotted back into
/// its place in time.
/// </summary>
public sealed record TimelinePoint(DateTimeOffset At, double Value, long Sequence);
//---------------------------DDDDDDDDDDDoooooooooooooo END OF FILE DDDDDDDDDDDoooooooooooooo---------------------------//
