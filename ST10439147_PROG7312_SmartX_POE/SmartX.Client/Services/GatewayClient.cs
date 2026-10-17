using System.Diagnostics;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Components.Forms;
using SmartX.Shared;
using SmartX.Shared.Telemetry;
using SmartX.Shared.Topology;

namespace SmartX.Client.Services;

/// <summary>
/// Typed client for the gateway API. Every call is async with a cancellation token, and
/// the base address comes from configuration (<c>wwwroot/appsettings.json</c>) rather
/// than a literal compiled into a component.
/// </summary>
public sealed class GatewayClient(HttpClient http)
{
    public Uri BaseAddress => http.BaseAddress!;
//----------------------------------------------------------------------------------------------------------------------//
    /// <summary>Health ping that also reports how long the round trip took.</summary>
    // Reference [33]: Nielsen, J. (1994, updated 2024). 10 Usability Heuristics for User Interface Design - Visibility of system status. Nielsen Norman Group.
    //   https://www.nngroup.com/articles/ten-usability-heuristics/
    public async Task<(GatewayStatus? Status, long Milliseconds, string? Error)> PingAsync(CancellationToken ct = default)
    {
        var stopwatch = Stopwatch.StartNew();

        try
        {
            var status = await http.GetFromJsonAsync<GatewayStatus>("api/health", SmartXJson.Options, ct);
            return (status, stopwatch.ElapsedMilliseconds, null);
        }
        // A health ping has one job: say whether the gateway is reachable and answering
        // with a valid status. Any failure - connection refused, a cancelled token, or a
        // non-JSON body (running the client standalone makes /api/health fall through to
        // index.html) - is just "not healthy", never an unhandled error that trips the
        // Blazor error UI.
        catch (Exception ex)
        {
            return (null, stopwatch.ElapsedMilliseconds, ex.Message);
        }
    }
//----------------------------------------------------------------------------------------------------------------------//
    public async Task<IReadOnlyList<SensorSnapshot>> GetSensorsAsync(CancellationToken ct = default)
        => await http.GetFromJsonAsync<List<SensorSnapshot>>("api/sensors", SmartXJson.Options, ct) ?? [];

    public async Task<SensorRegistration?> GetProfileAsync(string id, CancellationToken ct = default)
        => await http.GetFromJsonAsync<SensorRegistration>($"api/sensors/{id}/profile", SmartXJson.Options, ct);

    public async Task<DeploymentNode?> GetTopologyAsync(CancellationToken ct = default)
        => await http.GetFromJsonAsync<DeploymentNode>("api/topology", SmartXJson.Options, ct);
//----------------------------------------------------------------------------------------------------------------------//
    /// <summary>Registers a node. Field-level errors come straight back from the API's own validation.</summary>
    // Reference [17]: Microsoft. Handle errors in ASP.NET Core - problem details / ValidationProblem. Microsoft Learn.
    //   https://learn.microsoft.com/aspnet/core/fundamentals/minimal-apis/handle-errors
    public async Task<RegistrationOutcome> RegisterAsync(SensorRegistration registration, CancellationToken ct = default)
    {
        var response = await http.PostAsJsonAsync("api/sensors", registration, SmartXJson.Options, ct);

        if (response.IsSuccessStatusCode)
        {
            var snapshot = await response.Content.ReadFromJsonAsync<SensorSnapshot>(SmartXJson.Options, ct);
            return new RegistrationOutcome(true, snapshot, new Dictionary<string, string[]>());
        }

        var problem = await response.Content.ReadFromJsonAsync<ValidationProblemResponse>(SmartXJson.Options, ct);
        return new RegistrationOutcome(false, null, problem?.Errors ?? new Dictionary<string, string[]>
        {
            ["_"] = [$"The gateway rejected the registration ({(int)response.StatusCode})."]
        });
    }
//----------------------------------------------------------------------------------------------------------------------//
    public async Task<bool> DeleteSensorAsync(string id, CancellationToken ct = default)
        => (await http.DeleteAsync($"api/sensors/{id}", ct)).IsSuccessStatusCode;

    public async Task<IReadOnlyList<Alert>> GetAlertsAsync(CancellationToken ct = default)
        => await http.GetFromJsonAsync<List<Alert>>("api/alerts", SmartXJson.Options, ct) ?? [];

    public async Task DismissAlertAsync(string alertId, CancellationToken ct = default)
        => await http.PostAsync($"api/alerts/{alertId}/dismiss", null, ct);

