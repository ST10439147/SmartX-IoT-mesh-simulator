using System.ComponentModel.DataAnnotations;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR;
using SmartX.Api;
using SmartX.Api.Hubs;
using SmartX.Api.Services;
using SmartX.Shared;
using SmartX.Shared.Telemetry;
using SmartX.Shared.Topology;

// `dotnet run --project src/SmartX.Api -- --selftest` runs the data-structure and
// crypto checks and exits. Non-zero means something is broken.
if (args.Contains("--selftest"))
    return await SelfCheckRunner.RunAsync();

var builder = WebApplication.CreateBuilder(args);

builder.Services.Configure<Microsoft.AspNetCore.Http.Json.JsonOptions>(
    options => SmartXJson.Configure(options.SerializerOptions));

// Reference [25]: Microsoft. Configure options for the ASP.NET Core Kestrel web server - MaxRequestBodySize. Microsoft Learn.
//   https://learn.microsoft.com/aspnet/core/fundamentals/servers/kestrel/options
builder.WebHost.ConfigureKestrel(options =>
{
    // The multipart reader streams, but Kestrel still needs a hard ceiling so a runaway
    // upload cannot be used to fill the gateway's disk.
    options.Limits.MaxRequestBodySize = AttachmentService.MaxBytes + 1024 * 1024;
});
//----------------------------------------------------------------------------------------------------------------------//
// The services are singletons because they hold the state of the mesh and its telemetry.
builder.Services.AddSingleton<TelemetryStore>();
builder.Services.AddSingleton<AnomalyEngine>();
builder.Services.AddSingleton<AttachmentService>();
builder.Services.AddSingleton<IngestQueue>();
builder.Services.AddSingleton<CommandService>();
builder.Services.AddSingleton(services => new PredictionService(
    services.GetRequiredService<TelemetryStore>(), services.GetRequiredService<AnomalyEngine>(), "storage/predictions.json"));
// Reference [23]: Microsoft. Background tasks with hosted services in ASP.NET Core. Microsoft Learn.
//   https://learn.microsoft.com/aspnet/core/fundamentals/host/hosted-services
builder.Services.AddHostedService<IngestWorker>();
// Registered as a singleton too, so the unplug endpoint can reach the running simulator.
builder.Services.AddSingleton<MeshSimulator>();
builder.Services.AddHostedService(services => services.GetRequiredService<MeshSimulator>());
// Reference [22]: Microsoft. Introduction to ASP.NET Core SignalR. Microsoft Learn.
//   https://learn.microsoft.com/aspnet/core/signalr/introduction
builder.Services.AddSignalR().AddJsonProtocol(options => SmartXJson.Configure(options.PayloadSerializerOptions));
// Reference [7]: Microsoft. Response compression in ASP.NET Core. Microsoft Learn.
//   https://learn.microsoft.com/aspnet/core/performance/response-compression
builder.Services.AddResponseCompression(options =>
{
    options.MimeTypes = ["application/octet-stream", "application/json", "text/css", "application/javascript", "text/html", "application/wasm"];
});

var app = builder.Build();

app.UseResponseCompression();

// Serve the Blazor WebAssembly client from the same origin as the API. One process, one
// port, no CORS preflight on every telemetry call.
// Reference [4]: Microsoft. ASP.NET Core Blazor hosting models. Microsoft Learn.
//   https://learn.microsoft.com/aspnet/core/blazor/hosting-models
app.UseBlazorFrameworkFiles();
app.UseStaticFiles();

// Reference [5]: Microsoft. Minimal APIs overview. Microsoft Learn.
//   https://learn.microsoft.com/aspnet/core/fundamentals/minimal-apis/overview
var api = app.MapGroup("/api");

// ---------------------------------------------------------------- gateway health

api.MapGet("/health", (TelemetryStore store, AnomalyEngine anomalies) => store.Status(anomalies))
   .WithName("GatewayHealth");

// ---------------------------------------------------------------- sensors

api.MapGet("/sensors", (TelemetryStore store) => store.Snapshots());

api.MapGet("/sensors/{id}", (string id, TelemetryStore store) =>
    store.TryGet(id, out var sensor)
        ? Results.Ok(store.Snapshot(sensor))
        : Results.NotFound(new { message = $"No sensor registered with id '{id}'." }));

