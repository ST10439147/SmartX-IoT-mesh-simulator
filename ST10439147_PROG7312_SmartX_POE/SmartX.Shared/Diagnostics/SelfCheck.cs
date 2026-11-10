using System.ComponentModel.DataAnnotations;
using System.Text.Json;
using SmartX.Shared.Telemetry;
using SmartX.Shared.Topology;

namespace SmartX.Shared.Diagnostics;

/// <summary>
/// One runnable check over the non-trivial logic: ring wraparound, operator overloading,
/// the jagged-to-List hand-off, recursive validation under hostile depth, and the claim
/// that none of it boxes.
///
/// <para>Run it with <c>dotnet run --project src/SmartX.Api -- --selftest</c>.
/// Non-zero exit means something below is broken.</para>
/// </summary>
public static class SelfCheck
{
    public static IReadOnlyList<string> Run()
    {
        var log = new List<string>();

        CircularBufferWrapsAndKeepsOrder(log);
        OperatorsAggregateAndCompare(log);
        GenericPacketCarriesAllThreePayloadTypesWithoutBoxing(log);
        JaggedStagingFlushesIntoList(log);
        RecursiveValidationAcceptsAGoodTree(log);
        RecursiveValidationRejectsBadNesting(log);
        RecursiveValidationSurvivesPathologicalDepth(log);
        RecursiveValidationDetectsCycles(log);
        PacketSurvivesJsonRoundTrip(log);
        EverySensorIsASpecificAgriculturalDevice(log);
        AvlTreeStaysBalancedAndAnswersRanges(log);
        MeshGraphIsUndirectedAndWeighted(log);
        BreadthFirstFindsReachableAndStrandedNodes(log);
        MinHeapAlwaysPopsTheSmallest(log);
        PrimsMinimumSpanningTreeMatchesKruskal(log);

        return log;
    }
    //----------------------------------------------------------------------------------------------------------------------//
    // Reference [62]: Kruskal, J. B. (1956). On the shortest spanning subtree of a graph and the traveling salesman problem. Proc. AMS, 7(1), 48-50.
    private static void PrimsMinimumSpanningTreeMatchesKruskal(List<string> log)
    {
        const string gw = MeshGraph.GatewayId;

        // A triangle: the direct GW-B link is weak (cost 100), going through A costs 1 + 1.
        // BFS would take the direct link because it is fewer hops; the MST must not.
        var triangle = new MeshGraph();
        triangle.Connect(gw, "A", -50);
        triangle.Connect("A", "B", -50);
        triangle.Connect(gw, "B", -70);
        triangle.Connect("B", "D", -55);

        var mst = triangle.MinimumSpanningTree(gw);
        Assert(mst.TotalCost == Math.Round(1 + 1 + MeshGraph.CostOf(-55), 2), $"MST should cost 1 + 1 + 3.16, got {mst.TotalCost}");
        Assert(mst.Edges.Count == 3 && mst.Edges.All(e => !(e.From == gw && e.To == "B")), "the expensive direct GW-B link should be left out");
        Assert(mst.Parent["B"] == "A" && mst.Parent["D"] == "B" && mst.Order[0] == gw, "the tree should be rooted at the gateway with B hanging off A");
        Assert(mst.Edges.All(e => mst.Parent[e.To] == e.From), "every tree link should point from parent to child");
        Assert(triangle.BreadthFirst(gw).Parent["B"] == gw, "for contrast, BFS should have taken the 1-hop direct link");

        // Down nodes are left out; what only they connected cannot be spanned.
        var withoutB = triangle.MinimumSpanningTree(gw, new HashSet<string> { "b" });
        Assert(withoutB.Spans("A") && !withoutB.Spans("B") && !withoutB.Spans("D") && withoutB.TotalCost == 1, "with B down the tree is just GW-A");
        Assert(triangle.MinimumSpanningTree(gw, new HashSet<string> { gw }).Order.Count == 0, "a down root spans nothing");

        // Random meshes: Prim's total must equal an independent Kruskal's on the same component.
        var random = new Random(7312);
        for (var round = 0; round < 25; round++)
        {
            var graph = new MeshGraph();
            var nodes = Enumerable.Range(0, 40).Select(i => $"N{i}").Prepend(gw).ToList();
            for (var e = 0; e < 120; e++)
            {
                var a = nodes[random.Next(nodes.Count)];
                var b = nodes[random.Next(nodes.Count)];
                if (a != b) graph.Connect(a, b, -random.Next(40, 86));
            }

            var down = new HashSet<string> { nodes[1 + random.Next(40)], nodes[1 + random.Next(40)] };
            var prim = graph.MinimumSpanningTree(gw, down);
            var component = graph.BreadthFirst(gw, down).Order.ToHashSet();

            Assert(prim.Order.ToHashSet().SetEquals(component), "Prim's should span exactly the nodes BFS can reach");
            Assert(prim.Edges.Count == component.Count - 1, "a spanning tree of n nodes has n - 1 links");
            Assert(Math.Round(prim.TotalCost, 2) == Math.Round(Kruskal(graph, component), 2),
                $"Prim's ({prim.TotalCost}) and Kruskal's should agree on the minimum cost, round {round}");
        }

        log.Add("prim's mst: skips the costly direct link BFS takes, heals around down nodes, matches kruskal's on 25 random meshes");

        MstRoutesAgainstDijkstra(triangle, random, log);
    }
    //----------------------------------------------------------------------------------------------------------------------//
    // Reference [63]: Bellman, R. (1958). On a routing problem. Quarterly of Applied Mathematics, 16(1), 87-90.
    private static void MstRoutesAgainstDijkstra(MeshGraph triangle, Random random, List<string> log)
    {
        const string gw = MeshGraph.GatewayId;

        // On the triangle, the MST route home for B goes through A, which is also the cheapest.
        var mst = triangle.MinimumSpanningTree(gw);
        var cheapest = triangle.CheapestCosts(gw);
        Assert(mst.PathFrom("D")!.SequenceEqual(["D", "B", "A", gw]), "D's MST route should run D-B-A-gateway");
        Assert(mst.PathFrom(gw)!.SequenceEqual([gw]) && mst.PathFrom("nope") is null, "the root's route is itself; an unknown node has none");
        Assert(cheapest[gw] == 0 && cheapest["B"] == 2 && Math.Abs(cheapest["D"] - (2 + MeshGraph.CostOf(-55))) < 1e-9,
            "Dijkstra: B costs 1 + 1 through A, not 100 direct");
        Assert(!triangle.CheapestCosts(gw, new HashSet<string> { "b" }).ContainsKey("D"), "Dijkstra should not route through a down node");

        // Random meshes: Dijkstra must match Bellman-Ford, and no route - MST or BFS - may beat Dijkstra.
        var gaps = 0;
        var routes = 0;
        for (var round = 0; round < 25; round++)
        {
            var graph = new MeshGraph();
            var nodes = Enumerable.Range(0, 40).Select(i => $"N{i}").Prepend(gw).ToList();
            for (var e = 0; e < 120; e++)
            {
                var a = nodes[random.Next(nodes.Count)];
                var b = nodes[random.Next(nodes.Count)];
                if (a != b) graph.Connect(a, b, -random.Next(40, 86));
            }

            var dijkstra = graph.CheapestCosts(gw);
            var bellman = BellmanFord(graph, gw);
            Assert(dijkstra.Count == bellman.Count && dijkstra.All(p => Math.Abs(p.Value - bellman[p.Key]) < 1e-6),
                $"Dijkstra should agree with Bellman-Ford on every node, round {round}");

            var tree = graph.MinimumSpanningTree(gw);
            var bfs = graph.BreadthFirst(gw);
            foreach (var (node, best) in dijkstra)
            {
                var mstCost = PathCost(graph, tree.PathFrom(node)!);
                var bfsCost = PathCost(graph, bfs.PathFrom(node)!);
                Assert(mstCost >= best - 1e-6 && bfsCost >= best - 1e-6, "no route can be cheaper than Dijkstra's");
                if (mstCost > best + 1e-6) gaps++;
                routes++;
            }
        }

        // The honest part: the MST is cheapest for the network, not always for each node.
        Assert(gaps > 0, "on random meshes some MST routes should cost more than that node's own cheapest path");

        log.Add($"mst routes: follow the tree home, never beat dijkstra (which matches bellman-ford); {gaps} of {routes:N0} node routes cost more than their own cheapest path");
    }