    public async Task RestoreAsync(string sensorId, AlertKind kind, CancellationToken ct = default)
        => await http.PostAsJsonAsync("api/alerts/restore", new { sensorId, kind }, SmartXJson.Options, ct);

    public async Task<IReadOnlyList<AttachmentInfo>> GetAttachmentsAsync(string sensorId, CancellationToken ct = default)
        => await http.GetFromJsonAsync<List<AttachmentInfo>>($"api/sensors/{sensorId}/attachments", SmartXJson.Options, ct) ?? [];
//----------------------------------------------------------------------------------------------------------------------//
    /// <summary>
    /// Streams the browser file straight into a multipart request. The bytes are never
    /// materialised in the client's memory as one array, and the gateway never buffers
    /// them either - the whole path is a stream from disk to disk.
    /// </summary>
    // Reference [24]: Microsoft. Upload files in ASP.NET Core - uploading large files with streaming. Microsoft Learn.
    //   https://learn.microsoft.com/aspnet/core/mvc/models/file-uploads
    public async Task<(bool Ok, AttachmentInfo? Attachment, string? Error)> UploadAsync(
        string sensorId, IBrowserFile file, CancellationToken ct = default)
    {
        try
        {
            using var content = new MultipartFormDataContent();
            await using var stream = file.OpenReadStream(MaxUploadBytes, ct);

            content.Add(new StreamContent(stream), "file", file.Name);

            var response = await http.PostAsync($"api/sensors/{sensorId}/attachments", content, ct);

            if (response.IsSuccessStatusCode)
                return (true, await response.Content.ReadFromJsonAsync<AttachmentInfo>(SmartXJson.Options, ct), null);

            var problem = await response.Content.ReadFromJsonAsync<MessageResponse>(SmartXJson.Options, ct);
            return (false, null, problem?.Message ?? $"Upload failed ({(int)response.StatusCode}).");
        }
        catch (IOException ex)
        {
            return (false, null, ex.Message);
        }
        catch (HttpRequestException ex)
        {
            return (false, null, ex.Message);
        }
    }
//----------------------------------------------------------------------------------------------------------------------//
    public string DownloadUrl(AttachmentInfo info) => new Uri(BaseAddress, $"api/attachments/{info.Id}").ToString();
//----------------------------------------------------------------------------------------------------------------------//
    public async Task<AggregateResult?> AggregateAsync(string a, string b, CancellationToken ct = default)
    {
        var response = await http.GetAsync($"api/telemetry/aggregate?a={Uri.EscapeDataString(a)}&b={Uri.EscapeDataString(b)}", ct);
        return response.IsSuccessStatusCode
            ? await response.Content.ReadFromJsonAsync<AggregateResult>(SmartXJson.Options, ct)
            : null;
    }
//----------------------------------------------------------------------------------------------------------------------//
    public async Task<TopologyReport?> ValidateTopologyAsync(DeploymentNode root, CancellationToken ct = default)
    {
        var response = await http.PostAsJsonAsync("api/topology/validate", root, SmartXJson.Options, ct);
        return await response.Content.ReadFromJsonAsync<TopologyReport>(SmartXJson.Options, ct);
    }
//----------------------------------------------------------------------------------------------------------------------//
    public async Task<StressResult?> StressTopologyAsync(int depth, CancellationToken ct = default)
        => await http.GetFromJsonAsync<StressResult>($"api/topology/stress?depth={depth}", SmartXJson.Options, ct);

    // ---- device registry ---------------------------------------------------

    /// <summary>Registry size and lookup counters. Null if the gateway could not be reached.</summary>
    public async Task<RegistryStatus?> GetRegistryAsync(CancellationToken ct = default)
    {
        try
        {
            return await http.GetFromJsonAsync<RegistryStatus>("api/registry", SmartXJson.Options, ct);
        }
        catch (HttpRequestException)
        {
            return null;
        }
    }

    /// <summary>Looks one device up by MAC or id, in whatever spelling it was typed. Null when there is no such device.</summary>
    public async Task<SensorSnapshot?> FindSensorAsync(string macOrId, CancellationToken ct = default)
    {
        var response = await http.GetAsync($"api/sensors/{Uri.EscapeDataString(macOrId)}", ct);
        return response.IsSuccessStatusCode
            ? await response.Content.ReadFromJsonAsync<SensorSnapshot>(SmartXJson.Options, ct)
            : null;
    }