// Operator search: case-insensitive substring over name, id, deployment path and category.
// It runs on the gateway rather than filtering in the browser so every search is an event
// the gateway sees - the predictive engine learns from them.
api.MapGet("/search", async ([FromQuery] string? q, TelemetryStore store, PredictionService predictions) =>
{
    if (string.IsNullOrWhiteSpace(q)) return Results.BadRequest(new { message = "Give a search term (q)." });

    var term = q.Trim();
    await predictions.RecordAsync(OperatorActionKind.Search, term);

    return Results.Ok(store.Snapshots().Where(s =>
        s.DisplayName.Contains(term, StringComparison.OrdinalIgnoreCase)
        || s.Id.Contains(term, StringComparison.OrdinalIgnoreCase)
        || s.DeploymentPath.Contains(term, StringComparison.OrdinalIgnoreCase)
        || s.Category.Spec().Label.Contains(term, StringComparison.OrdinalIgnoreCase)).ToList());
});

api.MapGet("/sensors/{id}/profile",(string id, TelemetryStore store) =>
    store.TryGet(id, out var sensor) ? Results.Ok(sensor) : Results.NotFound());

// Reference [17]: Microsoft. Handle errors in ASP.NET Core - problem details / ValidationProblem. Microsoft Learn.
//   https://learn.microsoft.com/aspnet/core/fundamentals/minimal-apis/handle-errors
api.MapPost("/sensors", async (
    SensorRegistration registration,
    TelemetryStore store,
    IHubContext<TelemetryHub> hub) =>
{
    // Server-side validation is not a duplicate of the client's - the client's is a
    // convenience, this is the trust boundary. A direct POST bypasses the form entirely.
    var errors = Validate(registration);

    if (store.Exists(registration.MacAddress))
        errors.TryAdd(nameof(registration.MacAddress), ["A sensor with this MAC address is already registered."]);

    if (!TopologyValidator.PathExists(store.Topology, registration.DeploymentPath))
    {
        errors.TryAdd(nameof(registration.DeploymentPath),
            [$"'{registration.DeploymentPath}' does not resolve in the deployment tree. Use GET /api/topology to see valid paths."]);
    }

    if (errors.Count > 0) return Results.ValidationProblem(errors);

    var saved = store.Register(registration);
    await hub.Clients.All.SendAsync(HubEvents.SensorsChanged, saved.Id);

    return Results.Created($"/api/sensors/{saved.Id}", store.Snapshot(saved));
});
//----------------------------------------------------------------------------------------------------------------------//
api.MapDelete("/sensors/{id}", async (string id, TelemetryStore store, IHubContext<TelemetryHub> hub) =>
{
    if (!store.Remove(id)) return Results.NotFound();
    await hub.Clients.All.SendAsync(HubEvents.SensorsChanged, id);
    return Results.NoContent();
});

// ---------------------------------------------------------------- device registry

// The dictionary behind every packet arrival, made visible: how many devices it holds,
// how many lookups it has answered and how many of those missed. To look one device up
// by MAC or id, in any spelling, use GET /api/sensors/{id}.
api.MapGet("/registry", (TelemetryStore store) => Results.Ok(new
{
    devices = store.Sensors.Count,
    lookups = store.Lookups,
    misses = store.Misses,
    keys = store.Sensors.Select(s => s.Id).Order()
}));

// ---------------------------------------------------------------- telemetry

