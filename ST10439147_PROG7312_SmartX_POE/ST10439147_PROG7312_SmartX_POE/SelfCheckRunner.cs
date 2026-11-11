using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using SmartX.Api.Security;
using SmartX.Api.Services;
using SmartX.Shared.Diagnostics;
using SmartX.Shared.Telemetry;
using SmartX.Shared.Topology;

namespace SmartX.Api;

/// <summary>
/// The one runnable check. Runs the shared data-structure suite plus the crypto
/// round-trip that only exists on the server side.
///
/// <code>dotnet run --project src/SmartX.Api -- --selftest</code>
/// </summary>
internal static class SelfCheckRunner
{
    public static async Task<int> RunAsync()
    {
        Console.WriteLine("SmartX self-check");
        Console.WriteLine(new string('-', 64));

        try
        {
            foreach (var line in SelfCheck.Run())
                Console.WriteLine($"  ok  {line}");

            foreach (var line in await RunCryptoChecksAsync())
                Console.WriteLine($"  ok  {line}");

            foreach (var line in await RunQueueChecksAsync())
                Console.WriteLine($"  ok  {line}");

            foreach (var line in RunCommandChecks())
                Console.WriteLine($"  ok  {line}");

            foreach (var line in RunRegistryChecks())
                Console.WriteLine($"  ok  {line}");

            foreach (var line in RunTimelineChecks())
                Console.WriteLine($"  ok  {line}");

            foreach (var line in RunMeshChecks())
                Console.WriteLine($"  ok  {line}");

            foreach (var line in RunDisconnectChecks())
                Console.WriteLine($"  ok  {line}");

            foreach (var line in await RunPredictionLogChecksAsync())
                Console.WriteLine($"  ok  {line}");

            foreach (var line in await RunPersistenceChecksAsync())
                Console.WriteLine($"  ok  {line}");

            foreach (var line in await RunConcurrencyChecksAsync())
                Console.WriteLine($"  ok  {line}");

            foreach (var line in await RunTrendChecksAsync())
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
    private static async Task<IEnumerable<string>> RunCryptoChecksAsync()
    {
        var log = new List<string>();
        var key = RandomNumberGenerator.GetBytes(ChunkedAesGcm.KeySize);

        // Sizes chosen to straddle the chunk boundary: empty, tiny, exactly one chunk,
        // and one chunk plus a byte. Off-by-one framing bugs live at exactly these sizes.
        foreach (var size in new[] { 0, 1, 1024, ChunkedAesGcm.ChunkSize, ChunkedAesGcm.ChunkSize + 1, ChunkedAesGcm.ChunkSize * 2 })
        {
            var plaintext = RandomNumberGenerator.GetBytes(size);

            using var encrypted = new MemoryStream();
            var written = await ChunkedAesGcm.EncryptAsync(new MemoryStream(plaintext), encrypted, key);
            Assert(written == size, $"encrypt should consume all {size} plaintext bytes, reported {written}");

            encrypted.Position = 0;
            using var decrypted = new MemoryStream();
            await ChunkedAesGcm.DecryptAsync(encrypted, decrypted, key);

            Assert(decrypted.ToArray().SequenceEqual(plaintext), $"round trip failed at {size} bytes");
            Assert(encrypted.Length > size, "ciphertext should carry its header and tags");
        }

        log.Add($"crypto: AES-256-GCM round-trips at 0, 1, 1 KB, {ChunkedAesGcm.ChunkSize / 1024} KB and multi-chunk sizes");

        // Tampering must be caught, not silently decrypted into garbage.
        var payload = Encoding.UTF8.GetBytes("sensor=A4:CF:12:9B:04:7E; calibration=1.042");
        using (var sealed1 = new MemoryStream())
        {
            await ChunkedAesGcm.EncryptAsync(new MemoryStream(payload), sealed1, key);

            var bytes = sealed1.ToArray();
            bytes[^1] ^= 0xFF;   // flip a bit in the ciphertext

            Assert(await ThrowsAsync(() => ChunkedAesGcm.DecryptAsync(new MemoryStream(bytes), Stream.Null, key)),
                "a flipped ciphertext bit must fail authentication");
        }

        using (var sealed2 = new MemoryStream())
        {
            await ChunkedAesGcm.EncryptAsync(new MemoryStream(payload), sealed2, key);

            var truncated = sealed2.ToArray()[..^8];
            Assert(await ThrowsAsync(() => ChunkedAesGcm.DecryptAsync(new MemoryStream(truncated), Stream.Null, key)),
                "a truncated attachment must be rejected");
        }

        using (var sealed3 = new MemoryStream())
        {
            await ChunkedAesGcm.EncryptAsync(new MemoryStream(payload), sealed3, key);
            sealed3.Position = 0;

            var wrongKey = RandomNumberGenerator.GetBytes(ChunkedAesGcm.KeySize);
            Assert(await ThrowsAsync(() => ChunkedAesGcm.DecryptAsync(sealed3, Stream.Null, wrongKey)),
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
    private static async Task<IEnumerable<string>> RunQueueChecksAsync()
    {
        var log = new List<string>();
        var queue = new IngestQueue();

        for (var i = 1; i <= 5; i++) queue.TryEnqueue("meter-1", i);
        Assert(queue.Depth == 5, "five packets should be waiting");

        var order = new List<double>();
        while (queue.Depth > 0) order.Add((await queue.DequeueAsync()).Value);
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
        while (queue.Depth + queue.CriticalDepth > 0) order.Add((await queue.DequeueAsync()).Value);
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
    private static IEnumerable<string> RunMeshChecks()
    {
        var log = new List<string>();
        var store = new TelemetryStore();
        var mesh = new MeshNetwork(store);

        SensorRegistration Register(string mac, string path) => store.Register(new SensorRegistration
        {
            MacAddress = mac, DisplayName = mac, DeploymentPath = path, Category = SensorCategory.SoilMoisture
        });

        // Reported links are taken as given, by MAC in any spelling.
        var north = Register("A4:CF:12:9B:04:7E", "Facility A/Zone 1/Sub-Zone A");
        mesh.Link(MeshGraph.GatewayId, "a4-cf-12-9b-04-7e", -55);

        var view = mesh.View();
        Assert(view.Vertices[0].IsGateway, "the gateway should be listed first");
        Assert(view.Links.Single().RssiDbm == -55, "a reported link should keep its RSSI");

        // A node registered with no reported links is linked to what is near it.
        var south = Register("A4:CF:12:9B:04:7F", "Facility A/Zone 1/Sub-Zone A");   // same sub-zone as north
        var vent = Register("DC:A6:32:77:11:02", "Facility A/Zone 1/Sub-Zone B");    // same zone, other sub-zone
        var tank = Register("B8:27:EB:41:02:AD", "Facility B/Roof");                 // nothing nearby

        view = mesh.View();
        bool Linked(string a, string b, int rssi) =>
            view.Links.Any(l => l.RssiDbm == rssi && ((l.From == a && l.To == b) || (l.From == b && l.To == a)));

        Assert(Linked(south.Id, north.Id, -60), "a node should link to a neighbour in its own sub-zone");
        Assert(Linked(vent.Id, north.Id, -70) && Linked(vent.Id, south.Id, -70), "with no sub-zone neighbour it should link to two in its zone");
        Assert(Linked(tank.Id, MeshGraph.GatewayId, -75), "with nothing nearby it should fall back to the gateway");
        Assert(view.Vertices.All(v => v.Degree > 0), "no registered node should be an island");

        // Deleting a node takes its links with it.
        store.Remove(south.Id);
        view = mesh.View();
        Assert(view.Vertices.All(v => v.Id != south.Id), "a deleted node should leave the graph");
        Assert(view.Links.All(l => l.From != south.Id && l.To != south.Id), "a deleted node's links should go with it");

        log.Add("mesh network: reported links kept, unreported nodes auto-linked by location, deletes leave no dangling links");

        // Reachability. What is left: GW - north - vent, and GW - tank. Take north down and
        // the vent, whose only link home ran through north, is stranded.
        var down = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { north.Id };
        var report = mesh.Reachability(MeshGraph.GatewayId, down)!;

        Assert(report.Reachable.Select(r => r.Id).SequenceEqual([MeshGraph.GatewayId, tank.Id]), "only the gateway and the tank should still be reachable");
        Assert(report.Down.Single().Id == north.Id, "the down node should be listed as down");
        Assert(report.Stranded.Single().Id == vent.Id, "the vent only reached home through north, so it should be stranded");

        // The subnet filter narrows the lists, not the search.
        var zone1 = mesh.Reachability(MeshGraph.GatewayId, new HashSet<string>(), subnet: "facility a/zone 1")!;
        Assert(zone1.Reachable.Select(r => r.Id).Order().SequenceEqual(new[] { north.Id, vent.Id }.Order()),
            "a Zone 1 subnet report should list only Zone 1 nodes");
        Assert(zone1.Reachable.Single(r => r.Id == vent.Id).Hops == 2, "hops should still be counted from the gateway, across the whole mesh");

        Assert(mesh.Reachability("not-a-node", down) is null, "an unknown starting node should give no report");

        log.Add("mesh reachability: down and stranded nodes separated, subnet narrows the report not the search");

        // Routes, on a fresh mesh:   GW - A - B - D      B has two ways home (A or C),
        //                             \      /           D only has B.
        //                               C --
        store = new TelemetryStore();
        mesh = new MeshNetwork(store);
        var a = Register("02:00:00:00:00:0A", "Facility A/Zone 1/Sub-Zone A").Id;
        var b = Register("02:00:00:00:00:0B", "Facility A/Zone 1/Sub-Zone A").Id;
        var c = Register("02:00:00:00:00:0C", "Facility A/Zone 1/Sub-Zone A").Id;
        var d = Register("02:00:00:00:00:0D", "Facility A/Zone 1/Sub-Zone A").Id;
        mesh.Link(MeshGraph.GatewayId, a, -50);
        mesh.Link(MeshGraph.GatewayId, c, -60);
        mesh.Link(a, b, -50);
        mesh.Link(c, b, -60);
        mesh.Link(b, d, -55);

        HashSet<string> Down(params string[] ids) => [.. ids];   // default comparer on purpose: Route must cope
        IEnumerable<string> Ids(MeshRoute? r) => r!.Hops.Select(h => h.Id);

        var healthy = mesh.Route(d.ToLowerInvariant(), Down())!;   // any MAC spelling
        Assert(healthy.Status == RouteStatus.Online && healthy.NextHop == b, "a healthy node should be online and hand packets to B");
        Assert(Ids(healthy.Current).SequenceEqual([d, b, a, MeshGraph.GatewayId]), "D's route should be D-B-A-gateway");
        Assert(healthy.Current!.HopCount == 3 && healthy.Current.TotalCost == Math.Round(MeshGraph.CostOf(-55) + 1 + 1, 2), "hop count and energy should add up along the route");

        var healed = mesh.Route(d, Down(a.ToLowerInvariant()))!;
        Assert(healed.Status == RouteStatus.Rerouted, "with A down, D should be rerouted, not lost");
        Assert(Ids(healed.Current).SequenceEqual([d, b, c, MeshGraph.GatewayId]), "the detour should run through C");
        Assert(healed.FailedOnUsual.Single().Id == a && Ids(healed.Usual).Contains(a), "the report should name the node that broke the usual route");
        Assert(mesh.Route(b, Down(a))!.NextHop == c, "with A down, B's next hop should switch to C");

        var cutOff = mesh.Route(d, Down(b))!;
        Assert(cutOff.Status == RouteStatus.Stranded && cutOff.Current is null && cutOff.NextHop is null, "with B down, D has no way home");
        Assert(cutOff.RestoredBy.Single().Id == b, "repairing B should be the one fix for D");

        var bothGone = mesh.Route(b, Down(a, c))!;
        Assert(bothGone.Status == RouteStatus.Stranded && bothGone.RestoredBy.Select(v => v.Id).Order().SequenceEqual(new[] { a, c }.Order()),
            "with A and C both down, bringing back either one should reconnect B");

        Assert(mesh.Route(d, Down(d))!.Status == RouteStatus.Down, "a silent node should be reported down");
        Assert(mesh.Route(MeshGraph.GatewayId, Down()) is null && mesh.Route("nope", Down()) is null, "the gateway and unknown ids have no route");

        // The efficient (MST) route and the Dijkstra yardstick ride along on every live route.
        Assert(Ids(healthy.Efficient).SequenceEqual([d, b, a, MeshGraph.GatewayId]) && healthy.CheapestCost == healthy.Current.TotalCost,
            "on this mesh D's MST route is also its cheapest");
        Assert(healthy.NextHop == healthy.Efficient!.Hops[1].Id, "the next hop should come from the efficient route");
        Assert(Ids(healed.Efficient).SequenceEqual([d, b, c, MeshGraph.GatewayId]), "with A down the MST re-forms and D's efficient route heals through C");
        Assert(cutOff.Efficient is null && cutOff.CheapestCost is null, "a stranded node has no efficient route either");

        log.Add("mesh routes: traced by MAC, heal around a down node with a new next hop, stranded nodes name their repair, efficient route follows the mst");

        // MST report on the same mesh: GW-A (1), A-B (1) and B-D (3.16) are the cheap links;
        // C has two links, GW-C and C-B, both -60 dBm (cost 10), and joins through one of them.
        // The other is the one link the MST leaves out.
        var tree = mesh.Mst(Down());
        Assert(tree.Vertices.Count == 5 && tree.Edges.Count == 4 && tree.Down.Count == 0 && tree.Unreached.Count == 0, "all five vertices should be spanned by four links");
        Assert(tree.TotalCost == Math.Round(1 + 10 + 1 + MeshGraph.CostOf(-55), 2), $"MST should be GW-A + GW-C + A-B + B-D, got {tree.TotalCost}");
        Assert(tree.MeshLinks == 5 && tree.MeshCost == Math.Round(tree.TotalCost + 10, 2), "the full mesh adds the C-B link on top");
        Assert(tree.BfsTreeCost >= tree.TotalCost, "no routing tree can cost less than the MST");

        var withoutB = mesh.Mst(Down(b.ToLowerInvariant()));
        Assert(withoutB.Down.Single().Id == b && withoutB.Unreached.Single().Id == d, "with B down, D cannot be spanned");
        Assert(withoutB.Edges.Count == 2 && withoutB.MeshLinks == 2, "only GW-A and GW-C are left live");

        log.Add("mesh mst report: spans the live mesh, priced against the bfs tree and the full mesh, re-forms around down nodes");

        // A node whose fewest-hop and power-efficient routes differ: E hears the gateway
        // directly but weakly (cost 100), and D strongly (cost 1). BFS sends it direct;
        // the MST sends it E-D-B-A-gateway for 1 + 3.16 + 1 + 1.
        var e = Register("02:00:00:00:00:0E", "Facility A/Zone 1/Sub-Zone A").Id;
        mesh.Link(MeshGraph.GatewayId, e, -70);
        mesh.Link(d, e, -50);

        var split = mesh.Route(e, Down())!;
        Assert(Ids(split.Current).SequenceEqual([e, MeshGraph.GatewayId]), "BFS should take E's one weak hop");
        Assert(Ids(split.Efficient).SequenceEqual([e, d, b, a, MeshGraph.GatewayId]) && split.NextHop == d, "the MST should send E through D instead");
        Assert(split.Efficient!.TotalCost < split.Current!.TotalCost && split.CheapestCost == split.Efficient.TotalCost, "the MST route should be the cheaper one, and here the cheapest possible");

        // B is on E's MST route but not on its BFS route. Losing it must still count as a reroute.
        var lostB = mesh.Route(e, Down(b))!;
        Assert(lostB.Status == RouteStatus.Rerouted && lostB.FailedOnUsual.Single().Id == b,
            "a down node on the route packets actually use should make the node Rerouted");
        Assert(Ids(lostB.Efficient).SequenceEqual([e, MeshGraph.GatewayId]), "with B down, E's only way home is its weak direct link");

        log.Add("mesh routes: when the fewest-hop and mst routes differ, packets follow the mst and its failures count as reroutes");
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
    private static async Task<IEnumerable<string>> RunPredictionLogChecksAsync()
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
        Assert((await predictions.RecordAsync(OperatorActionKind.Search, "valve")).Contexts.Count == 0, "with no fault open an action has no context");

        // The probe goes silent, then the operator searches. The search is tied to that open fault.
        var lastHeard = DateTimeOffset.UtcNow;
        store.Ingest(new TelemetryPacket<float>(probe.Id, 31f, probe.Category, lastHeard));
        var outage = engine.SweepForSilence(lastHeard.AddSeconds(8))[0];

        var search = await predictions.RecordAsync(OperatorActionKind.Search, "  Pump ");
        Assert(search.Contexts.SequenceEqual(["SoilMoisture|Silence"]),
            $"a search during a moisture outage should carry that fault, got '{string.Join(", ", search.Contexts)}'");
        Assert(search.FollowUp == "Search:pump", "search terms are normalised so 'Pump ' and 'pump' are one follow-up");

        // Repeating an action during the same fault is one habit, not several.
        await predictions.RecordAsync(OperatorActionKind.Search, "pump");
        await predictions.RecordAsync(OperatorActionKind.Search, "PUMP");
        Assert((await predictions.RankAsync("SoilMoisture|Silence")).Single().Count == 1, "repeats during one fault should count once");
        Assert(await predictions.SuggestAsync(outage) is null, "one search is not a habit yet, so nothing is suggested");

        var command = await predictions.RecordAsync(OperatorActionKind.Command, "Switch Borehole Pump on");
        Assert(command.FollowUp == "Command:Switch Borehole Pump on", "command labels are kept as issued");
        var entries = await predictions.LogAsync();
        Assert(entries[0] == command && entries.Count == 5, "the log is newest first and holds every action");

        // The fault closes and a new one opens: the same search now counts again. Pump outranks the command.
        var back = DateTimeOffset.UtcNow;
        store.Ingest(new TelemetryPacket<float>(probe.Id, 31f, probe.Category, back));
        engine.SweepForSilence(back.AddSeconds(1));
        store.Ingest(new TelemetryPacket<float>(probe.Id, 31f, probe.Category, back.AddSeconds(30)));
        var secondOutage = engine.SweepForSilence(back.AddSeconds(38))[0];
        await predictions.SuggestAsync(secondOutage);   // raising the alert is what starts a new count
        await predictions.RecordAsync(OperatorActionKind.Search, "pump");
        await predictions.RecordAsync(OperatorActionKind.Search, "PUMP");
        var ranked = await predictions.RankAsync("SoilMoisture|Silence");
        Assert(ranked.Count == 2 && ranked[0] == new RankedFollowUp("Search:pump", 2) && ranked[1].Count == 1,
            $"pump searched in two separate faults should rank first, got {string.Join(", ", ranked)}");
        Assert((await predictions.RankAsync("SoilMoisture|Silence", top: 1)).Count == 1, "top limits the ranking");
        Assert((await predictions.RankAsync("TankLevel|Spike")).Count == 0, "an event never seen has no follow-ups");
        Assert((await predictions.PairsAsync()).Keys.SequenceEqual(["SoilMoisture|Silence"]), "an action with no alert open forms no pair");

        // The moisture alert comes round again: the pump is suggested on its own. The command,
        // done only once, is below MinOccurrences and is left out.
        var suggestion = await predictions.SuggestAsync(outage);
        Assert(suggestion is { FollowUps.Count: 1 } && suggestion.FollowUps[0].FollowUp == "Search:pump",
            "a repeated moisture-then-pump pattern should suggest the pump when the alert recurs");
        Assert(await predictions.CurrentAsync() == suggestion, "the latest suggestion is kept for a dashboard that opens mid-alert");
        Assert(await predictions.SuggestAsync(outage with { Kind = AlertKind.Spike }) is null, "a different event has nothing learned to suggest");
        Assert(await predictions.SuggestAsync(outage with { Suppressed = true }) is null, "a suppressed alert gets no suggestion");

        // The node reports again, so the fault is closed: what the operator does now is not a reaction to it.
        back = DateTimeOffset.UtcNow.AddMinutes(1);
        store.Ingest(new TelemetryPacket<float>(probe.Id, 31f, probe.Category, back));
        engine.SweepForSilence(back.AddSeconds(1));
        Assert((await predictions.RecordAsync(OperatorActionKind.Search, "pump")).Contexts.Count == 0, "once the fault closes, actions are no longer tied to it");

        for (var i = 0; i < PredictionService.MaxLog; i++) await predictions.RecordAsync(OperatorActionKind.Search, $"s{i}");
        Assert((await predictions.LogAsync()).Count == PredictionService.MaxLog, "the log drops its oldest entries past the cap");

        log.Add("prediction log: every search and command is logged with the alert that was open at the time");
        log.Add("dictionary of counters: after-X-did-Y pairs are counted and the most frequent follow-up ranks first");
        log.Add("suggestion: when a moisture alert recurs, the pump the operator keeps searching for is suggested");
        return log;
    }
    //----------------------------------------------------------------------------------------------------------------------//
    private static async Task<IEnumerable<string>> RunPersistenceChecksAsync()
    {
        var log = new List<string>();
        var path = Path.Combine(Path.GetTempPath(), $"smartx-predictions-{Guid.NewGuid():N}.json");

        try
        {
            var store = new TelemetryStore();
            var engine = new AnomalyEngine(store, new CommandService(), NullLogger<AnomalyEngine>.Instance);

            var first = new PredictionService(store, engine, path);
            var seeded = await first.InsightsAsync();
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
            await first.RecordAsync(OperatorActionKind.Search, "valve");
            Assert((await first.InsightsAsync()).Any(i => i.Open && i.Devices.Contains("Persist probe")), "a learned event with a fault open now is flagged open, naming the device");

            var second = new PredictionService(store, engine, path);
            Assert((await second.RankAsync("SoilMoisture|Silence")).Any(f => f.FollowUp == "Search:valve" && f.Count == 3), "a restart reloads what was learned (seed 2 + 1 new)");
            Assert((await second.InsightsAsync()).Count == (await first.InsightsAsync()).Count, "a restart does not re-seed over saved counters");

            // Reset starts the engine cold, and stays cold across a restart.
            await second.ResetAsync();
            Assert((await second.PairsAsync()).Count == 0 && (await second.LogAsync()).Count == 0 && (await second.InsightsAsync()).Count == 0 && await second.CurrentAsync() is null, "reset forgets everything learned");
            Assert((await new PredictionService(store, engine, path).PairsAsync()).Count == 0, "a reset engine is not re-seeded on restart");
        }
        finally
        {
            File.Delete(path);
        }

        log.Add("persistence: first run is seeded, counters survive a restart, open faults are flagged in the insights");
        return log;
    }
    //----------------------------------------------------------------------------------------------------------------------//
    private static async Task<IEnumerable<string>> RunConcurrencyChecksAsync()
    {
        var log = new List<string>();
        var path = Path.Combine(Path.GetTempPath(), $"smartx-predictions-{Guid.NewGuid():N}.json");

        try
        {
            var store = new TelemetryStore();
            var engine = new AnomalyEngine(store, new CommandService(), NullLogger<AnomalyEngine>.Instance);
            var predictions = new PredictionService(store, engine, path);

            var probe = store.Register(new SensorRegistration
            {
                MacAddress = "A4:CF:12:9B:09:01", DisplayName = "Concurrent probe", DeploymentPath = "Facility A/Zone 1/Sub-Zone A",
                Category = SensorCategory.SoilMoisture, PublishIntervalSeconds = 2
            });
            var heard = DateTimeOffset.UtcNow;
            store.Ingest(new TelemetryPacket<float>(probe.Id, 31f, probe.Category, heard));
            engine.SweepForSilence(heard.AddSeconds(8));

            // Every call arrives at once, on a cold engine: one load, no lost counts, the last save holds them all.
            const int actions = 100;
            await Task.WhenAll(Enumerable.Range(0, actions).Select(i =>
                Task.Run(() => predictions.RecordAsync(OperatorActionKind.Search, $"term{i}"))));

            Assert((await predictions.LogAsync()).Count == actions, "every concurrent action should be logged");

            var row = await new PredictionService(store, engine, path).RankAsync("SoilMoisture|Silence", int.MaxValue);
            Assert(row.Count == actions + 1 && row[0] == new RankedFollowUp("Search:valve", 2),
                $"the saved file should hold the seeded habit and all {actions} new ones, holds {row.Count}");
        }
        finally
        {
            File.Delete(path);
        }

        log.Add("async gate: concurrent actions on a cold engine load once, lose no counts and all reach the saved file");
        return log;
    }
    //----------------------------------------------------------------------------------------------------------------------//
    private static async Task<IEnumerable<string>> RunTrendChecksAsync()
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

        var flags = await predictions.TrendFlagsAsync();
        Assert(flags.Count == 1 && flags[0].SensorId == climbing,
            $"only the steady climb close to its threshold should be flagged, got {string.Join(", ", flags.Select(f => f.SensorName))}");
        Assert(Math.Abs(flags[0].SecondsToThreshold - 42) < 1 && Math.Abs(flags[0].SlopePerSecond - 0.15) < 1e-3,
            $"the climb should be projected to cross in about 42s, got {flags[0].SecondsToThreshold:0.#}s");

        log.Add("trend: a least-squares line flags a device climbing toward its threshold and when it will cross");
        return log;
    }
    //----------------------------------------------------------------------------------------------------------------------//
    private static async Task<bool> ThrowsAsync(Func<Task> action)
    {
        try
        {
            await action();
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
