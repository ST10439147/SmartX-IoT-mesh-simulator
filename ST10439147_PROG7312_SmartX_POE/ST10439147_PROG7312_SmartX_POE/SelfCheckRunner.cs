using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using SmartX.Api.Security;
using SmartX.Api.Services;
using SmartX.Shared.Diagnostics;
using SmartX.Shared.Telemetry;

namespace SmartX.Api;

/// <summary>
/// The one runnable check. Runs the shared data-structure suite plus the crypto
/// round-trip that only exists on the server side.
///
/// <code>dotnet run --project src/SmartX.Api -- --selftest</code>
/// </summary>
internal static class SelfCheckRunner
{
    public static int Run()
    {
        Console.WriteLine("SmartX self-check");
        Console.WriteLine(new string('-', 64));

        try
        {
            foreach (var line in SelfCheck.Run())
                Console.WriteLine($"  ok  {line}");

            foreach (var line in RunCryptoChecks())
                Console.WriteLine($"  ok  {line}");

            foreach (var line in RunQueueChecks())
                Console.WriteLine($"  ok  {line}");

            foreach (var line in RunCommandChecks())
                Console.WriteLine($"  ok  {line}");

            foreach (var line in RunRegistryChecks())
                Console.WriteLine($"  ok  {line}");

            foreach (var line in RunTimelineChecks())
                Console.WriteLine($"  ok  {line}");

            foreach (var line in RunDisconnectChecks())
                Console.WriteLine($"  ok  {line}");

            foreach (var line in RunPredictionLogChecks())
                Console.WriteLine($"  ok  {line}");

            foreach (var line in RunPersistenceChecks())
                Console.WriteLine($"  ok  {line}");

            foreach (var line in RunTrendChecks())
                Console.WriteLine($"  ok  {line}");
        }
        catch (Exception ex)
        {
            Console.WriteLine();
            Console.Error.WriteLine($"  FAILED  {ex.Message}");
            return 1;
        }

        Console.WriteLine(new string('-', 64));
        Console.WriteLine("all checks passed");
        return 0;
    }
    //----------------------------------------------------------------------------------------------------------------------//
    // Reference [26]: OWASP. File Upload Cheat Sheet. OWASP Cheat Sheet Series.
    //   https://cheatsheetseries.owasp.org/cheatsheets/File_Upload_Cheat_Sheet.html
    // Reference [27]: OWASP. Path Traversal.
    //   https://owasp.org/www-community/attacks/Path_Traversal
    // Reference [28]: Microsoft. AesGcm Class. Microsoft Learn.
    //   https://learn.microsoft.com/dotnet/api/system.security.cryptography.aesgcm
    private static IEnumerable<string> RunCryptoChecks()
    {
        var log = new List<string>();
        var key = RandomNumberGenerator.GetBytes(ChunkedAesGcm.KeySize);

        // Sizes chosen to straddle the chunk boundary: empty, tiny, exactly one chunk,
        // and one chunk plus a byte. Off-by-one framing bugs live at exactly these sizes.
        foreach (var size in new[] { 0, 1, 1024, ChunkedAesGcm.ChunkSize, ChunkedAesGcm.ChunkSize + 1, ChunkedAesGcm.ChunkSize * 2 })
        {
            var plaintext = RandomNumberGenerator.GetBytes(size);

            using var encrypted = new MemoryStream();
            var written = ChunkedAesGcm.EncryptAsync(new MemoryStream(plaintext), encrypted, key).GetAwaiter().GetResult();
            Assert(written == size, $"encrypt should consume all {size} plaintext bytes, reported {written}");

            encrypted.Position = 0;
            using var decrypted = new MemoryStream();
            ChunkedAesGcm.DecryptAsync(encrypted, decrypted, key).GetAwaiter().GetResult();

            Assert(decrypted.ToArray().SequenceEqual(plaintext), $"round trip failed at {size} bytes");
            Assert(encrypted.Length > size, "ciphertext should carry its header and tags");
        }

        log.Add($"crypto: AES-256-GCM round-trips at 0, 1, 1 KB, {ChunkedAesGcm.ChunkSize / 1024} KB and multi-chunk sizes");

        // Tampering must be caught, not silently decrypted into garbage.
        var payload = Encoding.UTF8.GetBytes("sensor=A4:CF:12:9B:04:7E; calibration=1.042");
        using (var sealed1 = new MemoryStream())
        {
            ChunkedAesGcm.EncryptAsync(new MemoryStream(payload), sealed1, key).GetAwaiter().GetResult();

            var bytes = sealed1.ToArray();
            bytes[^1] ^= 0xFF;   // flip a bit in the ciphertext

            Assert(Throws(() => ChunkedAesGcm.DecryptAsync(new MemoryStream(bytes), Stream.Null, key).GetAwaiter().GetResult()),
                "a flipped ciphertext bit must fail authentication");
        }

        using (var sealed2 = new MemoryStream())
        {
            ChunkedAesGcm.EncryptAsync(new MemoryStream(payload), sealed2, key).GetAwaiter().GetResult();

            var truncated = sealed2.ToArray()[..^8];
            Assert(Throws(() => ChunkedAesGcm.DecryptAsync(new MemoryStream(truncated), Stream.Null, key).GetAwaiter().GetResult()),
                "a truncated attachment must be rejected");
        }

        using (var sealed3 = new MemoryStream())
        {
            ChunkedAesGcm.EncryptAsync(new MemoryStream(payload), sealed3, key).GetAwaiter().GetResult();
            sealed3.Position = 0;

            var wrongKey = RandomNumberGenerator.GetBytes(ChunkedAesGcm.KeySize);
            Assert(Throws(() => ChunkedAesGcm.DecryptAsync(sealed3, Stream.Null, wrongKey).GetAwaiter().GetResult()),
                "the wrong key must not decrypt an attachment");
        }

        log.Add("crypto: bit-flips, truncation and wrong keys are all rejected");

        // Content sniffing: the extension is a claim, the bytes are the evidence.
        var png = new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0, 1, 2 };
        var mzExe = "MZ\0"u8.ToArray();

