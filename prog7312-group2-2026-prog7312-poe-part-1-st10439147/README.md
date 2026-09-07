# Smart-X IoT Mesh Gateway — Part 1, Task 2

Ingestion API and configuration dashboard for the Smart-X hybrid IoT ecosystem: an
ASP.NET Core Minimal API on **.NET 10** receiving multi-typed telemetry from a simulated
ESP32 mesh, paired with a **Blazor WebAssembly** operator console.

The API serves the client's static files, so the whole system is **one process on one
port** — no CORS configuration, no second terminal.

> Solution and projects live under [`ST10439147_PROG7312_SmartX_POE/`](ST10439147_PROG7312_SmartX_POE).
> The API project folder keeps the POE student-number name; its assembly and root
> namespace are `SmartX.Api`, so every path below that names `ST10439147_PROG7312_SmartX_POE`
> as a project is the API.

---

## Quick start

Three commands, from the `ST10439147_PROG7312_SmartX_POE/` directory (the one holding
`ST10439147_PROG7312_SmartX_POE.slnx`).

```bash
dotnet restore ST10439147_PROG7312_SmartX_POE.slnx
```

```bash
dotnet build ST10439147_PROG7312_SmartX_POE.slnx -c Release
```

```bash
dotnet run --project ST10439147_PROG7312_SmartX_POE --launch-profile http
```

Then open **<http://localhost:5036>**. The mesh simulator seeds 12 nodes and starts
publishing immediately, so the dashboard has live data within a second or two.

For HTTPS instead, use `--launch-profile https` and open <https://localhost:7030>
(run `dotnet dev-certs https --trust` once if the browser objects to the certificate).

### Prerequisites

| Requirement | Version | Check |
|---|---|---|
| .NET SDK | 10.0 or later | `dotnet --list-sdks` |
| Docker (optional) | any recent | `docker --version` |

Nothing else. No database, no message broker, no Node toolchain.

---

## Verifying the build

The non-trivial logic ships with a runnable self-check — ring buffer wraparound,
operator overloading, the no-boxing claim, recursive validation under hostile depth, and
the attachment crypto:

```bash
dotnet run --project ST10439147_PROG7312_SmartX_POE -- --selftest
```

Expected output ends with `all checks passed` and exit code 0:

```
  ok  ring buffer: wraps, preserves order, indexes oldest-first
  ok  operators: +, -, unary -, ==, !=, <, >, <=, >= over float and int
  ok  generics: float/int/bool through one wrapper, 0 bytes allocated over 50 000 ingests
  ok  arrays: ragged float[][] staging, float[,] projection, flush into List<T>
  ok  recursion: valid four-tier tree accepted, breadcrumb path resolves
  ok  recursion: tier-skipping and duplicate sensor mounts rejected with paths
  ok  recursion: 50 000-tier payload stopped at depth 64, no stack overflow
  ok  recursion: cycle in the deployment graph detected and reported
  ok  serialisation: TelemetryPacket<float> and <bool> round-trip through JSON
  ok  crypto: AES-256-GCM round-trips at 0, 1, 1 KB, 64 KB and multi-chunk sizes
  ok  crypto: bit-flips, truncation and wrong keys are all rejected
  ok  uploads: magic-byte sniffing, text validation and path-traversal stripping
```

That `0 bytes allocated over 50 000 ingests` line is measured with
`GC.GetAllocatedBytesForCurrentThread()`, not asserted in a comment — if anything on the
ingest path started boxing, this check fails.

---

## Running in Docker

From the `ST10439147_PROG7312_SmartX_POE/` directory:

```bash
docker compose up --build
```

Open <http://localhost:8080>. The image runs the self-check during build, so a broken
build never produces a runnable image. Encrypted attachments persist in the
`smartx-storage` volume.

To build and run the image directly instead:

```bash
docker build -t smartx-gateway:part1 . && docker run --rm -p 8080:8080 smartx-gateway:part1
```

---

## Using the console

