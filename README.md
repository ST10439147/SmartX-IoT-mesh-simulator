# Smart-X IoT Mesh Gateway - Part 2

Ingestion API and configuration dashboard for the Smart-X hybrid IoT ecosystem: an
ASP.NET Core Minimal API on **.NET 10** receiving multi-typed telemetry from a simulated
ESP32 mesh, paired with a **Blazor WebAssembly** operator console.

The API serves the client's static files, so the whole system is **one process on one
port** - no CORS configuration, no second terminal.

> Solution and projects live under [`ST10439147_PROG7312_SmartX_POE/`](ST10439147_PROG7312_SmartX_POE).
> The API project folder keeps the POE student-number name; its assembly and root
> namespace are `SmartX.Api`, so every path below that names `ST10439147_PROG7312_SmartX_POE`
> as a project is the API.

---

## Youtube video links:
### Part 1 video
- https://youtu.be/FJqMu1NI1Ys 
### Part 2 video
- [Link]

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

Then open **<http://localhost:5036>**. The mesh simulator seeds 13 nodes and starts
publishing immediately, so the dashboard has live data within a second or two.

For HTTPS instead, use `--launch-profile https` and open <https://localhost:7030>
(run `dotnet dev-certs https --trust` once if the browser objects to the certificate).

### Or run it in Docker

From the same directory, on Linux, macOS or Windows:

```bash
docker compose up --build
```

Then open **<http://localhost:8080>**.

### **Windows only:**
 run or double-click

```
docker-setup.bat
```

It starts the container in
the background, waits for the gateway to answer, and opens the browser. It is a Windows batch
file, so it will not run on Linux or macOS - use `docker compose up --build` there.

The image build runs `--selftest` and fails if any check does. The container runs as
`Production` and keeps `/app/storage` in the `smartx-storage` volume. Stop it with
`docker compose down`.

### Prerequisites

| Requirement | Version | Check |
|---|---|---|
| .NET SDK | 10.0 or later | `dotnet --list-sdks` |
| Docker (Docker route only; replaces the SDK) | Compose v2 | `docker compose version` |

Nothing else. No database, no message broker, no Node toolchain.

---

## Verifying the build

The non-trivial logic ships with a runnable self-check - ring buffer wraparound,
operator overloading, the no-boxing claim, recursive validation under hostile depth, and
the attachment crypto:

```bash
dotnet run --project ST10439147_PROG7312_SmartX_POE -- --selftest
```

Expected output ends with `all checks passed` and exit code 0:

```
SmartX self-check
----------------------------------------------------------------
  ok  ring buffer: wraps, preserves order, indexes oldest-first
  ok  operators: +, -, unary -, ==, !=, <, >, <=, >= over float and int
  ok  generics: float/int/bool through one wrapper, 0 bytes allocated over 50 000 ingests
  ok  arrays: ragged float[][] staging, float[,] projection, flush into List<T>
  ok  recursion: valid four-tier tree accepted, breadcrumb path resolves
  ok  recursion: tier-skipping and duplicate sensor mounts rejected with paths
  ok  recursion: 50 000-tier payload stopped at depth 64, no stack overflow
  ok  recursion: cycle in the deployment graph detected and reported
  ok  serialisation: TelemetryPacket<float> and <bool> round-trip through JSON
  ok  categories: 10 agricultural sensor types, payload and unit fixed per type, unknown types rejected
  ok  crypto: AES-256-GCM round-trips at 0, 1, 1 KB, 64 KB and multi-chunk sizes
  ok  crypto: bit-flips, truncation and wrong keys are all rejected
  ok  uploads: magic-byte sniffing, text validation and path-traversal stripping
  ok  queue: normal packets leave in strict FIFO order
  ok  priority queue: criticals jump a backed-up lane, spikes first, ties in arrival order, a full lane sheds load
  ok  stack: commands undo last-in-first-out, a batch reverts as one, empty undo is safe
  ok  pump override: a commanded stop and restart raise nothing, an unexpected stop still alerts
  ok  dictionary: a device is found by id or by MAC in any spelling, misses are counted
  ok  sorted dictionary: out-of-order packets read back in time order, the log stays bounded
  ok  hash set: a silent node raises one disconnect alert, an open error state is not raised twice
  ok  prediction log: every search and command is logged with the alert that was open at the time
  ok  dictionary of counters: after-X-did-Y pairs are counted and the most frequent follow-up ranks first
  ok  suggestion: when a moisture alert recurs, the pump the operator keeps searching for is suggested
  ok  trend: a least-squares line flags a device climbing toward its threshold and when it will cross
----------------------------------------------------------------
all checks passed
```

That `0 bytes allocated over 50 000 ingests` line is measured with
`GC.GetAllocatedBytesForCurrentThread()`, not asserted in a comment - if anything on the
ingest path started boxing, this check fails.

---

## Testing Part 2

Everything below takes a few minutes. Each structure has one self-check line and one
manual step; the full walkthroughs are in the sections that follow.

**1. Self-check (about 30 seconds, no running app needed).** Run the command in
*Verifying the build*. It must end with `all checks passed` and exit code 0. These lines
are the Part 2 checks:

