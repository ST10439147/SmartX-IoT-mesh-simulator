namespace SmartX.Shared.Telemetry;

/// <summary>
/// The answer to "every reading from this sensor between <see cref="From"/> and
/// <see cref="To"/>", read out of the sensor's AVL tree. <see cref="NodesVisited"/> against
/// <see cref="TreeSize"/> shows how much of the history the query had to touch.
/// </summary>
public sealed record HistoryRange(
    string SensorId,
    DateTimeOffset From,
    DateTimeOffset To,
    IReadOnlyList<TimelinePoint> Points,
    int TreeSize,
    int TreeHeight,
    int NodesVisited);
//---------------------------DDDDDDDDDDDoooooooooooooo END OF FILE DDDDDDDDDDDoooooooooooooo---------------------------//