    // ---- search ------------------------------------------------------------

    /// <summary>Devices whose name, id, path or category contains the term. Empty if the gateway could not be reached.</summary>
    public async Task<IReadOnlyList<SensorSnapshot>> SearchAsync(string term, CancellationToken ct = default)
    {
        try
        {
            return await http.GetFromJsonAsync<List<SensorSnapshot>>(
                $"api/search?q={Uri.EscapeDataString(term)}", SmartXJson.Options, ct) ?? [];
        }
        catch (HttpRequestException)
        {
            return [];
        }
    }

    // ---- predictive action engine ------------------------------------------

    /// <summary>The suggestion for the alert driving the operator right now. Null when there is none or the gateway is down.</summary>
    public async Task<Suggestion?> GetSuggestionAsync(CancellationToken ct = default)
    {
        try
        {
            var response = await http.GetAsync("api/predictions/suggestion", ct);
            return response.StatusCode == System.Net.HttpStatusCode.OK
                ? await response.Content.ReadFromJsonAsync<Suggestion>(SmartXJson.Options, ct)
                : null;
        }
        catch (HttpRequestException)
        {
            return null;
        }
    }

    /// <summary>Devices climbing toward their threshold, soonest first. Empty if the gateway could not be reached.</summary>
    public async Task<IReadOnlyList<TrendFlag>> GetTrendsAsync(CancellationToken ct = default)
    {
        try
        {
            return await http.GetFromJsonAsync<List<TrendFlag>>("api/predictions/trends", SmartXJson.Options, ct) ?? [];
        }
        catch (HttpRequestException)
        {
            return [];
        }
    }

    public async Task<IReadOnlyList<Insight>> GetInsightsAsync(CancellationToken ct = default)
    {
        try
        {
            return await http.GetFromJsonAsync<List<Insight>>("api/predictions/insights", SmartXJson.Options, ct) ?? [];
        }
        catch (HttpRequestException)
        {
            return [];
        }
    }

    // ---- timeline ----------------------------------------------------------

    /// <summary>One sensor's readings in timestamp order, oldest first. Null if the gateway could not be reached or the sensor is gone.</summary>
    public async Task<List<TimelinePoint>?> GetTimelineAsync(string sensorId, int samples = 60, CancellationToken ct = default)
    {
        try
        {
            return await http.GetFromJsonAsync<List<TimelinePoint>>(
                $"api/sensors/{Uri.EscapeDataString(sensorId)}/timeline?samples={samples}", SmartXJson.Options, ct);
        }
        catch (HttpRequestException)
        {
            return null;
        }
    }

    /// <summary>Test trigger: has the gateway receive a few backdated readings for this sensor in shuffled order.</summary>
    public async Task ScrambleTimelineAsync(string sensorId, CancellationToken ct = default)
        => await http.PostAsync($"api/sensors/{Uri.EscapeDataString(sensorId)}/timeline/scramble", null, ct);

    // ---- disconnect set ----------------------------------------------------

    /// <summary>The disconnected nodes and open error states. Null if the gateway could not be reached.</summary>
    public async Task<FaultState?> GetFaultsAsync(CancellationToken ct = default)
    {
        try
        {
            return await http.GetFromJsonAsync<FaultState>("api/faults", SmartXJson.Options, ct);
        }
        catch (HttpRequestException)
        {
            return null;
        }
    }

    /// <summary>Test trigger: the simulated node stops publishing for this long. Zero plugs it back in.</summary>
    public async Task UnplugAsync(string sensorId, int seconds, CancellationToken ct = default)
        => await http.PostAsync($"api/sensors/{Uri.EscapeDataString(sensorId)}/unplug?seconds={seconds}", null, ct);

    // ---- manual override commands ------------------------------------------

    /// <summary>Undo history (newest first) and current overrides. Null if the gateway could not be reached.</summary>
    public async Task<CommandLog?> GetCommandsAsync(CancellationToken ct = default)
    {
        try
        {
            return await http.GetFromJsonAsync<CommandLog>("api/commands", SmartXJson.Options, ct);
        }
        catch (HttpRequestException)
        {
            return null;
        }
    }

    /// <summary>Pins one actuator on or off.</summary>
    public async Task<bool> CommandDeviceAsync(string sensorId, bool state, CancellationToken ct = default)
        => (await http.PostAsJsonAsync("api/commands", new { sensorId, state }, SmartXJson.Options, ct)).IsSuccessStatusCode;