| Structure | Self-check line starts with | What it proves |
|---|---|---|
| `Queue<T>` | `queue:` | normal packets leave in arrival order (1,2,3,4,5) |
| `PriorityQueue<T,P>` | `priority queue:` | criticals leave first: 5,4,6,1,2,3,7 |
| `Stack<T>` | `stack:` | Undo reverts commands last-in-first-out |
| `SortedDictionary` | `sorted dictionary:` | packets sent 3,1,4,2 read back 1,2,3,4 |
| `HashSet<T>` | `hash set:` | one outage raises one alert, repeat sweeps are skipped |
| Suggestion ranking | `dictionary of counters:` and `suggestion:` | the most-repeated follow-up ranks first and is suggested |
| Persistence and insights | `persistence:` | first run is seeded with typical habits, counters survive a restart (`storage/predictions.json`), open faults are flagged in "Automated insights" |

**2. Simulate a high-priority alert (about 2 minutes).**

1. Start the app (see Quick start) and open <http://localhost:5036/dashboard>.
2. In the *Ingest queue* panel click **Flood the queue**. *Waiting (normal)* jumps to about 200.
3. While it counts down, click **Send critical packet**. The line under the counters shows
   how many older normal packets it overtook, and its row in *Processing order* is red.

Or, from a shell (Git Bash, macOS/Linux, Windows `cmd`; PowerShell users use the buttons):

```bash
curl -X POST "http://localhost:5036/api/queue/flood?count=200&delayMs=20"
```

```bash
curl -X POST http://localhost:5036/api/telemetry -H "Content-Type: application/json" -d "{\"sensorId\":\"A4:CF:12:9B:04:7E\",\"value\":95}"
```

```bash
curl http://localhost:5036/api/queue
```

In `recent`, the entry with `"lane": "critical"` has a `position` lower than the normal
packets that arrived before it.

**3. Filter the command stream (about 1 minute).** Open <http://localhost:5036/commands>.
In *Live feed*, pick a **Category** (for example Soil Moisture) and an **Alert state**
(Alerting, Silent or Normal), or type in **Search** (name, id or deployment path). The
filters combine, and the counter above the grid shows *n of N nodes*. Click **Unplug** on
the dashboard, then filter to *Silent* to find the disconnected node. Your filters stay
set when you leave the page and come back.

**4. The other structures.** Follow the matching section: Undo in *Stack: manual override
commands and Undo*, sorted order in *SortedDictionary: the sensor timeline*, set
deduplication in *HashSet: disconnected nodes and open error states*, and suggestion
ranking in *Suggest the pump after a moisture alert*.

---

## Using the console

1. **Gateway** (`/`) - landing page with the three architectural pillars. Pillars 1
   (ingestion) and 2 (command stream) are live; pillar 3 (mesh routing) is visibly locked
   until the final Portfolio of Evidence.
   The header shows a real measured round-trip to the API, refreshed every 10 seconds.
2. **Ingestion** (`/ingestion`) - register a node (MAC, display name, deployment path,
   sensor category, publish interval, threshold), attach a config file, hardware log or
   deployment photo, and validate the deployment tree. A node registered here is adopted
   by the simulator and starts publishing on the next cycle.
3. **Live dashboard** (`/dashboard`) - the severity-ranked alert rail, the small-multiple
   sensor grid, the drill-down inspector, and a panel that aggregates two pump meters
   through the overloaded `+`.
4. **Command stream** (`/commands`) - the live feed, category and alert-state filters,
   manual override commands with Undo, ingest queue depth, the sensor timeline and the
   suggested actions. Feed and filters survive navigation to the other pages.

### Sensor categories

Every node is a specific agricultural device - there is no generic "environmental" or
"float sensor" option. The category fixes the payload type and unit, so they cannot be
mismatched at registration (defined once in `SensorCategoryExtensions.Spec`):

| Category | Payload | Unit | Default warning threshold |
|---|---|---|---|
| Soil moisture | float | %VWC | 45 (waterlogged) |
| Soil pH | float | pH | 7.5 (alkaline lock-out) |
| Air temperature | float | °C | 32 (heat stress) |
| Relative humidity | float | %RH | 85 (fungal risk) |
| Irrigation flow | float | L/min | 30 (burst line) |
| Light intensity | int | lux | 55 000 (leaf scorch) |
| Water tank level | int | % | 95 (overflow) |
| Pump power draw | int | W | 3 000 (stalled pump) |
| Irrigation valve | bool | - | - |
| Greenhouse vent | bool | - | - |

### Things worth trying

- Register a sensor with MAC `nonsense` - the browser blocks it, and `curl`-ing the same
  payload straight at `POST /api/sensors` gets the same field-level rejection from the
  server. The client-side check is convenience; the server's is the trust boundary.
- Upload an `.exe` renamed to `.png`. Rejected on content, not extension.
- Click **Stress the depth guard** on the ingestion page. A 50 000-tier deployment tree
  goes at the recursive validator and comes back as an error at depth 64 instead of
  killing the process.
- Dismiss the same kind of alert three times on one node. It stops competing for the top
  of the rail and collapses into the auditable suppressed group - it is never discarded.

---

## Testing the queue and the stack

Start the app (see Quick start) and open the **Live dashboard** at
<http://localhost:5036/dashboard>. Both features have a panel there, and both can also be
driven with `curl`. The `curl` commands below are quoted for Git Bash, macOS/Linux shells
and Windows `cmd`; in PowerShell use the dashboard buttons instead.

### Queue and priority queue: simulate a high-priority alert

Every reading posted to `POST /api/telemetry` is placed in a `Queue<T>` and a background
worker ingests it in arrival order (FIFO). A reading that is a spike or breaches the
sensor's warning threshold goes into a `PriorityQueue<TElement,TPriority>` instead, and
the worker always empties that lane first.

**On the dashboard** (panel: *Ingest queue*):

1. Click **Flood the queue**. *Waiting (normal)* jumps to about 200 and counts down over
   roughly six seconds, because the flood also slows the worker so the backlog is visible.