// The ingest door for the mesh: the simulator posts here exactly as a real node would.
// The sensor's registered payload kind decides how the value is read, so a float node
// cannot be fed a bool. A valid reading is only queued here - the IngestWorker lands it
// in the store and raises any alert, so the node gets its 202 without waiting for either.
api.MapPost("/telemetry", (TelemetryReading reading, TelemetryStore store, AnomalyEngine anomalies, IngestQueue queue) =>
{
    if (reading.SensorId is null || !store.TryGet(reading.SensorId, out var sensor))
        return Results.NotFound(new { message = $"No sensor registered with id '{reading.SensorId}'." });

    // A node may stamp the reading with its own clock. An old timestamp is a late packet
    // and is welcome; one from the future is a broken clock, and would sit at the end of
    // the timeline ahead of every real reading.
    if (reading.Timestamp > DateTimeOffset.UtcNow.AddSeconds(5))
        return Results.BadRequest(new { message = "Timestamp is in the future." });

    var value = reading.Value;
    double queued;

    switch (sensor.Payload)
    {
        case PayloadKind.Float when value.ValueKind == JsonValueKind.Number && value.TryGetSingle(out var f) && float.IsFinite(f):
            queued = f;
            break;
        case PayloadKind.Integer when value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var i):
            queued = i;
            break;
        case PayloadKind.Boolean when value.ValueKind is JsonValueKind.True or JsonValueKind.False:
            queued = value.GetBoolean() ? 1 : 0;
            break;
        default:
            return Results.BadRequest(new { message = $"Sensor '{sensor.Id}' expects a {sensor.Payload} value." });
    }

    // A reading that would breach a threshold or is a spike goes in the priority lane.
    return queue.TryEnqueue(sensor.Id, queued, anomalies.Classify(sensor, queued), reading.Timestamp)
        ? Results.Accepted()
        : Results.Problem("Ingest queue is full.", statusCode: StatusCodes.Status503ServiceUnavailable);
});

// ---------------------------------------------------------------- ingest queue

// Queue depth per lane plus the trail of recently processed packets. `position` is the
// order the worker handled a packet in, `sequence` the order it arrived in - a critical
// packet shows up with a high sequence at a low position.
api.MapGet("/queue", (IngestQueue queue) => Results.Ok(new
{
    depth = queue.Depth,
    criticalDepth = queue.CriticalDepth,
    processed = queue.Processed,
    throttleMs = queue.Throttle.TotalMilliseconds,
    recent = queue.Recent.Select(p => new
    {
        p.Position,
        p.Packet.Sequence,
        lane = p.Packet.Critical is null ? "normal" : "critical",
        p.Packet.Critical,
        p.Packet.SensorId,
        p.Packet.Value,
        p.Packet.ReceivedAt,
        waitedMs = Math.Round((p.ProcessedAt - p.Packet.ReceivedAt).TotalMilliseconds)
    })
}));

// Test trigger: backs the normal lane up with ordinary packets and slows the worker so
// the backlog lasts long enough to watch. Each flood packet repeats its sensor's latest
// reading, so it is guaranteed normal. POST an out-of-range value to /api/telemetry
// while this drains and GET /api/queue to see it processed ahead of the backlog.
api.MapPost("/queue/flood", (TelemetryStore store, IngestQueue queue, [FromQuery] int count = 200, [FromQuery] int delayMs = 20) =>
{
    var sensors = store.Sensors.Where(s => !double.IsNaN(store.LatestNumeric(s.Id))).ToList();
    if (sensors.Count == 0) return Results.Conflict(new { message = "No sensor has published yet, nothing to flood with." });

    // ponytail: the slowed worker delays live readings too, so a long flood can trip the
    // silence detector (3 missed publish intervals). The clamps keep it short.
    queue.Throttle = TimeSpan.FromMilliseconds(Math.Clamp(delayMs, 0, 250));

    var enqueued = 0;
    for (var i = 0; i < Math.Clamp(count, 1, 2_000); i++)
    {
        var sensor = sensors[i % sensors.Count];
        if (queue.TryEnqueue(sensor.Id, store.LatestNumeric(sensor.Id))) enqueued++;
    }

    return Results.Accepted("/api/queue", new { enqueued, depth = queue.Depth, throttleMs = queue.Throttle.TotalMilliseconds });
});

// ---------------------------------------------------------------- manual override commands

// The undo stack, newest command first, plus which actuators are pinned right now.
api.MapGet("/commands", (CommandService commands) => Results.Ok(new
{
    history = commands.History,
    overrides = commands.Overrides
}));

// Just the undo stack, newest command first.
api.MapGet("/commands/history", (CommandService commands) => Results.Ok(commands.History));