1. **Gateway** (`/`) — landing page with the three architectural pillars. Pillar 1 is
   live; pillars 2 and 3 are visibly disabled and labelled with the Part they arrive in.
   The header shows a real measured round-trip to the API, refreshed every 10 seconds.
2. **Ingestion** (`/ingestion`) — register a node (MAC, display name, deployment path,
   category, payload type, publish interval, threshold, unit), attach a config file,
   hardware log or deployment photo, and validate the deployment tree. A node registered
   here is adopted by the simulator and starts publishing on the next cycle.
3. **Live dashboard** (`/dashboard`) — the severity-ranked alert rail, the small-multiple
   sensor grid, the drill-down inspector, and a panel that aggregates two meters through
   the overloaded `+`.

### Things worth trying

- Register a sensor with MAC `nonsense` — the browser blocks it, and `curl`-ing the same
  payload straight at `POST /api/sensors` gets the same field-level rejection from the
  server. The client-side check is convenience; the server's is the trust boundary.
- Upload an `.exe` renamed to `.png`. Rejected on content, not extension.
- Click **Stress the depth guard** on the ingestion page. A 50 000-tier deployment tree
  goes at the recursive validator and comes back as an error at depth 64 instead of
  killing the process.
- Dismiss the same kind of alert three times on one node. It stops competing for the top
  of the rail and collapses into the auditable suppressed group — it is never discarded.

---

## Architecture

```
ST10439147_PROG7312_SmartX_POE/
├── ST10439147_PROG7312_SmartX_POE.slnx
├── SmartX.Shared                      ← domain types, referenced by BOTH ends of the wire
│   ├── Telemetry/TelemetryPacket.cs      generic wrapper, T : struct
│   ├── Telemetry/SensorReading.cs        numeric half, T : struct, INumber<T>
│   ├── Collections/CircularTelemetryBuffer.cs   custom ring buffer
│   ├── Ingestion/TelemetryBatchArchive.cs       jagged float[][] → List<T>
│   ├── Topology/TopologyValidator.cs     recursive validation
│   └── Diagnostics/SelfCheck.cs          the runnable check
├── ST10439147_PROG7312_SmartX_POE     ← Minimal API (assembly SmartX.Api): SignalR hub,
│                                        mesh simulator, attachments
└── SmartX.Client                      ← Blazor WebAssembly console
```

`SmartX.Shared` being referenced by both projects is the load-bearing decision:
`TelemetryPacket<T>` is the same type in the gateway and in the browser, so there is no
DTO translation layer and no chance of the two sides drifting.

### Technical requirements, and where they live

| Requirement | Implementation |
|---|---|
| **Generics** | `TelemetryPacket<T> where T : struct` carries float, int and bool through one wrapper. `TelemetryStore` keeps a *typed bucket per payload kind* rather than a shared `List<object>` — that is what keeps the ingest path off the boxing path, and the self-check measures it. |
| **Operator overloading** | `SensorReading<T> where T : struct, INumber<T>` defines `+` (aggregate two meters), `-` (delta), unary `-`, `==`/`!=`, and the four relational operators both against another reading and against a bare `T`, so rule code reads `reading > limit`. |
| **Arrays** | Ragged poll cycles stage in `float[][]` (each cycle really does return a different sample count), project to a rectangular `float[,]` for the heat map, then flush into `List<TelemetryPacket<float>>`. |
| **Recursion** | `TopologyValidator` walks Facility → Zone → Sub-Zone → Node, reporting issues by breadcrumb path, with a depth ceiling and a cycle guard so a malformed tree is an error rather than a `StackOverflowException`. |
| **Collections** | `CircularTelemetryBuffer<T>` — fixed-capacity, array-backed, `IEnumerable<T>`. An unbounded stream into a `List<T>` grows until the gateway dies; the ring bounds memory *and* provides exactly the window the anomaly detector needs for its rolling baseline. |
| **File upload** | `MultipartReader` streaming (never `IFormFile` buffering), magic-byte content sniffing, path-traversal stripping, a 25 MB ceiling enforced mid-flight, and AES-256-GCM at rest with per-chunk nonces and authenticated chunk indices. |
| **Engagement** | The strategy chosen in Part 1 Task 1: anomaly-first visual encoding, severity-ranked rail, z-score spike detection against a rolling baseline, a *separate* silence timer for disconnects, and suppression-not-deletion of repeatedly dismissed alerts. |