        Assert(FileSignature.Inspect("deployment.png", png).Allowed, "a real PNG should be accepted");
        Assert(!FileSignature.Inspect("payload.png", mzExe).Allowed, "an executable renamed to .png must be rejected");
        Assert(!FileSignature.Inspect("payload.exe", mzExe).Allowed, "executables are not an accepted type at all");
        Assert(FileSignature.Inspect("gateway.log", "boot ok\nmesh joined\n"u8).Allowed, "a plain text log should be accepted");
        Assert(!FileSignature.Inspect("gateway.log", png).Allowed, "binary content in a .log must be rejected");
        Assert(FileSignature.SanitiseFileName("../../appsettings.json") == "appsettings.json",
            "path traversal must be stripped from the stored filename");

        log.Add("uploads: magic-byte sniffing, text validation and path-traversal stripping");

        return log;
    }
    //----------------------------------------------------------------------------------------------------------------------//
    private static IEnumerable<string> RunQueueChecks()
    {
        var log = new List<string>();
        var queue = new IngestQueue();

        for (var i = 1; i <= 5; i++) queue.TryEnqueue("meter-1", i);
        Assert(queue.Depth == 5, "five packets should be waiting");

        var order = new List<double>();
        while (queue.Depth > 0) order.Add(queue.DequeueAsync().AsTask().GetAwaiter().GetResult().Value);
        Assert(order.SequenceEqual([1, 2, 3, 4, 5]), $"expected FIFO 1..5 but got {string.Join(",", order)}");

        // Backed-up normal lane, then criticals arriving late: they must leave first,
        // spikes ahead of threshold breaches, and equal ranks in arrival order.
        for (var i = 1; i <= 3; i++) queue.TryEnqueue("meter-1", i);
        queue.TryEnqueue("meter-1", 4, AlertKind.ThresholdBreach);
        queue.TryEnqueue("meter-1", 5, AlertKind.Spike);
        queue.TryEnqueue("meter-1", 6, AlertKind.ThresholdBreach);
        queue.TryEnqueue("meter-1", 7);
        Assert(queue.Depth == 4 && queue.CriticalDepth == 3, "criticals wait in their own lane");

        order.Clear();
        while (queue.Depth + queue.CriticalDepth > 0) order.Add(queue.DequeueAsync().AsTask().GetAwaiter().GetResult().Value);
        Assert(order.SequenceEqual([5, 4, 6, 1, 2, 3, 7]), $"expected 5,4,6,1,2,3,7 but got {string.Join(",", order)}");

        // The processing trail keeps the order the worker reported, and stays bounded.
        queue.Throttle = TimeSpan.FromMilliseconds(5);
        for (var i = 1; i <= 260; i++) queue.MarkProcessed(new QueuedPacket(i, "meter-1", i, DateTimeOffset.UtcNow));
        var recent = queue.Recent;
        Assert(recent.Count == 200 && recent[0].Position == 61 && recent[^1].Position == 260, "trail should hold the newest 200, oldest first");
        Assert(queue.Throttle == TimeSpan.Zero, "the flood throttle must clear itself once the queue is empty");

        for (var i = 0; i < IngestQueue.MaxDepth; i++) queue.TryEnqueue("meter-1", i);
        Assert(!queue.TryEnqueue("meter-1", 0), "a full queue must refuse the packet, not grow without bound");
        Assert(queue.TryEnqueue("meter-1", 0, AlertKind.Spike), "a full normal lane must not turn a critical packet away");

        log.Add("queue: normal packets leave in strict FIFO order");
        log.Add("priority queue: criticals jump a backed-up lane, spikes first, ties in arrival order, a full lane sheds load");
        return log;
    }
    //----------------------------------------------------------------------------------------------------------------------//
    private static IEnumerable<string> RunCommandChecks()
    {
        var log = new List<string>();
        var commands = new CommandService();

        Assert(!commands.TryUndo(out _), "undo on an empty stack must report nothing to undo, not throw");

        commands.Issue("open valve A", ["valve-a"], true);
        commands.Issue("shut all valves", ["valve-a", "valve-b"], false);

        Assert(commands.History.Select(c => c.Label).SequenceEqual(["shut all valves", "open valve A"]),
            "history must list the most recent command first");
        Assert(commands.TryGetOverride("valve-a", out var a) && !a && commands.TryGetOverride("valve-b", out var b) && !b,
            "the batch command should have shut both valves");

        // Undo the batch: A goes back to the earlier override, B back to auto.
        Assert(commands.TryUndo(out var undone) && undone!.Label == "shut all valves", "undo must pop the last command issued");
        Assert(commands.TryGetOverride("valve-a", out a) && a, "valve A should be back to the override it had before");
        Assert(!commands.TryGetOverride("valve-b", out _), "valve B had no override before, so it returns to auto");

        Assert(commands.TryUndo(out _) && !commands.TryGetOverride("valve-a", out _), "second undo releases valve A");
        Assert(!commands.TryUndo(out _) && commands.History.Count == 0, "the stack is empty again");

        log.Add("stack: commands undo last-in-first-out, a batch reverts as one, empty undo is safe");

        // A commanded pump stop and the restart after Undo are not anomalies; an
        // uncommanded drop to 0 W still is.
        var store = new TelemetryStore();
        var engine = new AnomalyEngine(store, commands, NullLogger<AnomalyEngine>.Instance);
        var pump = store.Register(new SensorRegistration
        {
            MacAddress = "B8:27:EB:41:02:AA",
            DisplayName = "Borehole Pump",
            DeploymentPath = "Facility A/Zone 2/Sub-Zone C",
            Category = SensorCategory.PumpPower,
            WarningThreshold = 3000
        });
        var at = DateTimeOffset.UtcNow;
        void Read(int watts) => store.Ingest(new TelemetryPacket<int>(pump.Id, watts, pump.Category, at = at.AddSeconds(2)));

        for (var i = 0; i < 30; i++) Read(2200 + i % 5 * 40);

        commands.Issue("stop pump", [pump.Id], false);
        for (var i = 0; i < 10; i++)
        {
            Read(0);
            Assert(engine.Evaluate(pump) is null, "a pump stopped by the operator must not raise an alert");
        }

        commands.TryUndo(out _);
        Read(2210);
        Assert(engine.Evaluate(pump) is null, "restarting the pump must not look like a spike against its stopped readings");

        Read(0);
        Assert(engine.Evaluate(pump)?.Kind == AlertKind.Spike, "a pump dropping to 0 W on its own is still a spike");

        log.Add("pump override: a commanded stop and restart raise nothing, an unexpected stop still alerts");
        return log;
    }
    //----------------------------------------------------------------------------------------------------------------------//
    private static IEnumerable<string> RunRegistryChecks()
    {
        var log = new List<string>();
        var store = new TelemetryStore();

        var valve = store.Register(new SensorRegistration
        {
            MacAddress = "dc-a6-32-77-11-01",
            DisplayName = "Drip Line Valve A",
            DeploymentPath = "Facility A/Zone 1/Sub-Zone A",
            Category = SensorCategory.IrrigationValve
        });

        // One device, however the MAC is spelled: the id the gateway issued, the MAC as
        // it was typed, and a lower-case colon form all land on the same entry.
        Assert(valve.Id == "DC:A6:32:77:11:01", "the registry key is the MAC with colons, upper case");
        Assert(store.TryGet(valve.Id, out var byId) && ReferenceEquals(byId, valve), "lookup by id should find the device");
        Assert(store.TryGet("dc-a6-32-77-11-01", out var byMac) && ReferenceEquals(byMac, valve), "lookup by the MAC as typed should find the same device");
        Assert(store.TryGet("dc:a6:32:77:11:01", out _), "lookup must not care about case");
        Assert(!store.TryGet("00:00:00:00:00:00", out _), "an unregistered MAC is a miss, not an exception");
        Assert(store.Lookups == 4 && store.Misses == 1, $"expected 4 lookups and 1 miss, counted {store.Lookups} and {store.Misses}");

        Assert(store.Remove("dc-a6-32-77-11-01") && !store.Exists(valve.Id), "removing by MAC should remove the device");

        log.Add("dictionary: a device is found by id or by MAC in any spelling, misses are counted");
        return log;
    }
    //----------------------------------------------------------------------------------------------------------------------//
    private static IEnumerable<string> RunTimelineChecks()
    {
        var log = new List<string>();
        var store = new TelemetryStore();

        var probe = store.Register(new SensorRegistration
        {
            MacAddress = "A4:CF:12:9B:04:7E",
            DisplayName = "Soil Moisture North",
            DeploymentPath = "Facility A/Zone 1/Sub-Zone A",
            Category = SensorCategory.SoilMoisture
        });

        // Readings taken at seconds 1..4 arrive in the order 3, 1, 4, 2. The value is the
        // second it was taken at, so the expected timeline reads 1, 2, 3, 4.
        var start = DateTimeOffset.UtcNow.AddMinutes(-30);
        int[] arrivals = [3, 1, 4, 2];
        for (var i = 0; i < arrivals.Length; i++)
            store.LogReading(probe.Id, start.AddSeconds(arrivals[i]), arrivals[i], sequence: i + 1);

        var timeline = store.Timeline(probe.Id, 10);
        Assert(timeline.Select(p => p.Value).SequenceEqual([1, 2, 3, 4]),
            $"out-of-order packets should read back in time order but got {string.Join(",", timeline.Select(p => p.Value))}");
        Assert(timeline.Select(p => p.Sequence).SequenceEqual([2, 4, 1, 3]), "each point should still say when it arrived");
        Assert(store.Timeline(probe.Id, 2).Select(p => p.Value).SequenceEqual([3, 4]), "asking for 2 samples should return the newest 2");

        // Two readings with the same timestamp are both kept, in arrival order.
        var tie = start.AddSeconds(50);
        store.LogReading(probe.Id, tie, 50, sequence: 10);
        store.LogReading(probe.Id, tie, 51, sequence: 11);
        Assert(store.Timeline(probe.Id, 2).Select(p => p.Value).SequenceEqual([50, 51]), "same-timestamp readings should both be kept");

        // Bounded: flood it past capacity and only the newest readings are left.
        for (var i = 0; i < TelemetryStore.TimelineCapacity + 10; i++)
            store.LogReading(probe.Id, start.AddSeconds(100 + i), i, sequence: 100 + i);

        var full = store.Timeline(probe.Id, int.MaxValue);
        Assert(full.Count == TelemetryStore.TimelineCapacity, $"the timeline should stop at {TelemetryStore.TimelineCapacity} readings, holds {full.Count}");
        Assert(full[0].At == start.AddSeconds(110), "the oldest readings should be the ones dropped");

        // A late packet lands in the past; it must not make the node look older than it is.
        store.Ingest(new TelemetryPacket<float>(probe.Id, 31f, probe.Category, start.AddMinutes(5)));
        store.Ingest(new TelemetryPacket<float>(probe.Id, 31f, probe.Category, start));
        Assert(store.LastSeen(probe.Id) == start.AddMinutes(5), "a late packet must not move last-seen backwards");

        log.Add("sorted dictionary: out-of-order packets read back in time order, the log stays bounded");
        return log;
    }
    //----------------------------------------------------------------------------------------------------------------------//
    private static IEnumerable<string> RunDisconnectChecks()
    {
        var log = new List<string>();
        var store = new TelemetryStore();
        var engine = new AnomalyEngine(store, new CommandService(), NullLogger<AnomalyEngine>.Instance);

        var probe = store.Register(new SensorRegistration
        {
            MacAddress = "A4:CF:12:9B:04:7E",
            DisplayName = "Soil Moisture North",
            DeploymentPath = "Facility A/Zone 1/Sub-Zone A",
            Category = SensorCategory.SoilMoisture,
            PublishIntervalSeconds = 2,
            WarningThreshold = 45
        });

        // One packet, then nothing. With a 2s interval the node is silent after 6s and
        // critical after 18s.
        var lastHeard = DateTimeOffset.UtcNow;
        store.Ingest(new TelemetryPacket<float>(probe.Id, 31f, probe.Category, lastHeard));

        Assert(engine.SweepForSilence(lastHeard.AddSeconds(5)).Count == 0, "inside three publish intervals a node is not silent yet");

        var first = engine.SweepForSilence(lastHeard.AddSeconds(8));
        Assert(first.Count == 1 && first[0].Kind == AlertKind.Silence && first[0].Severity == AlertSeverity.Warning,
            "the first sweep past the limit should raise one warning");

        // The node stays silent. Every sweep finds it again; none may raise again.
        for (var seconds = 10; seconds <= 16; seconds += 2)
            Assert(engine.SweepForSilence(lastHeard.AddSeconds(seconds)).Count == 0, "a node that stays silent must not be alerted on again");

        Assert(engine.DuplicatesSkipped == 4, $"four repeat sweeps should have been skipped as duplicates, counted {engine.DuplicatesSkipped}");
        Assert(engine.Disconnected.SequenceEqual([probe.Id]), "the silent node should be in the disconnect set");

        // Much later it is the same outage: the one alert escalates, no second one appears.
        var later = engine.SweepForSilence(lastHeard.AddMinutes(5));
        Assert(later.Count == 1 && later[0].Id == first[0].Id && later[0].Severity == AlertSeverity.Critical,
            "a long outage should escalate the alert it already has");
        Assert(engine.Ranked().Count(a => a.Kind == AlertKind.Silence) == 1, "a silent node has exactly one disconnect alert");

        // Heard from again: out of the set, so a later outage would be a new alert.
        var back = lastHeard.AddMinutes(6);
        store.Ingest(new TelemetryPacket<float>(probe.Id, 31f, probe.Category, back));
        Assert(engine.SweepForSilence(back.AddSeconds(1)).Count == 0 && engine.Disconnected.Count == 0,
            "a node that reports again should leave the disconnect set");

        // Active error states. A baseline swinging between 30 and 44 keeps 46 inside three
        // sigma, so it is a threshold breach (over 45) and not a spike.
        var breachKey = $"{probe.Id}|{AlertKind.ThresholdBreach}";
        Alert? Send(float value)
        {
            store.Ingest(new TelemetryPacket<float>(probe.Id, value, probe.Category, back));
            return engine.Evaluate(probe);
        }

        for (var i = 0; i < 24; i++) Assert(Send(i % 2 == 0 ? 30f : 44f) is null, "readings under the threshold are not alerts");

        var skippedBefore = engine.DuplicatesSkipped;
        Assert(Send(46f) is { Kind: AlertKind.ThresholdBreach }, "the first reading over the threshold should raise a breach");
        Assert(Send(46f) is null && Send(46f) is null, "a breach that is still going on must not be raised again");
        Assert(engine.DuplicatesSkipped == skippedBefore + 2 && engine.ActiveErrors.Contains(breachKey),
            "the two repeats should be skipped as duplicates while the breach is open");

        Assert(Send(40f) is null && !engine.ActiveErrors.Contains(breachKey), "back under the threshold, the error state is cleared");

        log.Add("hash set: a silent node raises one disconnect alert, an open error state is not raised twice");
        return log;
    }
    //----------------------------------------------------------------------------------------------------------------------//
    private static IEnumerable<string> RunPredictionLogChecks()
    {
        var log = new List<string>();
        var store = new TelemetryStore();
        var engine = new AnomalyEngine(store, new CommandService(), NullLogger<AnomalyEngine>.Instance);
        var predictions = new PredictionService(store, engine);

        var probe = store.Register(new SensorRegistration
        {
            MacAddress = "A4:CF:12:9B:04:7F",
            DisplayName = "Soil Moisture South",
            DeploymentPath = "Facility A/Zone 1/Sub-Zone B",
            Category = SensorCategory.SoilMoisture,
            PublishIntervalSeconds = 2
        });

        // Nothing wrong: the action is logged with no context.
        Assert(predictions.Record(OperatorActionKind.Search, "valve").Contexts.Count == 0, "with no fault open an action has no context");

        // The probe goes silent, then the operator searches. The search is tied to that open fault.
        var lastHeard = DateTimeOffset.UtcNow;
        store.Ingest(new TelemetryPacket<float>(probe.Id, 31f, probe.Category, lastHeard));
        var outage = engine.SweepForSilence(lastHeard.AddSeconds(8))[0];

        var search = predictions.Record(OperatorActionKind.Search, "  Pump ");
        Assert(search.Contexts.SequenceEqual(["SoilMoisture|Silence"]),
            $"a search during a moisture outage should carry that fault, got '{string.Join(", ", search.Contexts)}'");
        Assert(search.FollowUp == "Search:pump", "search terms are normalised so 'Pump ' and 'pump' are one follow-up");

        // Repeating an action during the same fault is one habit, not several.
        predictions.Record(OperatorActionKind.Search, "pump");
        predictions.Record(OperatorActionKind.Search, "PUMP");
        Assert(predictions.Rank("SoilMoisture|Silence").Single().Count == 1, "repeats during one fault should count once");
        Assert(predictions.Suggest(outage) is null, "one search is not a habit yet, so nothing is suggested");

        var command = predictions.Record(OperatorActionKind.Command, "Switch Borehole Pump on");
        Assert(command.FollowUp == "Command:Switch Borehole Pump on", "command labels are kept as issued");
        Assert(predictions.Log[0] == command && predictions.Log.Count == 5, "the log is newest first and holds every action");

        // The fault closes and a new one opens: the same search now counts again. Pump outranks the command.
        var back = DateTimeOffset.UtcNow;
        store.Ingest(new TelemetryPacket<float>(probe.Id, 31f, probe.Category, back));
        engine.SweepForSilence(back.AddSeconds(1));
        store.Ingest(new TelemetryPacket<float>(probe.Id, 31f, probe.Category, back.AddSeconds(30)));
        var secondOutage = engine.SweepForSilence(back.AddSeconds(38))[0];
        predictions.Suggest(secondOutage);   // raising the alert is what starts a new count
        predictions.Record(OperatorActionKind.Search, "pump");
        predictions.Record(OperatorActionKind.Search, "PUMP");
        var ranked = predictions.Rank("SoilMoisture|Silence");
        Assert(ranked.Count == 2 && ranked[0] == new RankedFollowUp("Search:pump", 2) && ranked[1].Count == 1,
            $"pump searched in two separate faults should rank first, got {string.Join(", ", ranked)}");
        Assert(predictions.Rank("SoilMoisture|Silence", top: 1).Count == 1, "top limits the ranking");
        Assert(predictions.Rank("TankLevel|Spike").Count == 0, "an event never seen has no follow-ups");
        Assert(predictions.Pairs.Keys.SequenceEqual(["SoilMoisture|Silence"]), "an action with no alert open forms no pair");

        // The moisture alert comes round again: the pump is suggested on its own. The command,
        // done only once, is below MinOccurrences and is left out.
        var suggestion = predictions.Suggest(outage);
        Assert(suggestion is { FollowUps.Count: 1 } && suggestion.FollowUps[0].FollowUp == "Search:pump",
            "a repeated moisture-then-pump pattern should suggest the pump when the alert recurs");
        Assert(predictions.Current == suggestion, "the latest suggestion is kept for a dashboard that opens mid-alert");
        Assert(predictions.Suggest(outage with { Kind = AlertKind.Spike }) is null, "a different event has nothing learned to suggest");
        Assert(predictions.Suggest(outage with { Suppressed = true }) is null, "a suppressed alert gets no suggestion");

        // The node reports again, so the fault is closed: what the operator does now is not a reaction to it.
        back = DateTimeOffset.UtcNow.AddMinutes(1);
        store.Ingest(new TelemetryPacket<float>(probe.Id, 31f, probe.Category, back));
        engine.SweepForSilence(back.AddSeconds(1));
        Assert(predictions.Record(OperatorActionKind.Search, "pump").Contexts.Count == 0, "once the fault closes, actions are no longer tied to it");

        for (var i = 0; i < PredictionService.MaxLog; i++) predictions.Record(OperatorActionKind.Search, $"s{i}");
        Assert(predictions.Log.Count == PredictionService.MaxLog, "the log drops its oldest entries past the cap");

        log.Add("prediction log: every search and command is logged with the alert that was open at the time");
        log.Add("dictionary of counters: after-X-did-Y pairs are counted and the most frequent follow-up ranks first");
        log.Add("suggestion: when a moisture alert recurs, the pump the operator keeps searching for is suggested");
        return log;
    }
    //----------------------------------------------------------------------------------------------------------------------//
    private static IEnumerable<string> RunPersistenceChecks()
    {
        var log = new List<string>();
        var path = Path.Combine(Path.GetTempPath(), $"smartx-predictions-{Guid.NewGuid():N}.json");

        try
        {
            var store = new TelemetryStore();
            var engine = new AnomalyEngine(store, new CommandService(), NullLogger<AnomalyEngine>.Instance);

            var first = new PredictionService(store, engine, path);
            var seeded = first.Insights();
            Assert(seeded.Count >= 3 && seeded.All(i => !i.Open), "a first run starts from seeded habits, none of them open");
            Assert(seeded.SelectMany(i => i.FollowUps).All(f => f.Count >= PredictionService.MinOccurrences), "insights list only follow-ups that are a habit");

            // Learn one more, as a fault would: write straight through the counters by recording under a fault.
            var probe = store.Register(new SensorRegistration
            {
                MacAddress = "A4:CF:12:9B:08:01", DisplayName = "Persist probe", DeploymentPath = "Facility A/Zone 1/Sub-Zone A",
                Category = SensorCategory.SoilMoisture, PublishIntervalSeconds = 2
            });
            var heard = DateTimeOffset.UtcNow;
            store.Ingest(new TelemetryPacket<float>(probe.Id, 31f, probe.Category, heard));
            engine.SweepForSilence(heard.AddSeconds(8));
            first.Record(OperatorActionKind.Search, "valve");
            Assert(first.Insights().Any(i => i.Open && i.Devices.Contains("Persist probe")), "a learned event with a fault open now is flagged open, naming the device");

            var second = new PredictionService(store, engine, path);
            Assert(second.Rank("SoilMoisture|Silence").Any(f => f.FollowUp == "Search:valve" && f.Count == 3), "a restart reloads what was learned (seed 2 + 1 new)");
            Assert(second.Insights().Count == first.Insights().Count, "a restart does not re-seed over saved counters");

            // Reset starts the engine cold, and stays cold across a restart.
            second.Reset();
            Assert(second.Pairs.Count == 0 && second.Log.Count == 0 && second.Insights().Count == 0 && second.Current is null, "reset forgets everything learned");
            Assert(new PredictionService(store, engine, path).Pairs.Count == 0, "a reset engine is not re-seeded on restart");
        }
        finally
        {
            File.Delete(path);
        }

        log.Add("persistence: first run is seeded, counters survive a restart, open faults are flagged in the insights");
        return log;
    }
    //----------------------------------------------------------------------------------------------------------------------//
    private static IEnumerable<string> RunTrendChecks()
    {
        var log = new List<string>();
        var store = new TelemetryStore();
        var predictions = new PredictionService(store, new AnomalyEngine(store, new CommandService(), NullLogger<AnomalyEngine>.Instance));

        // Four moisture probes (threshold 45 %VWC, a packet every 2s), each fed 30 readings.
        var start = DateTimeOffset.UtcNow.AddMinutes(-2);
        string Probe(int n, Func<int, float> reading)
        {
            var probe = store.Register(new SensorRegistration
            {
                MacAddress = $"A4:CF:12:9B:07:{n:X2}",
                DisplayName = $"Trend probe {n}",
                DeploymentPath = "Facility A/Zone 1/Sub-Zone A",
                Category = SensorCategory.SoilMoisture,
                PublishIntervalSeconds = 2
            });

            for (var i = 0; i < PredictionService.TrendSamples; i++)
                store.Ingest(new TelemetryPacket<float>(probe.Id, reading(i), probe.Category, start.AddSeconds(i * 2)));
            return probe.Id;
        }

        Probe(1, i => i % 2 == 0 ? 29f : 31f);          // noise about a flat line
        var climbing = Probe(2, i => 30f + 0.3f * i);   // ends at 38.7, +0.15/s: crosses 45 in 42s
        Probe(3, i => 30f + 0.01f * i);                 // climbing, but would take half an hour
        Probe(4, i => 40f + 0.3f * i);                  // already over 45: a breach, not a trend

        var flags = predictions.TrendFlags();
        Assert(flags.Count == 1 && flags[0].SensorId == climbing,
            $"only the steady climb close to its threshold should be flagged, got {string.Join(", ", flags.Select(f => f.SensorName))}");
        Assert(Math.Abs(flags[0].SecondsToThreshold - 42) < 1 && Math.Abs(flags[0].SlopePerSecond - 0.15) < 1e-3,
            $"the climb should be projected to cross in about 42s, got {flags[0].SecondsToThreshold:0.#}s");

        log.Add("trend: a least-squares line flags a device climbing toward its threshold and when it will cross");
        return log;
    }
    //----------------------------------------------------------------------------------------------------------------------//
    private static bool Throws(Action action)
    {
        try
        {
            action();
            return false;
        }
        catch (CryptographicException)
        {
            return true;
        }
    }
    //----------------------------------------------------------------------------------------------------------------------//
    private static void Assert(bool condition, string because)
    {
        if (!condition) throw new SelfCheckFailure(because);
    }
}
//---------------------------DDDDDDDDDDDoooooooooooooo END OF FILE DDDDDDDDDDDoooooooooooooo---------------------------//