// Pins one actuator (sensorId) or every actuator of a kind (category) on or off.
// "Shut all valves" is { "category": "IrrigationValve", "state": false }.
api.MapPost("/commands", async (CommandRequest request, TelemetryStore store, CommandService commands, PredictionService predictions) =>
{
    List<SensorRegistration> targets;

    if (request.SensorId is not null)
    {
        if (!store.TryGet(request.SensorId, out var sensor))
            return Results.NotFound(new { message = $"No sensor registered with id '{request.SensorId}'." });

        targets = [sensor];
    }
    else if (request.Category is { } category)
    {
        targets = store.Sensors.Where(s => s.Category == category).OrderBy(s => s.Id).ToList();
    }
    else
    {
        return Results.BadRequest(new { message = "Give a sensorId or a category to command." });
    }

    // Only actuators take an on/off command - a moisture probe has nothing to switch.
    // A pump counts: it reports the power it draws, but it can still be started and stopped.
    if (targets.Count == 0 || targets.Any(s => s.Payload != PayloadKind.Boolean && s.Category != SensorCategory.PumpPower))
        return Results.BadRequest(new { message = "Override commands only apply to registered actuators (valves, vents, pumps)." });

    var action = request.State ? "on" : "off";
    var label = request.SensorId is not null
        ? $"Switch {targets[0].DisplayName} {action}"
        : $"Switch all {targets[0].Category.Spec().Label} devices {action}";

    var command = commands.Issue(label, targets.Select(s => s.Id), request.State);
    await predictions.RecordAsync(OperatorActionKind.Command, label);
    return Results.Ok(command);
});

api.MapPost("/commands/undo", async (CommandService commands, PredictionService predictions) =>
{
    if (!commands.TryUndo(out var undone)) return Results.Conflict(new { message = "Nothing to undo." });

    await predictions.RecordAsync(OperatorActionKind.Command, $"Undo {undone!.Label}");
    return Results.Ok(undone);
});

// ---------------------------------------------------------------- predictive action engine

// Every search and command, newest first, with the alert (Category|AlertKind) that was
// on the rail when it was made. A null context means nothing was alerting.
api.MapGet("/predictions/log", (PredictionService predictions, CancellationToken ct) => predictions.LogAsync(ct));

// Forgets everything learned so the engine can be shown learning from scratch.
api.MapPost("/predictions/reset", async (PredictionService predictions) =>
{
    await predictions.ResetAsync();
    return Results.NoContent();
});

// The dictionary of counters: every event seen, each with its follow-ups ranked.
api.MapGet("/predictions/pairs", (PredictionService predictions, CancellationToken ct) => predictions.PairsAsync(ct));

// The suggestion for the alert that is driving the operator right now, so a dashboard
// opened mid-alert still shows it. 204 when there is none. New ones are pushed as
// HubEvents.SuggestionReady the moment their alert is raised.
api.MapGet("/predictions/suggestion", async (PredictionService predictions, CancellationToken ct) =>
    await predictions.CurrentAsync(ct) is { } suggestion ? Results.Ok(suggestion) : Results.NoContent());

// Every learned event with its ranked follow-ups, open faults first; seeded on first run and kept across restarts.
api.MapGet("/predictions/insights", (PredictionService predictions, CancellationToken ct) => predictions.InsightsAsync(ct));

// Devices under their threshold but projected to cross it within the trend horizon, soonest first.
api.MapGet("/predictions/trends", (PredictionService predictions, CancellationToken ct) => predictions.TrendFlagsAsync(ct));

// The top follow-ups to one event, e.g. ?context=SoilMoisture|ThresholdBreach&top=3.
api.MapGet("/predictions/rank", ([FromQuery] string context, PredictionService predictions, CancellationToken ct, [FromQuery] int top = 3) =>
    predictions.RankAsync(context, Math.Clamp(top, 1, 20), ct));

// Returns the typed packets themselves, so TelemetryPacket<T> is what crosses the wire
// rather than a flattened DTO. The client deserialises straight back into the same type.
api.MapGet("/sensors/{id}/history", (string id, TelemetryStore store, [FromQuery] int samples = 120) =>
{
    if (!store.TryGet(id, out var sensor)) return Results.NotFound();

    return sensor.Payload switch
    {
        PayloadKind.Float => Results.Ok(store.Floats(id)?.TakeLast(samples) ?? []),
        PayloadKind.Integer => Results.Ok(store.Integers(id)?.TakeLast(samples) ?? []),
        PayloadKind.Boolean => Results.Ok(store.Booleans(id)?.TakeLast(samples) ?? []),
        _ => Results.NotFound()
    };
});