    /// <summary>Pins every actuator of one kind on or off as a single, undoable command.</summary>
    public async Task<bool> CommandCategoryAsync(SensorCategory category, bool state, CancellationToken ct = default)
        => (await http.PostAsJsonAsync("api/commands", new { category, state }, SmartXJson.Options, ct)).IsSuccessStatusCode;

    public async Task<bool> UndoCommandAsync(CancellationToken ct = default)
        => (await http.PostAsync("api/commands/undo", null, ct)).IsSuccessStatusCode;

    // ---- ingest queue ------------------------------------------------------

    /// <summary>Queue depth and processing trail. Null if the gateway could not be reached - the panel just keeps its last view.</summary>
    public async Task<QueueStatus?> GetQueueAsync(CancellationToken ct = default)
    {
        try
        {
            return await http.GetFromJsonAsync<QueueStatus>("api/queue", SmartXJson.Options, ct);
        }
        catch (HttpRequestException)
        {
            return null;
        }
    }

    public async Task FloodQueueAsync(CancellationToken ct = default)
        => await http.PostAsync("api/queue/flood", null, ct);

    /// <summary>Posts one reading exactly as a node would. Used by the queue panel to send a deliberately out-of-range value.</summary>
    public async Task<bool> PostTelemetryAsync(string sensorId, double value, CancellationToken ct = default)
        => (await http.PostAsJsonAsync("api/telemetry", new { sensorId, value }, SmartXJson.Options, ct)).IsSuccessStatusCode;
//----------------------------------------------------------------------------------------------------------------------//
    /// <summary>Mirrors <c>AttachmentService.MaxBytes</c> so the browser rejects an oversized file first.</summary>
    public const long MaxUploadBytes = 25 * 1024 * 1024;
}
//----------------------------------------------------------------------------------------------------------------------//
public sealed record RegistrationOutcome(bool Ok, SensorSnapshot? Snapshot, IDictionary<string, string[]> Errors);

public sealed record ValidationProblemResponse(Dictionary<string, string[]> Errors);

public sealed record MessageResponse(string Message);

public sealed record AggregateSide(string DisplayName, double Value, string Unit);

public sealed record AggregateValue(string Id, double Value);
//----------------------------------------------------------------------------------------------------------------------//
public sealed record AggregateResult(
    AggregateSide Left,
    AggregateSide Right,
    AggregateValue Aggregate,
    AggregateValue Delta,
    bool LeftDrawsMore,
    bool Equal);
//----------------------------------------------------------------------------------------------------------------------//
/// <summary>Shape of <c>GET /api/registry</c>.</summary>
public sealed record RegistryStatus(int Devices, long Lookups, long Misses, List<string> Keys);
//----------------------------------------------------------------------------------------------------------------------//
/// <summary>Shape of <c>GET /api/faults</c>. Active errors are <c>sensorId|Kind</c>.</summary>
public sealed record FaultState(List<string> Disconnected, List<string> ActiveErrors, long DuplicatesSkipped);
//----------------------------------------------------------------------------------------------------------------------//
/// <summary>Shape of <c>GET /api/commands</c>. History is the undo stack, top first.</summary>
public sealed record CommandLog(List<CommandEntry> History, Dictionary<string, bool> Overrides);

public sealed record CommandEntry(long Id, string Label, DateTimeOffset IssuedAt, List<CommandChange> Changes);

public sealed record CommandChange(string SensorId, bool? Previous, bool Target);
//----------------------------------------------------------------------------------------------------------------------//
/// <summary>Shape of <c>GET /api/queue</c>. Position is processing order, Sequence is arrival order.</summary>
public sealed record QueueStatus(int Depth, int CriticalDepth, long Processed, double ThrottleMs, List<QueueEntry> Recent);

public sealed record QueueEntry(
    long Position,
    long Sequence,
    string Lane,
    AlertKind? Critical,
    string SensorId,
    double Value,
    DateTimeOffset ReceivedAt,
    double WaitedMs);
//----------------------------------------------------------------------------------------------------------------------//
public sealed record StressResult(
    int RequestedDepth,
    int Ceiling,
    int MaxDepthReached,
    int NodesVisited,
    bool IsValid,
    bool Survived,
    List<TopologyIssue> Issues);
//---------------------------DDDDDDDDDDDoooooooooooooo END OF FILE DDDDDDDDDDDoooooooooooooo---------------------------//