2. While it is still counting down, click **Send critical packet**.
3. Read the line under the counters. It reports the critical packet's arrival number, the
   position it was processed at, and how many older normal packets it overtook. In the
   *Processing order* table the critical row is highlighted in red, with an *arrived*
   number far higher than the rows around it.

**With curl:**

```bash
curl -X POST "http://localhost:5036/api/queue/flood?count=200&delayMs=20"
```

```bash
curl -X POST http://localhost:5036/api/telemetry -H "Content-Type: application/json" -d "{\"sensorId\":\"A4:CF:12:9B:04:7E\",\"value\":95}"
```

```bash
curl http://localhost:5036/api/queue
```

The second command sends a soil-moisture reading of 95 from a probe that normally reads
about 31, which is a spike. In the `recent` array of the third response, find the entry
with `"lane": "critical"`: its `sequence` (arrival order) is higher than the entries
after it, while its `position` (processing order) is lower. For example:

```
position 1125  sequence 1125  normal
position 1126  sequence 1323  critical  Spike
position 1127  sequence 1126  normal
```

`depth` and `criticalDepth` in the same response are the number of packets waiting in
each lane. Keep floods short: `count` is capped at 2 000 and `delayMs` at 250, and a long
backlog delays live readings enough to raise false "silent node" alerts.

### Stack: manual override commands and Undo

A manual override pins an actuator (valve, vent or pump) on or off; a pump pinned off
reports 0 W until the command is undone. Each command is pushed onto
a `Stack<T>`; Undo pops the most recent one and puts every device it touched back the way
it was.

**On the dashboard** (panel: *Manual override*):

1. Click **On** next to *Drip Line Valve A*. It shows *pinned on* and, within a few
   seconds, reads OPEN.
2. Click **Shut all valves**. Both *Drip Line Valve A* and *Drip Line Valve B* show
   *pinned off* and read SHUT. The history on the right now lists two commands, the
   newest tagged *next undo*.
3. Click **Undo last command**. Each valve goes back to what it was before "shut all
   valves": A to *pinned on*, B to *auto*.
4. Click **Undo last command** again. Valve A returns to *auto*, the history is empty
   and the Undo button is disabled.

Pumps work the same way: **Off** next to *Borehole Pump* makes it read STOPPED (0 W), and
Undo starts it again. A stop the operator ordered is not reported as an anomaly.

**With curl:**

```bash
curl -X POST http://localhost:5036/api/commands -H "Content-Type: application/json" -d "{\"sensorId\":\"DC:A6:32:77:11:01\",\"state\":true}"
```

```bash
curl -X POST http://localhost:5036/api/commands -H "Content-Type: application/json" -d "{\"category\":\"IrrigationValve\",\"state\":false}"
```

```bash
curl http://localhost:5036/api/commands
```

```bash
curl -X POST http://localhost:5036/api/commands/undo
```

`GET /api/commands` returns `history` (newest first, the order Undo takes them in) and
`overrides` (which actuators are pinned right now); `GET /api/commands/history` returns
the history list alone. Each undo returns the command it
reverted; once the stack is empty it answers `409` with `"Nothing to undo."` rather than
failing. Commanding a sensor that is not an actuator returns `400`.

Both structures are also covered by the self-check (see Verifying the build): the `queue:`
line proves FIFO order, the `priority queue:` line proves that criticals jump the line,
and the `stack:` line proves last-in-first-out undo.

---

## Testing the lookup structures

Same setup as above: the app running and the **Live dashboard** open.

### Dictionary: the device registry

Every packet names the node that sent it, and the gateway has to find that node before it
can do anything else. The registry is a dictionary keyed by MAC address, so the lookup is
a single hash probe - O(1) on average - instead of a scan through every registered
device. It runs twice per packet: once when `POST /api/telemetry` accepts the reading and
once when the background worker ingests it.

The key is the MAC with colons and in upper case, which is also the `id` the gateway
returns on registration. `a4-cf-12-9b-04-7e`, `A4:CF:12:9B:04:7E` and the id all reach the
same entry.

**On the dashboard** (panel: *Device registry*):

1. Watch *Lookups* climb as the simulated mesh publishes. *Devices* stays at 12.
2. Type `a4-cf-12-9b-04-7e` (dashes, lower case) and click **Look up**. It finds
   *Soil Moisture North* under the id `A4:CF:12:9B:04:7E`.
3. Type `00:00:00:00:00:00` and click **Look up**. Nothing is found and *Misses* goes up
   by one.

**With curl:**

```bash
curl http://localhost:5036/api/registry
```

```bash
curl http://localhost:5036/api/sensors/a4-cf-12-9b-04-7e
```

```bash
curl -X POST http://localhost:5036/api/telemetry -H "Content-Type: application/json" -d "{\"sensorId\":\"a4-cf-12-9b-04-7e\",\"value\":31}"
```

The first returns `devices`, `lookups`, `misses` and the registry `keys`. The second finds
a device from a MAC typed with dashes. The third is a packet arriving with the MAC spelled
that way: it is accepted with `202`, where an unregistered MAC gets `404`.

The self-check's `dictionary:` line covers the same lookups.

### SortedDictionary: the sensor timeline

A mesh relays packets over several hops, so they do not always reach the gateway in the
order the nodes sent them. A reading may carry the node's own `timestamp`; if it does not,
the gateway stamps it on arrival. Each sensor's log is a
`SortedDictionary<DateTimeOffset, TimelinePoint>` keyed by that timestamp, so a late
packet is inserted where it belongs in time (O(log n)) and the log reads back already in
order. Nothing is re-sorted when the graph asks for it. The log keeps the newest 240
readings per sensor.