---

## API reference

Base path `/api`. Enums travel as names.

| Method | Route | Purpose |
|---|---|---|
| `GET` | `/health` | Gateway status: uptime, node count, throughput, alert counts |
| `GET` | `/sensors` | All sensor snapshots (value, baseline, sparkline, silence state) |
| `GET` | `/sensors/{id}` | One snapshot |
| `GET` | `/sensors/{id}/profile` | The stored registration |
| `POST` | `/sensors` | Register a node — 400 with field-level errors on invalid input |
| `DELETE` | `/sensors/{id}` | Deregister |
| `GET` | `/sensors/{id}/history?samples=n` | Typed `TelemetryPacket<T>` history straight off the ring |
| `GET` | `/sensors/{id}/archive` | Jagged staging area and its rectangular projection, side by side |
| `GET` | `/telemetry/aggregate?a=&b=` | Operator overloading over HTTP: sum, delta, comparison |
| `GET` | `/alerts?includeSuppressed=` | Severity-ranked alert rail |
| `POST` | `/alerts/{id}/dismiss` | Dismiss, and count toward suppression of that kind |
| `POST` | `/alerts/restore` | Un-suppress a kind for a node |
| `GET` | `/topology` | The seeded deployment tree |
| `POST` | `/topology/validate` | Recursive validation of a supplied tree |
| `GET` | `/topology/stress?depth=` | Depth-guard demonstration |
| `POST` | `/sensors/{id}/attachments` | Streamed multipart upload |
| `GET` | `/sensors/{id}/attachments` | Attachment metadata for a node |
| `GET` | `/attachments/{attachmentId}` | Decrypt and download |

Real-time channel: SignalR hub at `/hubs/telemetry`, pushing `tick`, `alertRaised`,
`alertDismissed` and `sensorsChanged`.

Quick check with the server running:

```bash
curl http://localhost:5036/api/health
```

---

## Configuration

Client — `SmartX.Client/wwwroot/appsettings.json`, fetched at startup, so a published
client can be repointed without a rebuild:

```json
{ "Gateway": { "BaseUrl": "" } }
```

Blank means "the origin that served this page", which is how the hosted setup runs. Set an
absolute URL to point the dashboard at a remote gateway.

Server — `ST10439147_PROG7312_SmartX_POE/appsettings.json`, or environment variables using
the `SmartX__Key` form:

| Key | Default | Notes |
|---|---|---|
| `SmartX:AttachmentRoot` | `storage/attachments` | Encrypted blobs. Outside the web root. |
| `SmartX:AttachmentKeyPath` | `storage/attachment.key` | Where a generated development key is kept. |
| `SmartX:AttachmentKey` | *(empty)* | Base64 of 32 bytes. **Set this in production**; leaving it blank makes the gateway generate a key and log a warning. |

Generate a real key (PowerShell, no extra tooling):

```bash
powershell -Command "[Convert]::ToBase64String((1..32|%{Get-Random -Max 256}))"
```

On Linux or macOS, `openssl rand -base64 32` does the same job.

---

## Known scope

Telemetry is **simulated** — `MeshSimulator` stands in for the MQTT feed, injecting value
spikes and node disconnects so the alerting path has something real to catch. It writes
through the same `TelemetryStore.Ingest` call the HTTP path uses, so swapping in a real
broker changes nothing downstream.

Gateway state is **in memory** and resets on restart; only attachments survive, on disk
and encrypted. Persistence, the command stream and mesh routing arrive in Part 2 and the
final PoE.
