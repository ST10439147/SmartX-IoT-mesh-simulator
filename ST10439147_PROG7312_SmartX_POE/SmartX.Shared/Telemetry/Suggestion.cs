namespace SmartX.Shared.Telemetry;

/// <summary>A follow-up to an event (<c>Search:pump</c>) and how many times the operator has done it after that event.</summary>
public sealed record RankedFollowUp(string FollowUp, int Count);

/// <summary>
/// What the operator has usually done next after alerts like this one, pushed the moment
/// the alert is raised so it is on screen before they start searching.
/// </summary>
public sealed record Suggestion(
    string AlertId,
    string Context,
    string SensorName,
    DateTimeOffset RaisedAt,
    IReadOnlyList<RankedFollowUp> FollowUps);
/// <summary>
/// One learned event and what the operator usually does after it. <see cref="Open"/> is true
/// while a fault of that kind is on the rail right now, with <see cref="Devices"/> naming them.
/// </summary>
public sealed record Insight(
    string Context,
    string Label,
    IReadOnlyList<string> Devices,
    bool Open,
    IReadOnlyList<RankedFollowUp> FollowUps);
//---------------------------DDDDDDDDDDDoooooooooooooo END OF FILE DDDDDDDDDDDoooooooooooooo---------------------------//