**On the dashboard** (panel: *Sensor timeline*):

1. Pick a sensor. The graph plots its last 60 readings against the time each was taken.
2. Click **Send 8 packets out of order**. The gateway receives eight backdated readings
   for that sensor in shuffled order.
3. The line under the graph reports 8 late readings and the graph marks each with a dashed
   line. In the table the late rows are highlighted: *taken at* still runs in order down
   the whole table, while their *arrived* numbers are higher than the rows above them and
   out of sequence with each other.

**With curl:**

```bash
curl -X POST "http://localhost:5036/api/sensors/A4:CF:12:9B:04:7E/timeline/scramble?count=5"
```

```bash
curl "http://localhost:5036/api/sensors/A4:CF:12:9B:04:7E/timeline?samples=12"
```

The first response lists the timestamps in the order they were sent. In the second, `at`
ascends from the first entry to the last, while `sequence` (arrival order) does not:

```
at 12:31:02.37  sequence 1074
at 12:31:03.37  sequence 1089   late
at 12:31:04.37  sequence 1084
at 12:31:05.37  sequence 1088   late
at 12:31:06.37  sequence 1099
```

To send a single late packet yourself, add a `timestamp` to a normal reading:

```bash
curl -X POST http://localhost:5036/api/telemetry -H "Content-Type: application/json" -d "{\"sensorId\":\"A4:CF:12:9B:04:7E\",\"value\":31,\"timestamp\":\"2026-01-01T08:00:00Z\"}"
```

A timestamp more than five seconds in the future is rejected with `400`. Two readings
from one sensor with an identical timestamp keep only the one that arrived last.

The self-check's `sorted dictionary:` line feeds readings in the order 3, 1, 4, 2 and
asserts they read back 1, 2, 3, 4.

### HashSet: disconnected nodes and open error states

A node counts as disconnected once it has missed three publish intervals. The gateway
checks for that every five seconds, and a node that stays silent fails the check every
time - without a memory of what it has already reported, each failure would be another
alert. The anomaly engine keeps two `HashSet<string>`s:

- **disconnected nodes**, by sensor id. A node is added when it is first found silent and
  removed when it is heard from again.
- **open error states**, as `sensorId|Kind`. A spike or threshold breach is added when it
  starts and removed when the reading returns to normal.

`HashSet<T>.Add` returns `false` if the entry is already there. That return value is the
duplicate test: `true` means a new fault, so the alert is raised; `false` means it was
raised when the entry went in, so it is skipped and counted. A long outage keeps its one
alert, which is raised to *Critical* in place after nine missed intervals.

**On the dashboard** (panel: *Disconnect set*):

1. Pick a node and click **Unplug for 60 s**.
2. Within about ten seconds it appears under *In the disconnect set* and one *Silence*
   alert appears on the alert rail.
3. Keep watching for the rest of the minute. *Duplicate alerts skipped* climbs every five
   seconds, the rail still shows one *Silence* alert for that node, and that alert turns
   from *Warning* to *Critical*.
4. When the minute is up (or after **Plug back in**) the node leaves the set.

**With curl:**

```bash
curl -X POST "http://localhost:5036/api/sensors/A4:CF:12:9B:04:7E/unplug?seconds=60"
```

```bash
curl http://localhost:5036/api/faults
```

```bash
curl http://localhost:5036/api/alerts
```

Run the second and third a few times over the minute. `disconnected` lists the node and
`duplicatesSkipped` keeps rising, while `/api/alerts` holds a single entry with
`"kind": "Silence"` for it. The simulator also drops nodes at random, so other ids can
show up in the set on their own.

The self-check's `hash set:` line sweeps a silent node six times and asserts exactly one
disconnect alert, then holds a reading over its threshold and asserts it is raised once.

---

## Testing the predictive action engine

The console learns from what the operator actually does. Every search and every override
command is logged with the faults that were open at that moment, and the gateway counts
"after event X, the operator did Y" pairs. When X happens again, the follow-ups the
operator has made a habit of are on screen before they type anything. Separately, devices
still under their threshold but climbing toward it are flagged with an estimate of when
they will cross.

### How it learns

`PredictionService` keeps a dictionary of counters,
`Dictionary<string, Dictionary<string, int>>`:

- **Event X** is an open fault, written `Category|AlertKind` - for example
  `SoilMoisture|Silence` or `SoilMoisture|ThresholdBreach`. It is keyed by category, not
  by device, so an outage on either moisture probe teaches the same lesson.
- **Follow-up Y** is what the operator did: `Search:pump` or
  `Command:Switch Borehole Pump on`. Search terms are trimmed and lower-cased, so `Pump `
  and `pump` are one follow-up.

Recording an action is two hash lookups and an increment. Ranking an event's follow-ups
sorts only that event's row - the handful of things operators do in response to it - most
frequent first, ties broken by name so the order is stable.

The context is the set of faults *still open*, read from the disconnect set and the open
error states described above, not "the newest alert". On a busy mesh a one-reading spike
elsewhere lands seconds after the moisture alert and closes again at once, while the
moisture problem the operator is actually reacting to is still open. An action taken with
several faults open counts once against each. Kinds the operator has suppressed are left
out - they have said those are noise - and an action with nothing open forms no pair.

When an alert is raised, the gateway looks up its event and pushes a suggestion over
SignalR (`suggestionReady`) holding every follow-up seen at least **twice** after it. One
search could be coincidence; two is the start of a habit. A suggestion stays up for two
minutes after its alert, and `GET /api/predictions/suggestion` returns it to a dashboard
that opens mid-alert.