    private static double PathCost(MeshGraph graph, IReadOnlyList<string> path)
        => path.Zip(path.Skip(1), (a, b) => graph.LinkBetween(a, b)!.Cost).Sum();

    // Bellman-Ford: relax every link V - 1 times. Slow, O(V x E), but with no heap and no
    // greedy choice it is an independent answer to check Dijkstra's against.
    private static Dictionary<string, double> BellmanFord(MeshGraph graph, string root)
    {
        var cost = new Dictionary<string, double> { [root] = 0 };
        for (var i = 1; i < graph.VertexCount; i++)
            foreach (var link in graph.Links.SelectMany(l => new[] { l, l with { From = l.To, To = l.From } }))
                if (cost.TryGetValue(link.From, out var from) && from + link.Cost < cost.GetValueOrDefault(link.To, double.PositiveInfinity))
                    cost[link.To] = from + link.Cost;

        return cost;
    }

    // Kruskal's with union-find, as an independent answer to check Prim's against: sort every
    // link by cost, keep each one that joins two separate groups.
    private static double Kruskal(MeshGraph graph, HashSet<string> component)
    {
        var group = component.ToDictionary(v => v, v => v);
        string Find(string v) => group[v] == v ? v : group[v] = Find(group[v]);

        var total = 0.0;
        foreach (var link in graph.Links.Where(l => component.Contains(l.From) && component.Contains(l.To)).OrderBy(l => l.Cost))
        {
            var (a, b) = (Find(link.From), Find(link.To));
            if (a == b) continue;
            group[a] = b;
            total += link.Cost;
        }

        return total;
    }
    //----------------------------------------------------------------------------------------------------------------------//
    // Reference [60]: Cormen, T. H. et al. (2022). Introduction to Algorithms, 4th ed., ch. 6. MIT Press.
    private static void MinHeapAlwaysPopsTheSmallest(List<string> log)
    {
        var heap = new MinHeap<string, double>();
        Assert(!heap.TryPop(out _, out _) && !heap.TryPeek(out _, out _) && heap.Height == 0, "an empty heap has nothing to pop or peek");

        // 10,000 random priorities with plenty of repeats: popping them all must give them back sorted.
        var random = new Random(7312);
        var priorities = Enumerable.Range(0, 10_000).Select(_ => Math.Round(random.NextDouble() * 500, 1)).ToList();
        foreach (var p in priorities) heap.Push($"link-{p}", p);

        Assert(heap.Count == 10_000 && heap.IsValid(), "every push should keep the heap property");
        Assert(heap.Height == 14, $"10,000 items should sit in floor(log2 n) + 1 = 14 levels, got {heap.Height}");
        Assert(heap.TryPeek(out _, out var top) && top == priorities.Min(), "peek should show the smallest priority");

        var popped = new List<double>();
        while (heap.TryPop(out var element, out var priority))
        {
            Assert(element == $"link-{priority}", "an element should come out with its own priority");
            popped.Add(priority);
            if (popped.Count % 1_000 == 0) Assert(heap.IsValid(), "every pop should keep the heap property");
        }

        Assert(popped.SequenceEqual(priorities.Order()), "popping everything should return the priorities in ascending order");
        Assert(heap.Count == 0, "the heap should be empty after popping everything");

        // Interleaved pushes and pops, the way Prim's uses it.
        heap.Push("c", 3);
        heap.Push("a", 1);
        Assert(heap.TryPop(out var first, out _) && first == "a", "the smaller of two should come out first");
        heap.Push("b", 2);
        heap.Push("d", 0.5);
        Assert(heap.TryPop(out var next, out _) && next == "d", "an item pushed after a pop should still jump the queue if it is smallest");
        Assert(heap.TryPop(out next, out _) && next == "b" && heap.TryPop(out next, out _) && next == "c", "the rest should follow in order");

        log.Add("min-heap: array-backed, pops 10,000 random priorities in sorted order, O(log n) height, interleaved push/pop");
    }
    //----------------------------------------------------------------------------------------------------------------------//
    private static void BreadthFirstFindsReachableAndStrandedNodes(List<string> log)
    {
        //   GW - A - B - D - E        B has two ways home (via A or via C);
        //    \      /                 D and E only have one: through B.
        //      C --
        const string gw = MeshGraph.GatewayId;
        var graph = new MeshGraph();
        graph.Connect(gw, "A", -50);
        graph.Connect(gw, "C", -50);
        graph.Connect("A", "B", -50);
        graph.Connect("C", "B", -50);
        graph.Connect("B", "D", -50);
        graph.Connect("D", "E", -50);
        graph.AddVertex("F");   // never linked to anything

        var all = graph.BreadthFirst(gw);
        Assert(all.Order.Count == 6 && !all.Reached("F"), "BFS should reach the 6 linked vertices and not the island");
        Assert(all.Hops["A"] == 1 && all.Hops["C"] == 1 && all.Hops["B"] == 2 && all.Hops["D"] == 3 && all.Hops["E"] == 4,
            "hop counts should be the fewest hops from the gateway");
        Assert(all.Order.Select(id => all.Hops[id]).Zip(all.Order.Skip(1).Select(id => all.Hops[id])).All(p => p.First <= p.Second),
            "BFS should discover nodes in rings: never a 3-hop node before a 2-hop one");
        Assert(!all.Parent.ContainsKey(gw) && all.Parent["E"] == "D" && all.Parent["D"] == "B", "each node should point back one hop towards the root");

        // A goes down: B is still reachable via C, at the same hop count.
        var withoutA = graph.BreadthFirst(gw, new HashSet<string> { "A" });
        Assert(!withoutA.Reached("A") && withoutA.Reached("E"), "losing A should strand nobody - B has a second way home");
        Assert(withoutA.Parent["B"] == "C", "with A down, B should be reached through C");

        // B goes down: D and E only ever had B, so they are cut off.
        var withoutB = graph.BreadthFirst(gw, new HashSet<string> { "b" });   // any letter case
        Assert(withoutB.Reached("A") && withoutB.Reached("C") && !withoutB.Reached("D") && !withoutB.Reached("E"),
            "losing B should strand D and E, which have no other path");
        Assert(withoutB.Order.Count == 3, "a down node must not be visited or passed through");

        // A search can start anywhere, and a down or unknown root reaches nothing.
        Assert(graph.BreadthFirst("E").Hops[gw] == 4, "BFS from a node should find the gateway 4 hops away");
        Assert(graph.BreadthFirst(gw, new HashSet<string> { gw }).Order.Count == 0, "a down root reaches nothing");
        Assert(graph.BreadthFirst("nope").Order.Count == 0, "an unknown root reaches nothing");

        // Following the parent pointers gives the route home.
        Assert(all.PathFrom("E")!.SequenceEqual(["E", "D", "B", "A", gw]), "E's route should run E-D-B-A-gateway");
        Assert(withoutA.PathFrom("E")!.SequenceEqual(["E", "D", "B", "C", gw]), "with A down, E's route should heal through C");
        Assert(withoutB.PathFrom("E") is null, "a stranded node should have no route");
        Assert(all.PathFrom(gw)!.SequenceEqual([gw]), "the root's route is just itself");

        log.Add("bfs: exact hop counts, ring order, reroutes around a down node, strands what only it connected, parent chain is the route");
    }
    //----------------------------------------------------------------------------------------------------------------------//
    private static void MeshGraphIsUndirectedAndWeighted(List<string> log)
    {
        var graph = new MeshGraph();
        graph.Connect(MeshGraph.GatewayId, "A", -50);
        graph.Connect("A", "B", -60);
        graph.Connect("B", "C", -70);
        graph.Connect("A", "C", -55);

        Assert(graph.VertexCount == 4 && graph.LinkCount == 4, $"expected 4 vertices and 4 links, got {graph.VertexCount} and {graph.LinkCount}");
        Assert(graph.Links.Count() == 4, "Links should list each undirected link once");

        // Undirected: the link is filed under both ends, pointing away from each.
        Assert(graph.Neighbours("A").Any(l => l.To == "B") && graph.Neighbours("B").Any(l => l.To == "A"), "a link should be visible from both ends");
        Assert(graph.Neighbours("A").All(l => l.From == "A"), "each end's list should hold links leaving that end");
        Assert(graph.DegreeOf("A") == 3, "A hears the gateway, B and C");

        // Reconnecting a pair updates the reading instead of adding a parallel edge.
        graph.Connect("B", "A", -65);
        Assert(graph.LinkCount == 4 && graph.DegreeOf("A") == 3, "relinking a pair should not add a second edge");
        Assert(graph.Neighbours("A").Single(l => l.To == "B").RssiDbm == -65, "relinking should update the RSSI on both ends");

        // Cost: every 10 dB weaker is ten times the energy.
        Assert(MeshGraph.CostOf(-50) == 1 && MeshGraph.CostOf(-60) == 10 && MeshGraph.CostOf(-70) == 100,
            "cost should be 1, 10, 100 at -50, -60, -70 dBm");

        // Removing a node drops every link that touched it, from the other ends too.
        Assert(graph.RemoveVertex("A"), "removing a known vertex should succeed");
        Assert(graph.VertexCount == 3 && graph.LinkCount == 1, $"only B-C should be left, got {graph.LinkCount} links");
        Assert(graph.DegreeOf(MeshGraph.GatewayId) == 0 && graph.Neighbours("B").All(l => l.To != "A"), "no neighbour may still point at a removed node");
        Assert(!graph.RemoveVertex("A"), "removing it twice should report false");

        var threw = false;
        try { graph.Connect("B", "b", -40); }
        catch (ArgumentException) { threw = true; }
        Assert(threw, "a node linking to itself (in any letter case) should be rejected");

        log.Add("mesh graph: undirected adjacency list, no parallel edges, removal cleans both ends, cost x10 per 10 dB");
    }
    //----------------------------------------------------------------------------------------------------------------------//
    // Reference [59]: Wikipedia. AVL tree. https://en.wikipedia.org/wiki/AVL_tree
    private static void AvlTreeStaysBalancedAndAnswersRanges(List<string> log)
    {
        // Sorted keys are the worst case: a plain BST would end up 10,000 deep.
        var tree = new AvlTree<int, int>();
        for (var i = 0; i < 10_000; i++) tree.Add(i, i);

        Assert(tree.Count == 10_000, "every insert should be counted");
        Assert(tree.IsValid(), "sorted inserts should leave a valid, balanced AVL tree");
        Assert(tree.Height <= 20, $"10,000 sorted keys should sit at most ~1.44 log2(n) deep, got {tree.Height}");

        // Range answers match a brute-force scan, and touch far fewer nodes than n.
        var hits = tree.Range(4_000, 4_099, out var visited);
        Assert(hits.SequenceEqual(Enumerable.Range(4_000, 100)), "range should return exactly 4000..4099 in order");
        Assert(visited < 300, $"a 100-key range should visit O(log n + k) nodes, visited {visited}");
        Assert(tree.Range(20_000, 30_000, out _).Count == 0, "a range past the data should be empty");
        Assert(tree.Range(5, 1, out _).Count == 0, "a reversed range should be empty");

        // Random order, duplicates replace rather than add.
        var random = new Random(7312);
        var shuffled = new AvlTree<int, int>();
        var reference = new SortedDictionary<int, int>();
        for (var i = 0; i < 5_000; i++)
        {
            var key = random.Next(0, 3_000);
            shuffled.Add(key, i);
            reference[key] = i;
        }

        Assert(shuffled.Count == reference.Count, "a repeated key should replace, not add");
        Assert(shuffled.IsValid(), "random inserts should leave a valid AVL tree");
        Assert(shuffled.Range(500, 1_500, out _).SequenceEqual(reference.Where(p => p.Key is >= 500 and <= 1_500).Select(p => p.Value)),
            "range over random keys should match a sorted scan");

        // Dropping the oldest keeps it balanced and gives back the minimum each time.
        for (var i = 0; i < 9_000; i++)
            Assert(tree.TryRemoveMin(out var key, out _) && key == i, "remove-min should return keys in ascending order");

        Assert(tree.Count == 1_000 && tree.IsValid(), "the tree should stay balanced while the oldest are dropped");

        // Timestamp keys, the way the store uses them: same instant, different sequence, both kept.
        var at = DateTimeOffset.UnixEpoch;
        var history = new AvlTree<(DateTimeOffset, long), double>();
        history.Add((at, 1), 1);
        history.Add((at, 2), 2);
        history.Add((at.AddHours(3), 3), 3);
        Assert(history.Range((at, long.MinValue), (at.AddHours(2), long.MaxValue), out _).SequenceEqual([1d, 2d]),
            "a time window should include every reading at its boundary timestamp and nothing past it");

        log.Add("avl tree: balanced under sorted inserts, range queries prune, remove-min keeps it balanced");
    }
    //----------------------------------------------------------------------------------------------------------------------//
    // Reference [12]: Wikipedia. Circular buffer.
    //   https://en.wikipedia.org/wiki/Circular_buffer
    private static void CircularBufferWrapsAndKeepsOrder(List<string> log)
    {
        var buffer = new CircularTelemetryBuffer<int>(4);

        for (var i = 1; i <= 6; i++)
            buffer.Add(new TelemetryPacket<int>("meter-1", i, SensorCategory.PumpPower));

        Assert(buffer.Count == 4, "buffer should cap at its capacity");
        Assert(buffer.Capacity == 4, "capacity should not drift");
        Assert(buffer.TotalWritten == 6, "lifetime counter should include overwritten packets");

        // 1 and 2 were overwritten; what remains is 3,4,5,6 oldest-first.
        var values = buffer.ToList().Select(p => p.Value).ToArray();
        Assert(values.SequenceEqual([3, 4, 5, 6]), $"expected 3,4,5,6 after wrap but got {string.Join(",", values)}");

        Assert(buffer[0].Value == 3, "indexer 0 must be the oldest retained packet");
        Assert(buffer.TryGetNewest(out var newest) && newest.Value == 6, "newest packet should be 6");
        Assert(buffer.TakeLast(2).Select(p => p.Value).SequenceEqual([5, 6]), "TakeLast should return the tail");

        log.Add("ring buffer: wraps, preserves order, indexes oldest-first");
    }
    //----------------------------------------------------------------------------------------------------------------------//
    // Reference [3]: Microsoft. Generic math (System.Numerics.INumber<T>). Microsoft Learn.
    //   https://learn.microsoft.com/dotnet/standard/generics/math
    // Reference [8]: Microsoft. Operator overloading - predefined unary, arithmetic and comparison operators. C# language reference, Microsoft Learn.
    //   https://learn.microsoft.com/dotnet/csharp/language-reference/operators/operator-overloading
    private static void OperatorsAggregateAndCompare(List<string> log)
    {
        var earlier = DateTimeOffset.UtcNow.AddSeconds(-5);
        var later = DateTimeOffset.UtcNow;

        var meter1 = new SensorReading<float>("meter-1", 240.5f, later);
        var meter2 = new SensorReading<float>("meter-2", 118.25f, earlier);

        var combined = meter1 + meter2;
        Assert(Math.Abs(combined.Value - 358.75f) < 0.001f, "operator + should aggregate the two loads");
        Assert(combined.SensorId == "meter-1+meter-2", "aggregate should name its sources");
        Assert(combined.TimestampUtc == earlier, "an aggregate is only as fresh as its stalest input");

        var delta = meter1 - meter2;
        Assert(Math.Abs(delta.Value - 122.25f) < 0.001f, "operator - should give the delta");
        Assert(Math.Abs((-meter1).Value + 240.5f) < 0.001f, "unary negation should flip the sign");

        Assert(meter1 > meter2, "meter1 draws more than meter2");
        Assert(meter2 < meter1, "the mirrored comparison must agree");
        Assert(meter1 > 200f, "comparison straight against a bare float should work");
        Assert(meter2 <= 118.25f, "<= against a bare float should be inclusive");

        var meter1Again = new SensorReading<float>("meter-1", 240.5f, later);
        Assert(meter1 >= meter1Again, ">= must hold for equal readings");
        Assert(meter1 == meter1Again, "== should compare by value");
        Assert(meter1 != meter2, "!= must be the negation of ==");
        Assert(meter1.GetHashCode() == meter1Again.GetHashCode(), "equal readings must hash equally");
        Assert((float)meter1 == 240.5f, "explicit conversion should yield the raw value");

        // Same operators, a different numeric type - generic math, not per-type overloads.
        var wattA = new SensorReading<int>("inverter-a", 900);
        var wattB = new SensorReading<int>("inverter-b", 350);
        Assert((wattA + wattB).Value == 1250, "operator + should work identically for int");

        var farm = new[] { meter1, meter2, new SensorReading<float>("meter-3", 41.25f, later) };
        Assert(Math.Abs(farm.Sum().Value - 400f) < 0.001f, "fold over operator + should total the farm");

        log.Add("operators: +, -, unary -, ==, !=, <, >, <=, >= over float and int");
    }
    //----------------------------------------------------------------------------------------------------------------------//
    // Reference [1]: Microsoft. Generics in C#. Microsoft Learn.
    //   https://learn.microsoft.com/dotnet/csharp/fundamentals/types/generics
    private static void GenericPacketCarriesAllThreePayloadTypesWithoutBoxing(List<string> log)
    {
        var moisture = new TelemetryPacket<float>("probe-7", 41.8f, SensorCategory.SoilMoisture);
        var wattage = new TelemetryPacket<int>("meter-1", 2400, SensorCategory.PumpPower);
        var valve = new TelemetryPacket<bool>("valve-3", true, SensorCategory.IrrigationValve);

        Assert(moisture.Value == 41.8f && wattage.Value == 2400 && valve.Value, "one wrapper, three payload types");
        Assert(valve == new TelemetryPacket<bool>("valve-3", true, SensorCategory.IrrigationValve, valve.TimestampUtc),
            "packet equality should compare id, value and timestamp");

        // The no-boxing claim, measured rather than asserted in a comment. Filling a
        // pre-allocated ring with 50 000 value-type packets must not allocate heap.
        // If TelemetryPacket<T> were a class, or if T were boxed into object anywhere
        // on this path, this would allocate megabytes.
        var buffer = new CircularTelemetryBuffer<float>(512);
        var packet = new TelemetryPacket<float>("probe-7", 41.8f, SensorCategory.SoilMoisture);

        buffer.Add(packet); // warm up, so JIT and first-touch costs are not counted

        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < 50_000; i++) buffer.Add(in packet);
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert(allocated < 4096, $"ingest path allocated {allocated} bytes for 50 000 packets - something is boxing");

