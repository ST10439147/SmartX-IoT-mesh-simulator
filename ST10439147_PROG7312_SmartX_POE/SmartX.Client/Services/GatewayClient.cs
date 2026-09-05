using System.Diagnostics;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Components.Forms;
using SmartX.Shared;
using SmartX.Shared.Classes.Telemetry;
using SmartX.Shared.Classes.Topology;
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

    /// <summary>Health ping that also reports how long the round trip took.</summary>
    public async Task<(GatewayStatus? Status, long Milliseconds, string? Error)> PingAsync(CancellationToken ct = default)
    {
        var stopwatch = Stopwatch.StartNew();

        try
        {
            var status = await http.GetFromJsonAsync<GatewayStatus>("api/health", SmartXJson.Options, ct);
            return (status, stopwatch.ElapsedMilliseconds, null);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            return (null, stopwatch.ElapsedMilliseconds, ex.Message);
        }
    }

    public async Task<IReadOnlyList<SensorSnapshot>> GetSensorsAsync(CancellationToken ct = default)
        => await http.GetFromJsonAsync<List<SensorSnapshot>>("api/sensors", SmartXJson.Options, ct) ?? [];

    public async Task<SensorReg?> GetProfileAsync(string id, CancellationToken ct = default)
        => await http.GetFromJsonAsync<SensorReg>($"api/sensors/{id}/profile", SmartXJson.Options, ct);

    public async Task<DeploymentNode?> GetTopologyAsync(CancellationToken ct = default)
        => await http.GetFromJsonAsync<DeploymentNode>("api/topology", SmartXJson.Options, ct);

    /// <summary>Registers a node. Field-level errors come straight back from the API's own validation.</summary>
    public async Task<RegistrationOutcome> RegisterAsync(SensorReg registration, CancellationToken ct = default)
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

    /// <summary>
    /// Streams the browser file straight into a multipart request. The bytes are never
    /// materialised in the client's memory as one array, and the gateway never buffers
    /// them either - the whole path is a stream from disk to disk.
    /// </summary>
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

    public string DownloadUrl(AttachmentInfo info) => new Uri(BaseAddress, $"api/attachments/{info.Id}").ToString();

    public async Task<AggregateResult?> AggregateAsync(string a, string b, CancellationToken ct = default)
    {
        var response = await http.GetAsync($"api/telemetry/aggregate?a={Uri.EscapeDataString(a)}&b={Uri.EscapeDataString(b)}", ct);
        return response.IsSuccessStatusCode
            ? await response.Content.ReadFromJsonAsync<AggregateResult>(SmartXJson.Options, ct)
            : null;
    }

    public async Task<TopologyReport?> ValidateTopologyAsync(DeploymentNode root, CancellationToken ct = default)
    {
        var response = await http.PostAsJsonAsync("api/topology/validate", root, SmartXJson.Options, ct);
        return await response.Content.ReadFromJsonAsync<TopologyReport>(SmartXJson.Options, ct);
    }

    public async Task<StressResult?> StressTopologyAsync(int depth, CancellationToken ct = default)
        => await http.GetFromJsonAsync<StressResult>($"api/topology/stress?depth={depth}", SmartXJson.Options, ct);

    /// <summary>Mirrors <c>AttachmentService.MaxBytes</c> so the browser rejects an oversized file first.</summary>
    public const long MaxUploadBytes = 25 * 1024 * 1024;
}

public sealed record RegistrationOutcome(bool Ok, SensorSnapshot? Snapshot, IDictionary<string, string[]> Errors);

public sealed record ValidationProblemResponse(Dictionary<string, string[]> Errors);

public sealed record MessageResponse(string Message);

public sealed record AggregateSide(string DisplayName, double Value, string Unit);

public sealed record AggregateValue(string Id, double Value);

public sealed record AggregateResult(
    AggregateSide Left,
    AggregateSide Right,
    AggregateValue Aggregate,
    AggregateValue Delta,
    bool LeftDrawsMore,
    bool Equal);

public sealed record StressResult(
    int RequestedDepth,
    int Ceiling,
    int MaxDepthReached,
    int NodesVisited,
    bool IsValid,
    bool Survived,
    List<TopologyIssue> Issues);