### Suggest the pump after a moisture alert

This is the scenario the feature is built around: repeat a pattern a few times, and the
next time the event happens the console suggests the follow-up on its own.

**On the dashboard** (panel: *Search*, top of the page; *Disconnect set* lower down):

1. In *Disconnect set*, pick **Soil Moisture South** and click **Unplug for 60 s**. Wait
   for its *Silence* alert to appear on the rail (about ten seconds).
2. In *Search*, type `pump` and press Enter.
3. Click **Plug back in** and wait a few seconds for the node to leave the set.
4. Repeat steps 1-3 once more. The pattern has now been seen twice.
5. Unplug **Soil Moisture South** a third time and do **not** search. When the *Silence*
   alert arrives, a **Suggested next** strip appears in the *Search* panel:
   *Soil moisture silence on Soil Moisture South. After this alert you have usually:*
   **Search "pump" ×2**.
6. Click the chip. It runs the search, and that search is logged like any other, so the
   count goes up to 3. Taking a suggestion reinforces it.

**With curl:**

```bash
curl -X POST "http://localhost:5036/api/sensors/A4:CF:12:9B:04:7F/unplug?seconds=60"
```

Wait about ten seconds for the *Silence* alert, then search:

```bash
curl "http://localhost:5036/api/search?q=pump"
```

Plug the node back in:

```bash
curl -X POST "http://localhost:5036/api/sensors/A4:CF:12:9B:04:7F/unplug?seconds=0"
```

Run those three once more, then see what has been learned:

```bash
curl http://localhost:5036/api/predictions/pairs
```

```json
{"SoilMoisture|Silence":[{"followUp":"Search:pump","count":2}]}
```

Unplug the node a third time, wait for the alert, and read the suggestion:

```bash
curl http://localhost:5036/api/predictions/suggestion
```

The response has `"context": "SoilMoisture|Silence"` and `Search:pump` in `followUps`.
`GET /api/predictions/log` shows each action with the `contexts` it was counted against,
and `GET /api/predictions/rank?context=SoilMoisture%7CSilence` returns one event's ranking
(`%7C` is the `|`).

Search suggestions are clickable. Command suggestions are shown as labels to issue from
*Manual override*, because a label alone does not say which devices the command targeted.

### Trend flags: devices heading for their threshold

A threshold alert fires once a reading is already over the line. A trend flag fires
before that, while there is still time to act. For each device that reports a level
(valves and vents report a state, so they are skipped), the gateway fits a least-squares
line through its last 30 readings. The slope is the rate of climb; where the line meets
the warning threshold is when the device will cross if nothing changes. A device is
flagged when all of these hold:

- the line is rising and the device is still under its threshold - over it, it is a
  breach, not a trend;
- the line fits the readings well (R² of at least 0.6), so noise wandering about a flat
  baseline is not mistaken for a climb;
- the projected crossing is less than three minutes away.

Silent nodes are skipped because their readings are stale, and a pump's 0 W readings
(time spent stopped) are left out, the same rule the anomaly baseline uses. The 30
samples, the three-minute horizon and the 0.6 fit are constants at the top of
`PredictionService`, so they can be tuned without touching the maths.

The simulated mesh never climbs on its own, so a test trigger makes one node drift
upward on top of its normal signal.

**On the dashboard:** start the drift with the curl command below. Within about half a
minute a **Trending** strip appears in the *Search* panel, for example
*Soil Moisture North: 38.83 → 45%VWC in ~36s*, and the countdown falls as the reading
climbs. Click it to open the node in the inspector.

**With curl:**

```bash
curl -X POST "http://localhost:5036/api/sensors/A4:CF:12:9B:04:7E/drift"
```

Left without `perSecond`, the rate is set so the node reaches its threshold in about 90
seconds. Watch the projection:

```bash
curl http://localhost:5036/api/predictions/trends
```

Stop the climb and put the node back where it started:

```bash
curl -X POST "http://localhost:5036/api/sensors/A4:CF:12:9B:04:7E/drift?perSecond=0"
```

A steep ramp is also a large deviation from the node's own history, so the spike detector
may raise a *Spike* on it partway through the climb. That is expected.

