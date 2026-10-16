using Microsoft.AspNetCore.SignalR.Client;
using SmartX.Shared;
using SmartX.Shared.Telemetry;

namespace SmartX.Client.Services;

public enum AlertFilter { Any, Alerting, Silent, Normal }

/// <summary>
/// Everything the Command Stream page shows, held outside the page. Blazor disposes a page
/// when you navigate away, so state kept in the page is lost on every visit. This is scoped
/// (one per browser tab, for the app's lifetime): the push connection, the latest data and
/// the operator's filters all survive moving between pages and come straight back.
/// </summary>
public sealed class CommandStreamState(GatewayClient gateway) : IAsyncDisposable
{
    private HubConnection? _hub;
    private Task? _starting;

    public event Action? Changed;

    public bool Connected { get; private set; }
    public IReadOnlyList<SensorSnapshot> Snapshots { get; private set; } = [];
    public GatewayStatus? Status { get; private set; }
    public IReadOnlyList<Alert> Alerts { get; private set; } = [];
    public CommandLog? Commands { get; private set; }
    public QueueStatus? Queue { get; private set; }
    public Suggestion? Suggestion { get; private set; }
    public IReadOnlyList<TrendFlag> Trends { get; private set; } = [];
    public IReadOnlyList<TimelinePoint> Timeline { get; private set; } = [];

    // Operator choices; they persist because this object does.
    public string SearchText { get; set; } = string.Empty;
    public SensorCategory? Category { get; set; }
    public AlertFilter AlertState { get; set; }
    public string TimelineSensor { get; set; } = string.Empty;

    /// <summary>Safe to call on every visit: the first call connects, later calls return the same task.</summary>
    public Task EnsureStartedAsync() => _starting ??= StartAsync();

    private async Task StartAsync()
    {
        Snapshots = await gateway.GetSensorsAsync();
        Alerts = await gateway.GetAlertsAsync();
        await RefreshAsync();

        _hub = new HubConnectionBuilder()
            .WithUrl(new Uri(gateway.BaseAddress, "hubs/telemetry"))
            .AddJsonProtocol(options => SmartXJson.Configure(options.PayloadSerializerOptions))
            // Reference [47]: Microsoft. ASP.NET Core SignalR .NET client - automatically reconnect. Microsoft Learn.
            //   https://learn.microsoft.com/aspnet/core/signalr/dotnet-client#automatically-reconnect
            .WithAutomaticReconnect()
            .Build();

        _hub.On<List<SensorSnapshot>, GatewayStatus>(HubEvents.Tick, async (snapshots, status) =>
        {
            Snapshots = snapshots;
            Status = status;
            await RefreshAsync();
            Changed?.Invoke();
        });

        _hub.On<Alert>(HubEvents.AlertRaised, alert =>
        {
            Alerts = Alerts.Where(a => a.Id != alert.Id).Prepend(alert).ToList();
            Changed?.Invoke();
        });

        _hub.On<string>(HubEvents.AlertDismissed, id =>
        {
            Alerts = Alerts.Where(a => a.Id != id).ToList();
            Changed?.Invoke();
        });

        _hub.On<Suggestion>(HubEvents.SuggestionReady, suggestion =>
        {
            Suggestion = suggestion;
            Changed?.Invoke();
        });

        _hub.Reconnecting += _ => SetConnected(false);
        _hub.Reconnected += _ => SetConnected(true);
        _hub.Closed += _ => SetConnected(false);

        try
        {
            await _hub.StartAsync();
            Connected = true;
        }
        catch (Exception)
        {
            // Push unavailable: the page still shows what the REST calls returned.
        }

        Changed?.Invoke();
    }

    private Task SetConnected(bool connected)
    {
        Connected = connected;
        Changed?.Invoke();
        return Task.CompletedTask;
    }

    // The queue, history and trends have no push event of their own; the tick is the cue to re-read them.
    private async Task RefreshAsync()
    {
        Queue = await gateway.GetQueueAsync() ?? Queue;
        Commands = await gateway.GetCommandsAsync() ?? Commands;
        Trends = await gateway.GetTrendsAsync();
        Suggestion ??= await gateway.GetSuggestionAsync();

        if (!Snapshots.Any(s => s.Id == TimelineSensor))
            TimelineSensor = (Snapshots.FirstOrDefault(s => s.Payload != PayloadKind.Boolean) ?? Snapshots.FirstOrDefault())?.Id ?? string.Empty;
        if (TimelineSensor.Length > 0)
            Timeline = await gateway.GetTimelineAsync(TimelineSensor) ?? Timeline;
    }

    public async Task SelectTimelineAsync(string sensorId)
    {
        TimelineSensor = sensorId;
        Timeline = await gateway.GetTimelineAsync(sensorId) ?? [];
        Changed?.Invoke();
    }

    public async Task CommandAsync(string sensorId, bool state)
    {
        await gateway.CommandDeviceAsync(sensorId, state);
        Commands = await gateway.GetCommandsAsync() ?? Commands;
        Changed?.Invoke();
    }

    public async Task ShutAllValvesAsync()
    {
        await gateway.CommandCategoryAsync(SensorCategory.IrrigationValve, false);
        Commands = await gateway.GetCommandsAsync() ?? Commands;
        Changed?.Invoke();
    }

    public async Task UndoAsync()
    {
        await gateway.UndoCommandAsync();
        Commands = await gateway.GetCommandsAsync() ?? Commands;
        Changed?.Invoke();
    }

    public bool HasOpenAlert(string sensorId) => Alerts.Any(a => a.SensorId == sensorId && !a.Suppressed);

    public bool Matches(SensorSnapshot s)
    {
        if (Category is { } c && s.Category != c) return false;

        var open = HasOpenAlert(s.Id);
        var stateOk = AlertState switch
        {
            AlertFilter.Alerting => open,
            AlertFilter.Silent => s.IsSilent,
            AlertFilter.Normal => !open && !s.IsSilent,
            _ => true
        };
        if (!stateOk) return false;

        var term = SearchText.Trim();
        return term.Length == 0
            || s.DisplayName.Contains(term, StringComparison.OrdinalIgnoreCase)
            || s.Id.Contains(term, StringComparison.OrdinalIgnoreCase)
            || s.DeploymentPath.Contains(term, StringComparison.OrdinalIgnoreCase)
            || s.Category.Spec().Label.Contains(term, StringComparison.OrdinalIgnoreCase);
    }

    public async ValueTask DisposeAsync()
    {
        if (_hub is not null) await _hub.DisposeAsync();
    }
}
