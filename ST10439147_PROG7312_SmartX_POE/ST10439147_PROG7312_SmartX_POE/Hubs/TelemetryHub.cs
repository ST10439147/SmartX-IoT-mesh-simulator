using Microsoft.AspNetCore.SignalR;

namespace SmartX.Api.Hubs;

/// <summary>
/// Push channel for the dashboard. Polling a REST endpoint on a timer would show
/// anomalies late and average away short spikes entirely, which is the exact failure the
/// anomaly-first strategy exists to avoid - so the gateway pushes and the client listens.
///
/// <para>Message names live in <see cref="SmartX.Shared.HubEvents"/> so both ends of the
/// wire read them from the same constant.</para>
/// </summary>
// Reference [22]: Microsoft. Introduction to ASP.NET Core SignalR. Microsoft Learn.
//   https://learn.microsoft.com/aspnet/core/signalr/introduction
public sealed class TelemetryHub : Hub
{
    /// <summary>Client-callable ping, used by the dashboard to display live round-trip latency.</summary>
    public DateTimeOffset Ping() => DateTimeOffset.UtcNow;
}
//---------------------------DDDDDDDDDDDoooooooooooooo END OF FILE DDDDDDDDDDDoooooooooooooo---------------------------//