The self-check's last four lines cover the engine: actions logged with the faults open at
the time, the most frequent follow-up ranking first, the pump suggested once the moisture
pattern repeats (and nothing suggested after a single search, for a different event or
for a suppressed alert), and a trend flagged only for a steady climb close to its
threshold, at the crossing time the line predicts.

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
│   │                                    mesh simulator, attachments
│   └── Services/IngestQueue.cs, IngestWorker.cs   Queue<T> + PriorityQueue lanes, worker
│       Services/CommandService.cs                 Stack<T> override history, Undo
│       Services/PredictionService.cs              pair counts, trend flags, suggestions
└── SmartX.Client                      ← Blazor WebAssembly console
```

`SmartX.Shared` being referenced by both projects is the load-bearing decision:
`TelemetryPacket<T>` is the same type in the gateway and in the browser, so there is no
DTO translation layer and no chance of the two sides drifting.

### Technical requirements, and where they live

| Requirement | Implementation |
|---|---|
| **Generics** | `TelemetryPacket<T> where T : struct` carries float, int and bool through one wrapper. `TelemetryStore` keeps a *typed bucket per payload kind* rather than a shared `List<object>` - that is what keeps the ingest path off the boxing path, and the self-check measures it. |
| **Operator overloading** | `SensorReading<T> where T : struct, INumber<T>` defines `+` (aggregate two meters), `-` (delta), unary `-`, `==`/`!=`, and the four relational operators both against another reading and against a bare `T`, so rule code reads `reading > limit`. |
| **Arrays** | Ragged poll cycles stage in `float[][]` (each cycle really does return a different sample count), project to a rectangular, NaN-padded `float[,]`, then flush into `List<TelemetryPacket<float>>`. `GET /api/sensors/{id}/archive` shows the jagged rows beside the projection's shape. |
| **Recursion** | `TopologyValidator` walks Facility → Zone → Sub-Zone → Node, reporting issues by breadcrumb path, with a depth ceiling and a cycle guard so a malformed tree is an error rather than a `StackOverflowException`. |
| **Collections** | `CircularTelemetryBuffer<T>` - fixed-capacity, array-backed, `IEnumerable<TelemetryPacket<T>>`. An unbounded stream into a `List<T>` grows until the gateway dies; the ring bounds memory *and* provides exactly the window the anomaly detector needs for its rolling baseline. |
| **Dictionary** | `TelemetryStore` keeps the device registry in a `ConcurrentDictionary<string, SensorRegistration>` keyed by canonical MAC. `TryGet` is the lookup every packet arrival goes through, by MAC or id, and it counts lookups and misses for `GET /api/registry`. The concurrent variant is used because request threads and the ingest worker read it at the same time. |
| **SortedDictionary** | `TelemetryStore` keeps a `SortedDictionary<DateTimeOffset, TimelinePoint>` per sensor, keyed by the time the reading was taken. `LogReading` inserts wherever the timestamp falls, so packets that arrive out of order are still served in time order by `GET /api/sensors/{id}/timeline` and drawn that way by the dashboard's timeline graph. |
| **HashSet** | `AnomalyEngine` keeps a `HashSet<string>` of disconnected nodes and another of open error states. `Add` returning `false` means the fault is already known, so the repeat alert is skipped: a node that stays silent raises one disconnect alert, not one per sweep. `GET /api/faults` shows both sets and the count of duplicates skipped. |
| **Predictive actions** | `PredictionService` logs every search and command with the faults open at that moment, and counts "after event X, the operator did Y" pairs in a `Dictionary<string, Dictionary<string, int>>`. When X recurs, the follow-ups seen at least twice are pushed to the dashboard before the operator searches. A least-squares line over each device's recent readings flags the ones projected to cross their threshold within three minutes. |
| **File upload** | `MultipartReader` streaming (never `IFormFile` buffering), magic-byte content sniffing, path-traversal stripping, a 25 MB ceiling enforced mid-flight, and AES-256-GCM at rest with per-chunk nonces and authenticated chunk indices. |
| **Engagement** | The strategy chosen in Part 1 Task 1: anomaly-first visual encoding, severity-ranked rail, z-score spike detection against a rolling baseline, a *separate* silence timer for disconnects, and suppression-not-deletion of repeatedly dismissed alerts. |

---

## API reference

Base path `/api`. Enums travel as names.

| Method | Route | Purpose |
|---|---|---|
| `GET` | `/health` | Gateway status: uptime, node count, throughput, alert counts |
| `GET` | `/sensors` | All sensor snapshots (value, baseline, sparkline, silence state) |
| `GET` | `/sensors/{id}` | One snapshot. `{id}` is the id or the MAC in any spelling, here and on every other sensor route |
| `GET` | `/registry` | Device registry: device count, lookups, misses, keys |
| `GET` | `/search?q=` | Devices whose name, id, deployment path or category contains `q`. Every search is logged for the predictive engine |
| `GET` | `/sensors/{id}/profile` | The stored registration |
| `POST` | `/sensors` | Register a node - 400 with field-level errors on invalid input |
| `DELETE` | `/sensors/{id}` | Deregister |
| `GET` | `/sensors/{id}/history?samples=n` | Typed `TelemetryPacket<T>` history straight off the ring |
| `GET` | `/sensors/{id}/archive` | Float sensors only: jagged staging rows, the shape of their rectangular projection, the newest flushed packets |
| `GET` | `/sensors/{id}/timeline?samples=n` | The sensor's log in timestamp order, each point with its arrival `sequence` |
| `POST` | `/sensors/{id}/timeline/scramble?count=` | Test trigger: receive backdated readings for the sensor in shuffled order |
| `POST` | `/telemetry` | Ingest one reading `{ sensorId, value, timestamp? }` - queued, then ingested by the background worker |
| `GET` | `/queue` | Queue depth per lane and the recent processing order |
| `POST` | `/queue/flood?count=&delayMs=` | Test trigger: back the normal lane up and slow the worker |
| `GET` | `/commands` | Override command history (newest first) and current overrides |
| `POST` | `/commands` | Pin one actuator `{ sensorId, state }` or a whole kind `{ category, state }` |
| `POST` | `/commands/undo` | Revert the most recent command - 409 when there is none |
| `GET` | `/predictions/log` | Every search and command (newest first) with the faults it was counted against |
| `GET` | `/predictions/pairs` | The dictionary of counters: every event seen, each with its follow-ups ranked |
| `GET` | `/predictions/rank?context=&top=` | The top follow-ups to one event, e.g. `context=SoilMoisture%7CSilence` |
| `GET` | `/predictions/suggestion` | The suggestion for the alert driving the operator now - 204 when there is none |
| `GET` | `/predictions/trends` | Devices under their threshold but projected to cross it within three minutes, soonest first |
| `POST` | `/sensors/{id}/drift?perSecond=` | Test trigger: the simulated node's reading climbs; omit the rate to reach the threshold in ~90 s, `perSecond=0` to stop |
| `GET` | `/telemetry/aggregate?a=&b=` | Operator overloading over HTTP: sum, delta, comparison |
| `GET` | `/alerts?includeSuppressed=` | Severity-ranked alert rail |
| `POST` | `/alerts/{id}/dismiss` | Dismiss, and count toward suppression of that kind |
| `POST` | `/alerts/restore` | Un-suppress a kind for a node |
| `GET` | `/faults` | The disconnect set, the open error states and the number of duplicate alerts skipped |
| `POST` | `/sensors/{id}/unplug?seconds=` | Test trigger: the simulated node stops publishing; `seconds=0` plugs it back in |
| `GET` | `/topology` | The seeded deployment tree |
| `POST` | `/topology/validate` | Recursive validation of a supplied tree |
| `GET` | `/topology/stress?depth=` | Depth-guard demonstration |
| `POST` | `/sensors/{id}/attachments` | Streamed multipart upload |
| `GET` | `/sensors/{id}/attachments` | Attachment metadata for a node |
| `GET` | `/attachments/{attachmentId}` | Decrypt and download |

Real-time channel: SignalR hub at `/hubs/telemetry`, pushing `tick`, `alertRaised`,
`alertDismissed`, `sensorsChanged` and `suggestionReady`.

Quick check with the server running:

```bash
curl http://localhost:5036/api/health
```

---

## Configuration

Client - `SmartX.Client/wwwroot/appsettings.json`, fetched at startup, so a published
client can be repointed without a rebuild:

```json
{ "Gateway": { "BaseUrl": "" } }
```

Blank means "the origin that served this page", which is how the hosted setup runs. Set an
absolute URL to point the dashboard at a gateway on another origin; the API ships without
CORS, so add a policy allowing the dashboard's origin first.

Server - `ST10439147_PROG7312_SmartX_POE/appsettings.json`, or environment variables using
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

Telemetry is **simulated** - `MeshSimulator` stands in for the physical devices, injecting
value spikes and node disconnects so the alerting path has something real to catch. It
reaches the gateway the way a device would: every reading is an HTTP `POST /api/telemetry`,
which enters the ingest queue and is processed by the background worker. A real ESP32 would
post to the same endpoint and nothing downstream changes.

Gateway state is **in memory** and resets on restart: registered nodes, history and alerts
are lost, and the 13 seeded nodes come back. Uploaded files stay on disk, encrypted, but
their metadata is in memory too, so after a restart the API no longer lists or serves
them. What the predictive engine has learned is in memory as well, so its counters start
empty after a restart and the moisture-then-pump pattern has to be taught again.
Pillar 3 (Network Topology and Mesh Routing) and persistence are out of scope for this
part and arrive in the final PoE.

# References
- [1] Microsoft. *Generics in C#*. Microsoft Learn. https://learn.microsoft.com/dotnet/csharp/fundamentals/types/generics
- [2] Microsoft. *How to serialize and deserialize JSON using System.Text.Json*. Microsoft Learn. https://learn.microsoft.com/dotnet/standard/serialization/system-text-json/how-to
- [3] Microsoft. *Generic math (System.Numerics.INumber<T>)*. Microsoft Learn. https://learn.microsoft.com/dotnet/standard/generics/math
- [4] Microsoft. *ASP.NET Core Blazor hosting models*. Microsoft Learn. https://learn.microsoft.com/aspnet/core/blazor/hosting-models
- [5] Microsoft. *Minimal APIs overview*. Microsoft Learn. https://learn.microsoft.com/aspnet/core/fundamentals/minimal-apis/overview
- [6] Microsoft. *HttpClient.Timeout Property*. Microsoft Learn. https://learn.microsoft.com/dotnet/api/system.net.http.httpclient.timeout
- [7] Microsoft. *Response compression in ASP.NET Core*. Microsoft Learn. https://learn.microsoft.com/aspnet/core/performance/response-compression
- [8] Microsoft. *Operator overloading — predefined unary, arithmetic and comparison operators*. C# language reference, Microsoft Learn. https://learn.microsoft.com/dotnet/csharp/language-reference/operators/operator-overloading
- [9] Microsoft. *EqualityComparer<T>.Default Property*. Microsoft Learn. https://learn.microsoft.com/dotnet/api/system.collections.generic.equalitycomparer-1.default
- [10] Microsoft. *Structure types — readonly struct and in parameters*. C# language reference, Microsoft Learn. https://learn.microsoft.com/dotnet/csharp/language-reference/builtin-types/struct
- [11] Microsoft. *How to define value equality for a type*. Microsoft Learn. https://learn.microsoft.com/dotnet/csharp/programming-guide/statements-expressions-operators/how-to-define-value-equality-for-a-type
- [12] Wikipedia. *Circular buffer*. https://en.wikipedia.org/wiki/Circular_buffer
- [13] Microsoft. *Jagged arrays (C# programming guide)*. Microsoft Learn. https://learn.microsoft.com/dotnet/csharp/programming-guide/arrays/jagged-arrays
- [14] Microsoft. *Multidimensional arrays (C# programming guide)*. Microsoft Learn. https://learn.microsoft.com/dotnet/csharp/language-reference/builtin-types/arrays
- [15] Microsoft. *Span<T> Struct*. Microsoft Learn. https://learn.microsoft.com/dotnet/api/system.span-1
- [16] Microsoft. *StackOverflowException Class* (cannot be caught by a try/catch). Microsoft Learn. https://learn.microsoft.com/dotnet/api/system.stackoverflowexception
- [17] Microsoft. *Handle errors in ASP.NET Core — problem details / ValidationProblem*. Microsoft Learn. https://learn.microsoft.com/aspnet/core/fundamentals/minimal-apis/handle-errors
- [18] Microsoft. *ConcurrentDictionary<TKey,TValue> Class*. Microsoft Learn. https://learn.microsoft.com/dotnet/api/system.collections.concurrent.concurrentdictionary-2
- [19] Wikipedia. *Standard score (z-score)*. https://en.wikipedia.org/wiki/Standard_score
- [20] Wikipedia. *68–95–99.7 rule (three-sigma rule of thumb)*. https://en.wikipedia.org/wiki/68%E2%80%9395%E2%80%9399.7_rule
- [21] Wikipedia. *Alarm fatigue*. https://en.wikipedia.org/wiki/Alarm_fatigue
- [22] Microsoft. *Introduction to ASP.NET Core SignalR*. Microsoft Learn. https://learn.microsoft.com/aspnet/core/signalr/introduction
- [23] Microsoft. *Background tasks with hosted services in ASP.NET Core*. Microsoft Learn. https://learn.microsoft.com/aspnet/core/fundamentals/host/hosted-services
- [24] Microsoft. *Upload files in ASP.NET Core — uploading large files with streaming*. Microsoft Learn. https://learn.microsoft.com/aspnet/core/mvc/models/file-uploads
- [25] Microsoft. *Configure options for the ASP.NET Core Kestrel web server — MaxRequestBodySize*. Microsoft Learn. https://learn.microsoft.com/aspnet/core/fundamentals/servers/kestrel/options
- [26] OWASP. *File Upload Cheat Sheet*. OWASP Cheat Sheet Series. https://cheatsheetseries.owasp.org/cheatsheets/File_Upload_Cheat_Sheet.html
- [27] OWASP. *Path Traversal*. https://owasp.org/www-community/attacks/Path_Traversal
- [28] Microsoft. *AesGcm Class*. Microsoft Learn. https://learn.microsoft.com/dotnet/api/system.security.cryptography.aesgcm
- [29] Dworkin, M. (2007). *NIST Special Publication 800-38D: Recommendation for Block Cipher Modes of Operation: Galois/Counter Mode (GCM) and GMAC*. NIST. https://doi.org/10.6028/NIST.SP.800-38D
- [30] McGrew, D. (2008). *RFC 5116: An Interface and Algorithms for Authenticated Encryption*. IETF. https://www.rfc-editor.org/rfc/rfc5116
- [31] Microsoft. *ASP.NET Core Blazor forms validation*. Microsoft Learn. https://learn.microsoft.com/aspnet/core/blazor/forms/validation
- [32] Tufte, E. R. (1990). *Envisioning Information*. Cheshire, CT: Graphics Press.
- [33] Nielsen, J. (1994, updated 2024). *10 Usability Heuristics for User Interface Design — Visibility of system status*. Nielsen Norman Group. https://www.nngroup.com/articles/ten-usability-heuristics/
- [34] Docker. *Multi-stage builds*. Docker Documentation. https://docs.docker.com/build/building/multi-stage/
- [35] The Independent Institute of Education (2026). *PROG7312 Portfolio of Evidence Guide, Part 1*. The IIE.
- [36] Microsoft. *Dictionary<TKey,TValue> Class*. Microsoft Learn. https://learn.microsoft.com/dotnet/api/system.collections.generic.dictionary-2
- [37] Wikipedia. *Simple linear regression*. https://en.wikipedia.org/wiki/Simple_linear_regression
- [38] Wikipedia. *Coefficient of determination*. https://en.wikipedia.org/wiki/Coefficient_of_determination
- [39] Microsoft. *Lock Class (System.Threading)*. Microsoft Learn. https://learn.microsoft.com/dotnet/api/system.threading.lock
- [40] Microsoft. *SemaphoreSlim Class*. Microsoft Learn. https://learn.microsoft.com/dotnet/api/system.threading.semaphoreslim
- [41] Microsoft. *PeriodicTimer Class*. Microsoft Learn. https://learn.microsoft.com/dotnet/api/system.threading.periodictimer
- [42] Microsoft. *IncrementalHash Class*. Microsoft Learn. https://learn.microsoft.com/dotnet/api/system.security.cryptography.incrementalhash
- [43] Microsoft. *BinaryPrimitives Class*. Microsoft Learn. https://learn.microsoft.com/dotnet/api/system.buffers.binary.binaryprimitives
- [44] Microsoft. *Interlocked Class*. Microsoft Learn. https://learn.microsoft.com/dotnet/api/system.threading.interlocked
- [45] Microsoft. *ASP.NET Core Blazor file uploads (InputFile)*. Microsoft Learn. https://learn.microsoft.com/aspnet/core/blazor/file-uploads
- [46] Microsoft. *UTF8Encoding Constructor (throwOnInvalidBytes)*. Microsoft Learn. https://learn.microsoft.com/dotnet/api/system.text.utf8encoding.-ctor
- [47] Microsoft. *ASP.NET Core SignalR .NET client - automatically reconnect*. Microsoft Learn. https://learn.microsoft.com/aspnet/core/signalr/dotnet-client#automatically-reconnect
- [48] Microsoft. *Random.Shuffle Method*. Microsoft Learn. https://learn.microsoft.com/dotnet/api/system.random.shuffle
- [49] Microsoft. *Validator.TryValidateObject Method*. Microsoft Learn. https://learn.microsoft.com/dotnet/api/system.componentmodel.dataannotations.validator.tryvalidateobject

