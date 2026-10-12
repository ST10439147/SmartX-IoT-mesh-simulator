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
    return SelfCheckRunner.Run();

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
// Reference [23]: Microsoft. Background tasks with hosted services in ASP.NET Core. Microsoft Learn.
//   https://learn.microsoft.com/aspnet/core/fundamentals/host/hosted-services
builder.Services.AddHostedService<MeshSimulator>();
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

api.MapGet("/sensors/{id}/profile", (string id, TelemetryStore store) =>
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

    if (store.Exists(registration.MacAddress.Replace('-', ':').ToUpperInvariant()))
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

// ---------------------------------------------------------------- telemetry

// The ingest door for the mesh: the simulator posts here exactly as a real node would.
// The sensor's registered payload kind decides how the value is read, so a float node
// cannot be fed a bool and the packet lands in its typed buffer without boxing.
api.MapPost("/telemetry", async (
    TelemetryReading reading,
    TelemetryStore store,
    AnomalyEngine anomalies,
    IHubContext<TelemetryHub> hub) =>
{
    if (reading.SensorId is null || !store.TryGet(reading.SensorId, out var sensor))
        return Results.NotFound(new { message = $"No sensor registered with id '{reading.SensorId}'." });

    var value = reading.Value;

    switch (sensor.Payload)
    {
        case PayloadKind.Float when value.ValueKind == JsonValueKind.Number && value.TryGetSingle(out var f) && float.IsFinite(f):
            store.Ingest(new TelemetryPacket<float>(sensor.Id, f, sensor.Category));
            break;
        case PayloadKind.Integer when value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var i):
            store.Ingest(new TelemetryPacket<int>(sensor.Id, i, sensor.Category));
            break;
        case PayloadKind.Boolean when value.ValueKind is JsonValueKind.True or JsonValueKind.False:
            store.Ingest(new TelemetryPacket<bool>(sensor.Id, value.GetBoolean(), sensor.Category));
            break;
        default:
            return Results.BadRequest(new { message = $"Sensor '{sensor.Id}' expects a {sensor.Payload} value." });
    }

    var alert = anomalies.Evaluate(sensor);
    if (alert is not null) await hub.Clients.All.SendAsync(HubEvents.AlertRaised, alert);

    return Results.Accepted();
});

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
    Validator.TryValidateObject(model, new ValidationContext(model), results, validateAllProperties: true);

    return results
        .SelectMany(r => r.MemberNames.DefaultIfEmpty(string.Empty).Select(m => (Member: m, r.ErrorMessage)))
        .GroupBy(x => x.Member)
        .ToDictionary(g => g.Key, g => g.Select(x => x.ErrorMessage ?? "Invalid value.").ToArray());
}

// The request body is a simple JSON object with two properties, so a record is a good fit.
internal sealed record RestoreRequest(string SensorId, AlertKind Kind);

// Value stays a JsonElement until the sensor's payload kind says which type to read it as.
internal sealed record TelemetryReading(string SensorId, JsonElement Value);
//---------------------------DDDDDDDDDDDoooooooooooooo END OF FILE DDDDDDDDDDDoooooooooooooo---------------------------//