// The sensor's log in timestamp order, straight out of its SortedDictionary. `sequence`
// is the order each reading arrived in; where it runs backwards, a packet arrived late.
api.MapGet("/sensors/{id}/timeline", (string id, TelemetryStore store, [FromQuery] int samples = 60) =>
    store.TryGet(id, out var sensor)
        ? Results.Ok(store.Timeline(sensor.Id, samples))
        : Results.NotFound());

// Test trigger: sends a handful of backdated readings for one sensor in shuffled order,
// the way a mesh delivers packets that took different routes. Each repeats the sensor's
// latest value, so none of them is an anomaly. GET the timeline afterwards: `at` still
// ascends while `sequence` does not.
api.MapPost("/sensors/{id}/timeline/scramble", (string id, TelemetryStore store, IngestQueue queue, [FromQuery] int count = 8) =>
{
    if (!store.TryGet(id, out var sensor)) return Results.NotFound();

    var value = store.LatestNumeric(sensor.Id);
    if (double.IsNaN(value)) return Results.Conflict(new { message = "This sensor has not published yet." });

    // One per publish interval going back from now, offset by half an interval so each
    // falls between two readings the node really sent. The newest is 1.5 intervals old,
    // so every one of them is older than a reading the gateway already has.
    var now = DateTimeOffset.UtcNow;
    var sent = Enumerable.Range(1, Math.Clamp(count, 2, 20))
        .Select(i => now.AddSeconds(-(i + 0.5) * sensor.PublishIntervalSeconds))
        .ToArray();

    // Oldest-first would be in order. A shuffle can land on that, so turn it round if it does.
    // Reference [48]: Microsoft. Random.Shuffle Method. Microsoft Learn.
    //   https://learn.microsoft.com/dotnet/api/system.random.shuffle
    Random.Shared.Shuffle(sent);
    if (sent.SequenceEqual(sent.Order())) Array.Reverse(sent);

    foreach (var at in sent) queue.TryEnqueue(sensor.Id, value, sentAt: at);

    return Results.Accepted($"/api/sensors/{sensor.Id}/timeline", new { sentInThisOrder = sent });
});

// Shows the jagged staging area and its rectangular projection side by side.
api.MapGet("/sensors/{id}/archive", (string id, TelemetryStore store) =>
{
    if (!store.TryGet(id, out var sensor)) return Results.NotFound();

    var archive = store.Archive(id);
    if (archive is null) return Results.Ok(new { message = "Only float sensors stage raw poll cycles." });

    var dense = archive.ToDenseMatrix();
    var flushed = archive.Flush(id, sensor.Category);

    return Results.Ok(new
    {
        cycles = archive.CycleCount,
        widestCycle = archive.WidestCycle,
        denseShape = new { rows = dense.GetLength(0), columns = dense.GetLength(1) },
        jagged = archive.ToJagged(),
        flushedPackets = flushed.Count,
        newest = flushed.TakeLast(5)
    });
});

// Operator overloading, exercised over HTTP: Meter3 = Meter1 + Meter2.
// Reference [8]: Microsoft. Operator overloading - predefined unary, arithmetic and comparison operators. C# language reference, Microsoft Learn.
//   https://learn.microsoft.com/dotnet/csharp/language-reference/operators/operator-overloading
api.MapGet("/telemetry/aggregate", ([FromQuery] string a, [FromQuery] string b, TelemetryStore store) =>
{
    if (!store.TryGet(a, out var left) || !store.TryGet(b, out var right))
        return Results.NotFound(new { message = "Both sensor ids must be registered." });

    if (left.Payload == PayloadKind.Boolean || right.Payload == PayloadKind.Boolean)
        return Results.BadRequest(new { message = "Actuator states are not numeric and cannot be aggregated." });

    var readingA = new SensorReading<double>(left.Id, store.LatestNumeric(left.Id));
    var readingB = new SensorReading<double>(right.Id, store.LatestNumeric(right.Id));

    var total = readingA + readingB;      // operator +
    var delta = readingA - readingB;      // operator -

    return Results.Ok(new
    {
        left = new { left.DisplayName, value = readingA.Value, left.Unit },
        right = new { right.DisplayName, value = readingB.Value, right.Unit },
        aggregate = new { id = total.SensorId, value = total.Value },
        delta = new { id = delta.SensorId, value = delta.Value },
        leftDrawsMore = readingA > readingB,      // operator >
        equal = readingA == readingB              // operator ==
    });
});

