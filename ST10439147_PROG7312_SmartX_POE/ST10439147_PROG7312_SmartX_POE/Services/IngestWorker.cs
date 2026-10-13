using Microsoft.AspNetCore.SignalR;
using SmartX.Api.Hubs;
using SmartX.Shared;
using SmartX.Shared.Telemetry;

namespace SmartX.Api.Services;

/// <summary>
/// The one consumer of <see cref="IngestQueue"/>. Dequeues a packet, lands it in the
/// sensor's typed buffer, then runs the anomaly check that used to happen inside the
/// request. One worker on purpose: packets reach the store in exactly the order the
/// queue hands them out.
/// </summary>
// Reference [23]: Microsoft. Background tasks with hosted services in ASP.NET Core. Microsoft Learn.
//   https://learn.microsoft.com/aspnet/core/fundamentals/host/hosted-services
public sealed class IngestWorker(
    IngestQueue queue,
    TelemetryStore store,
    AnomalyEngine anomalies,
    IHubContext<TelemetryHub> hub,
    ILogger<IngestWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var packet = await queue.DequeueAsync(stoppingToken);

                // Only ever non-zero during a flood test - see POST /api/queue/flood.
                if (queue.Throttle > TimeSpan.Zero) await Task.Delay(queue.Throttle, stoppingToken);

                queue.MarkProcessed(in packet);

                // The node can be deleted while its packets are still waiting in line.
                if (!store.TryGet(packet.SensorId, out var sensor)) continue;

                switch (sensor.Payload)
                {
                    case PayloadKind.Float:
                        store.Ingest(new TelemetryPacket<float>(sensor.Id, (float)packet.Value, sensor.Category, packet.Timestamp));
                        break;
                    case PayloadKind.Integer:
                        store.Ingest(new TelemetryPacket<int>(sensor.Id, (int)packet.Value, sensor.Category, packet.Timestamp));
                        break;
                    case PayloadKind.Boolean:
                        store.Ingest(new TelemetryPacket<bool>(sensor.Id, packet.Value != 0, sensor.Category, packet.Timestamp));
                        break;
                }

                store.LogReading(sensor.Id, packet.Timestamp, packet.Value, packet.Sequence);

                var alert = anomalies.Evaluate(sensor);
                if (alert is not null) await hub.Clients.All.SendAsync(HubEvents.AlertRaised, alert, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                // One bad packet must not take the whole worker down with it.
                logger.LogError(ex, "Ingest worker failed on a packet");
            }
        }
    }
}
//---------------------------DDDDDDDDDDDoooooooooooooo END OF FILE DDDDDDDDDDDoooooooooooooo---------------------------//