        log.Add($"generics: float/int/bool through one wrapper, {allocated} bytes allocated over 50 000 ingests");
    }
    //----------------------------------------------------------------------------------------------------------------------//
    // Reference [13]: Microsoft. Jagged arrays (C# programming guide). Microsoft Learn.
    //   https://learn.microsoft.com/dotnet/csharp/programming-guide/arrays/jagged-arrays
    // Reference [14]: Microsoft. Multidimensional arrays (C# programming guide). Microsoft Learn.
    //   https://learn.microsoft.com/dotnet/csharp/language-reference/builtin-types/arrays
    private static void JaggedStagingFlushesIntoList(List<string> log)
    {
        var archive = new TelemetryBatchArchive(cycleCapacity: 3);
        var now = DateTimeOffset.UtcNow;

        // Deliberately ragged: the mesh delivered 2, then 4, then 1 packet.
        archive.StageCycle([1f, 2f], now);
        archive.StageCycle([3f, 4f, 5f, 6f], now.AddSeconds(1));
        archive.StageCycle([7f], now.AddSeconds(2));

        Assert(archive.CycleCount == 3, "three cycles staged");
        Assert(archive.WidestCycle == 4, "widest cycle should be 4 samples");

        var jagged = archive.ToJagged();
        Assert(jagged.Length == 3 && jagged[0].Length == 2 && jagged[1].Length == 4 && jagged[2].Length == 1,
            "jagged rows should keep their individual lengths");

        var packets = archive.Flush("probe-7", SensorCategory.SoilMoisture);
        Assert(packets.Count == 7, $"7 raw samples should become 7 packets, got {packets.Count}");
        Assert(packets is List<TelemetryPacket<float>>, "flush target must be a generic List<T>");
        Assert(Math.Abs(packets[^1].Value - 7f) < 0.001f, "last packet should be the newest cycle's sample");

        var dense = archive.ToDenseMatrix();
        Assert(dense.GetLength(0) == 3 && dense.GetLength(1) == 4, "dense projection should be 3x4");
        Assert(float.IsNaN(dense[0, 2]), "short rows must be padded with NaN, not silently zeroed");
        Assert(Math.Abs(dense[1, 3] - 6f) < 0.001f, "the widest row should survive the projection intact");

        // Fourth cycle overwrites the first.
        archive.StageCycle([8f, 9f], now.AddSeconds(3));
        Assert(archive.CycleCount == 3, "archive is a ring and stays bounded");
        Assert(Math.Abs(archive.ToJagged()[0][0] - 3f) < 0.001f, "oldest cycle should have been evicted");

        log.Add("arrays: ragged float[][] staging, float[,] projection, flush into List<T>");
    }
    //----------------------------------------------------------------------------------------------------------------------//
    private static DeploymentNode GoodTree() =>
        DeploymentNode.Create("Facility A", NodeKind.Facility,
            DeploymentNode.Create("Zone 1", NodeKind.Zone,
                DeploymentNode.Create("Sub-Zone B", NodeKind.SubZone,
                    new DeploymentNode { Name = "node-3", Kind = NodeKind.Node, SensorId = "A4:CF:12:9B:04:7E" },
                    new DeploymentNode { Name = "node-4", Kind = NodeKind.Node, SensorId = "A4:CF:12:9B:04:7F" })));
    //----------------------------------------------------------------------------------------------------------------------//
    private static void RecursiveValidationAcceptsAGoodTree(List<string> log)
    {
        var report = TopologyValidator.Validate(GoodTree());

        Assert(report.IsValid, "a well-formed Facility/Zone/Sub-Zone/Node tree should validate: "
                               + string.Join("; ", report.Issues.Select(i => $"{i.Path}: {i.Message}")));
        Assert(report.NodesVisited == 5, $"expected 5 nodes visited, got {report.NodesVisited}");
        Assert(report.MaxDepthReached == 4, $"expected depth 4, got {report.MaxDepthReached}");
        Assert(TopologyValidator.PathExists(GoodTree(), "Facility A/Zone 1/Sub-Zone B/node-3"),
            "the documented path should resolve");
        Assert(!TopologyValidator.PathExists(GoodTree(), "Facility A/Zone 9/node-3"),
            "a path through a zone that does not exist must not resolve");

        log.Add("recursion: valid four-tier tree accepted, breadcrumb path resolves");
    }
    //----------------------------------------------------------------------------------------------------------------------//
    private static void RecursiveValidationRejectsBadNesting(List<string> log)
    {
        // A Node hung straight off the Facility, skipping Zone and Sub-Zone.
        var bad = DeploymentNode.Create("Facility A", NodeKind.Facility,
            new DeploymentNode { Name = "node-3", Kind = NodeKind.Node, SensorId = "AA:BB:CC:DD:EE:FF" });

        var report = TopologyValidator.Validate(bad);
        Assert(!report.IsValid, "a Node directly inside a Facility should be rejected");
        Assert(report.Issues.Any(i => i.Path == "Facility A/node-3" && i.Severity == IssueSeverity.Error),
            "the issue should be addressed by its breadcrumb path");

        // Same sensor mounted at two places in the tree.
        var duplicated = DeploymentNode.Create("Facility A", NodeKind.Facility,
            DeploymentNode.Create("Zone 1", NodeKind.Zone,
                DeploymentNode.Create("Sub-Zone B", NodeKind.SubZone,
                    new DeploymentNode { Name = "node-3", Kind = NodeKind.Node, SensorId = "DUP" },
                    new DeploymentNode { Name = "node-4", Kind = NodeKind.Node, SensorId = "DUP" })));

        Assert(!TopologyValidator.Validate(duplicated).IsValid, "one sensor cannot be mounted twice");

        log.Add("recursion: tier-skipping and duplicate sensor mounts rejected with paths");
    }
    //----------------------------------------------------------------------------------------------------------------------//
    // Reference [16]: Microsoft. StackOverflowException Class (cannot be caught by a try/catch). Microsoft Learn.
    //   https://learn.microsoft.com/dotnet/api/system.stackoverflowexception
    private static void RecursiveValidationSurvivesPathologicalDepth(List<string> log)
    {
        // 50 000 tiers. Without the depth guard this is a StackOverflowException, which
        // .NET cannot catch - the process dies outright and takes the gateway with it.
        var root = new DeploymentNode { Name = "Facility A", Kind = NodeKind.Facility };
        var cursor = root;
        for (var i = 0; i < 50_000; i++)
        {
            var child = new DeploymentNode { Name = $"tier-{i}", Kind = NodeKind.Zone };
            cursor.Children.Add(child);
            cursor = child;
        }

        var report = TopologyValidator.Validate(root);

        Assert(!report.IsValid, "a 50 000-tier payload should be reported as invalid");
        Assert(report.MaxDepthReached <= TopologyValidator.MaxDepth,
            $"walk went {report.MaxDepthReached} deep, past the {TopologyValidator.MaxDepth} ceiling");
        Assert(report.Issues.Any(i => i.Message.Contains("ceiling")), "the depth ceiling should be the reported reason");

        log.Add($"recursion: 50 000-tier payload stopped at depth {report.MaxDepthReached}, no stack overflow");
    }
    //----------------------------------------------------------------------------------------------------------------------//
    private static void RecursiveValidationDetectsCycles(List<string> log)
    {
        var root = DeploymentNode.Create("Facility A", NodeKind.Facility);
        var zone = DeploymentNode.Create("Zone 1", NodeKind.Zone);
        root.Children.Add(zone);
        zone.Children.Add(root); // wired back into its own ancestor

        var report = TopologyValidator.Validate(root);
        Assert(report.Issues.Any(i => i.Message.Contains("Cycle")), "a self-referencing tree should be caught, not looped over");

        log.Add("recursion: cycle in the deployment graph detected and reported");
    }
    //----------------------------------------------------------------------------------------------------------------------//
    // Reference [2]: Microsoft. How to serialize and deserialize JSON using System.Text.Json. Microsoft Learn.
    //   https://learn.microsoft.com/dotnet/standard/serialization/system-text-json/how-to
    private static void PacketSurvivesJsonRoundTrip(List<string> log)
    {
        var original = new TelemetryPacket<float>("probe-7", 41.8f, SensorCategory.SoilMoisture);
        var json = JsonSerializer.Serialize(original);
        var restored = JsonSerializer.Deserialize<TelemetryPacket<float>>(json);

        Assert(restored == original, $"generic packet should survive the wire; got back {json}");

        var boolPacket = new TelemetryPacket<bool>("valve-3", true, SensorCategory.IrrigationValve);
        Assert(JsonSerializer.Deserialize<TelemetryPacket<bool>>(JsonSerializer.Serialize(boolPacket)) == boolPacket,
            "the bool payload should survive the wire too");

        log.Add("serialisation: TelemetryPacket<float> and <bool> round-trip through JSON");
    }
    //----------------------------------------------------------------------------------------------------------------------//
    private static void EverySensorIsASpecificAgriculturalDevice(List<string> log)
    {
        // Spec() throws for a category with no row, so an enum member added without its
        // payload and unit fails here instead of on the first registration of that type.
        var categories = Enum.GetValues<SensorCategory>();
        var payloads = categories.Select(c => c.Spec().Payload).Distinct().Count();
        Assert(payloads == 3, "the categories must still exercise float, int and bool payloads");

        var pump = new SensorRegistration { Category = SensorCategory.PumpPower };
        Assert(pump.Payload == PayloadKind.Integer && pump.Unit == "W", "payload and unit must come from the category");

        // Enum JSON accepts bare numbers, so an undefined category has to die in validation.
        var bogus = new SensorRegistration
        {
            MacAddress = "A4:CF:12:9B:04:7E",
            DisplayName = "Mystery probe",
            DeploymentPath = "Facility A/Zone 1",
            Category = (SensorCategory)99
        };
        var results = new List<ValidationResult>();
        Validator.TryValidateObject(bogus, new ValidationContext(bogus), results, validateAllProperties: true);
        Assert(results.Count == 1 && results[0].MemberNames.Contains(nameof(SensorRegistration.Category)),
            "an undefined category must be the one thing validation rejects");

        log.Add($"categories: {categories.Length} agricultural sensor types, payload and unit fixed per type, unknown types rejected");
    }
    //----------------------------------------------------------------------------------------------------------------------//
    private static void Assert(bool condition, string because)
    {
        if (!condition) throw new SelfCheckFailure(because);
    }
}

public sealed class SelfCheckFailure(string message) : Exception(message);
//---------------------------DDDDDDDDDDDoooooooooooooo END OF FILE DDDDDDDDDDDoooooooooooooo---------------------------//