// ---------------------------------------------------------------- alerts

api.MapGet("/alerts", (AnomalyEngine anomalies, [FromQuery] bool includeSuppressed = true) =>
    anomalies.Ranked(includeSuppressed));

api.MapPost("/alerts/{id}/dismiss", async (string id, AnomalyEngine anomalies, IHubContext<TelemetryHub> hub) =>
{
    if (!anomalies.Dismiss(id)) return Results.NotFound();
    await hub.Clients.All.SendAsync(HubEvents.AlertDismissed, id);
    return Results.NoContent();
});

api.MapPost("/alerts/restore", (RestoreRequest request, AnomalyEngine anomalies) =>
{
    anomalies.Restore(request.SensorId, request.Kind);
    return Results.NoContent();
});

// The two hash sets behind "one fault, one alert": which nodes are known to be
// disconnected, which error states are open (as sensorId|Kind), and how many repeat
// alerts were skipped because the fault was already in a set.
api.MapGet("/faults", (AnomalyEngine anomalies) => Results.Ok(new
{
    disconnected = anomalies.Disconnected,
    activeErrors = anomalies.ActiveErrors,
    duplicatesSkipped = anomalies.DuplicatesSkipped
}));

// Test trigger: the simulated node stops publishing for a while, as if unplugged. The
// silence sweep finds it within a few seconds and keeps finding it; GET /api/alerts
// should still show one Silence alert for it. seconds=0 plugs it back in.
api.MapPost("/sensors/{id}/unplug", (string id, TelemetryStore store, MeshSimulator mesh, [FromQuery] int seconds = 60) =>
{
    if (!store.TryGet(id, out var sensor)) return Results.NotFound();

    seconds = Math.Clamp(seconds, 0, 600);
    mesh.Unplug(sensor.Id, TimeSpan.FromSeconds(seconds));

    return Results.Accepted("/api/faults", new { sensor.Id, silentForSeconds = seconds });
});

// Test trigger: the simulated node's reading starts climbing by perSecond units a second,
// for the trend flags in GET /api/predictions/trends to catch. Left out, the rate is set so
// the node reaches its threshold in about 90 seconds. perSecond=0 stops it and drops back.
api.MapPost("/sensors/{id}/drift", (string id, TelemetryStore store, MeshSimulator mesh, [FromQuery] double? perSecond) =>
{
    if (!store.TryGet(id, out var sensor)) return Results.NotFound();
    if (sensor.Payload == PayloadKind.Boolean) return Results.BadRequest(new { message = "Valves and vents report a state; there is nothing to drift." });

    var rate = perSecond ?? Math.Max(0, sensor.WarningThreshold - store.LatestNumeric(sensor.Id)) / 90;
    mesh.Drift(sensor.Id, rate);

    return Results.Accepted("/api/predictions/trends", new { sensor.Id, perSecond = rate });
});

// ---------------------------------------------------------------- topology (recursion)

api.MapGet("/topology", (TelemetryStore store) => store.Topology);

api.MapPost("/topology/validate", (DeploymentNode root) => TopologyValidator.Validate(root));

// Proves the depth guard through the API. System.Text.Json refuses to parse a body this
// deeply nested (its own 64-level ceiling), so the tree is built server-side instead.
// Reference [16]: Microsoft. StackOverflowException Class (cannot be caught by a try/catch). Microsoft Learn.
//   https://learn.microsoft.com/dotnet/api/system.stackoverflowexception
api.MapGet("/topology/stress", ([FromQuery] int depth = 50_000) =>
{
    depth = Math.Clamp(depth, 1, 200_000);

    var root = new DeploymentNode { Name = "Facility A", Kind = NodeKind.Facility };
    var cursor = root;
    for (var i = 0; i < depth; i++)
    {
        var child = new DeploymentNode { Name = $"tier-{i}", Kind = NodeKind.Zone };
        cursor.Children.Add(child);
        cursor = child;
    }

    var report = TopologyValidator.Validate(root);

    return Results.Ok(new
    {
        requestedDepth = depth,
        ceiling = TopologyValidator.MaxDepth,
        report.MaxDepthReached,
        report.NodesVisited,
        report.IsValid,
        survived = true,
        // The ceiling issue is the point of this endpoint, and it is raised last (at the
        // deepest frame), so it is hoisted rather than buried under the tier warnings.
        issues = report.Issues
            .Where(i => i.Message.Contains("ceiling"))
            .Concat(report.Issues.Where(i => !i.Message.Contains("ceiling")))
            .Take(3)
    });
});

// ---------------------------------------------------------------- attachments

// Reference [24]: Microsoft. Upload files in ASP.NET Core - uploading large files with streaming. Microsoft Learn.
//   https://learn.microsoft.com/aspnet/core/mvc/models/file-uploads
api.MapPost("/sensors/{id}/attachments", async (
    string id,
    HttpRequest request,
    AttachmentService attachments,
    CancellationToken ct) =>
{
    var result = await attachments.StoreAsync(request, id, ct);

    return result.Ok
        ? Results.Created($"/api/attachments/{result.Attachment!.Id}", result.Attachment)
        : Results.BadRequest(new { message = result.Error });
})
.DisableAntiforgery();   // token-free API surface; the body is read as a raw stream

api.MapGet("/sensors/{id}/attachments", (string id, TelemetryStore store) => store.Attachments(id));

api.MapGet("/attachments/{attachmentId}", async (
    string attachmentId,
    TelemetryStore store,
    AttachmentService attachments,
    HttpResponse response,
    CancellationToken ct) =>
{
    var info = store.FindAttachment(attachmentId);
    if (info is null) return Results.NotFound();

    response.ContentType = "application/octet-stream";
    response.Headers.ContentDisposition = $"attachment; filename=\"{info.FileName}\"";

    // Decrypts straight into the response stream - the plaintext never hits disk.
    return await attachments.WriteDecryptedAsync(info, response.Body, ct)
        ? Results.Empty
        : Results.NotFound();
});

// ---------------------------------------------------------------- realtime + client

app.MapHub<TelemetryHub>("/hubs/telemetry");
app.MapFallbackToFile("index.html");

app.Run();
return 0;

// DataAnnotations over the whole object, flattened into the shape ValidationProblem wants.
// Reference [17]: Microsoft. Handle errors in ASP.NET Core - problem details / ValidationProblem. Microsoft Learn.
//   https://learn.microsoft.com/aspnet/core/fundamentals/minimal-apis/handle-errors
static Dictionary<string, string[]> Validate(object model)
{
    var results = new List<ValidationResult>();
    // Reference [49]: Microsoft. Validator.TryValidateObject Method. Microsoft Learn.
    //   https://learn.microsoft.com/dotnet/api/system.componentmodel.dataannotations.validator.tryvalidateobject
    Validator.TryValidateObject(model, new ValidationContext(model), results, validateAllProperties: true);

    return results
        .SelectMany(r => r.MemberNames.DefaultIfEmpty(string.Empty).Select(m => (Member: m, r.ErrorMessage)))
        .GroupBy(x => x.Member)
        .ToDictionary(g => g.Key, g => g.Select(x => x.ErrorMessage ?? "Invalid value.").ToArray());
}

// The request body is a simple JSON object with two properties, so a record is a good fit.
internal sealed record RestoreRequest(string SensorId, AlertKind Kind);

// Exactly one of SensorId / Category picks the target; State is the position to pin it to.
internal sealed record CommandRequest(string? SensorId, SensorCategory? Category, bool State);

// Value stays a JsonElement until the sensor's payload kind says which type to read it as.
// Timestamp is optional: a node without a clock leaves it out and the gateway stamps arrival.
internal sealed record TelemetryReading(string SensorId, JsonElement Value, DateTimeOffset? Timestamp = null);
//---------------------------DDDDDDDDDDDoooooooooooooo END OF FILE DDDDDDDDDDDoooooooooooooo---------------------------//