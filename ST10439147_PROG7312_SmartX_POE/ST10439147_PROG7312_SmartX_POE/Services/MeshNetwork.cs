using SmartX.Shared.Telemetry;
using SmartX.Shared.Topology;

namespace SmartX.Api.Services;

/// <summary>
/// Owns the gateway's picture of the physical mesh: a <see cref="MeshGraph"/> whose
/// vertices are the registered ESP32 nodes plus the central gateway, and whose edges are
/// the radio links between them.
///
/// <para><b>Where links come from.</b> Real ESP-WIFI-MESH or Zigbee nodes report which
/// neighbours they can hear and how strongly. Here the <see cref="MeshSimulator"/> plays
/// that part and reports the seeded farm's links through <see cref="Link"/> at boot.</para>
///
/// <para><b>Kept in step with the registry.</b> Every read first syncs the graph against
/// <see cref="TelemetryStore.Sensors"/>: a deleted node drops out with all its links, and
/// a node registered from the dashboard - which no simulator has reported links for - is
/// linked to the nodes it would physically be near (see <see cref="AutoLink"/>), so it
/// never shows up as an island.</para>
/// </summary>
public sealed class MeshNetwork(TelemetryStore store)
{
    /// <summary>Where the gateway physically sits: the pump house, with mains power and the uplink.</summary>
    public const string GatewayPath = "Facility A/Zone 2/Sub-Zone C";

    // Assumed signal for an auto-linked node: a neighbour in the same sub-zone is close,
    // one elsewhere in the zone is further, the gateway is the last resort.
    private const int SameSubZoneRssi = -60;
    private const int SameZoneRssi = -70;
    private const int GatewayFallbackRssi = -75;
    private const int AutoLinkPeers = 2;

    private readonly MeshGraph _graph = new();
    private readonly Lock _gate = new();

    /// <summary>Records a radio link two nodes reported. Either end may be <see cref="MeshGraph.GatewayId"/>.</summary>
    public void Link(string a, string b, int rssiDbm)
    {
        lock (_gate) _graph.Connect(TelemetryStore.KeyFor(a), TelemetryStore.KeyFor(b), rssiDbm);
    }
    //----------------------------------------------------------------------------------------------------------------------//
    /// <summary>The whole mesh, gateway first, then nodes grouped by where they are deployed.</summary>
    public MeshView View()
    {
        var sensors = store.Sensors;
        var byId = sensors.ToDictionary(s => s.Id, StringComparer.OrdinalIgnoreCase);

        lock (_gate)
        {
            Sync(sensors);

            var vertices = Ordered(_graph.Vertices.Select(id => VertexFor(id, byId))).ToList();
            return new MeshView(vertices, _graph.Links.ToList());
        }
    }
    //----------------------------------------------------------------------------------------------------------------------//
    /// <summary>
    /// Runs a BFS from <paramref name="from"/> (the gateway by default) with every node in
    /// <paramref name="down"/> taken out of the mesh, and sorts every other node into
    /// reachable or stranded. Null if <paramref name="from"/> is not on the graph.
    /// </summary>
    /// <param name="down">Nodes that cannot send or relay - the gateway's disconnect set.</param>
    /// <param name="subnet">Optional deployment-path prefix, e.g. <c>Facility A/Zone 1</c>, to narrow the lists to one area.</param>
    public ReachabilityReport? Reachability(string from, IReadOnlySet<string> down, string? subnet = null)
    {
        var sensors = store.Sensors;
        var byId = sensors.ToDictionary(s => s.Id, StringComparer.OrdinalIgnoreCase);
        from = TelemetryStore.KeyFor(from);
        down = IgnoringCase(down);

        lock (_gate)
        {
            Sync(sensors);
            if (!_graph.Contains(from)) return null;

            var tree = _graph.BreadthFirst(from, down);

            // The subnet filter only narrows what is reported. The search itself always runs
            // over the whole mesh, because a Zone 1 node's way home can run through Zone 2.
            bool InSubnet(MeshVertex v) => subnet is null || v.DeploymentPath.StartsWith(subnet, StringComparison.OrdinalIgnoreCase);

            var everyone = Ordered(_graph.Vertices.Select(id => VertexFor(id, byId))).Where(InSubnet).ToList();

            var reachable = tree.Order
                .Select(id => VertexFor(id, byId))
                .Where(InSubnet)
                .Select(v => new ReachableNode(v.Id, v.Name, v.DeploymentPath, tree.Hops[v.Id]))
                .ToList();

            return new ReachabilityReport(
                from,
                subnet,
                reachable,
                Down: everyone.Where(v => down.Contains(v.Id)).ToList(),
                Stranded: everyone.Where(v => !down.Contains(v.Id) && !tree.Reached(v.Id)).ToList(),
                tree.Order);
        }
    }
    //----------------------------------------------------------------------------------------------------------------------//
    /// <summary>
    /// The minimum spanning tree of the live mesh, grown by Prim's from the gateway with
    /// the down nodes taken out - the set of links that keeps every reachable node
    /// connected for the least total transmit energy.
    ///
    /// <para>To show what that buys, the report also prices two alternatives over the same
    /// live nodes: the fewest-hop routing tree BFS builds (section 3), and leaving every
    /// link in the mesh active.</para>
    /// </summary>
    public MstReport Mst(IReadOnlySet<string> down)
    {
        var sensors = store.Sensors;
        var byId = sensors.ToDictionary(s => s.Id, StringComparer.OrdinalIgnoreCase);
        down = IgnoringCase(down);

        lock (_gate)
        {
            Sync(sensors);

            var mst = _graph.MinimumSpanningTree(MeshGraph.GatewayId, down);

            // The BFS tree's links are each node's link to its BFS parent.
            var bfs = _graph.BreadthFirst(MeshGraph.GatewayId, down);
            var bfsCost = bfs.Parent.Sum(p => _graph.LinkBetween(p.Key, p.Value)!.Cost);

            // Every link between two live nodes.
            var live = _graph.Links.Where(l => !down.Contains(l.From) && !down.Contains(l.To)).ToList();

            var everyone = Ordered(_graph.Vertices.Select(id => VertexFor(id, byId))).ToList();

            return new MstReport(
                MeshGraph.GatewayId,
                mst.Order.Select(id => VertexFor(id, byId)).ToList(),
                mst.Edges,
                mst.TotalCost,
                Math.Round(bfsCost, 2),
                Math.Round(live.Sum(l => l.Cost), 2),
                live.Count,
                Down: everyone.Where(v => down.Contains(v.Id)).ToList(),
                Unreached: everyone.Where(v => !down.Contains(v.Id) && !mst.Spans(v.Id)).ToList());
        }
    }
    //----------------------------------------------------------------------------------------------------------------------//
    /// <summary>
    /// Traces one node's route to the gateway by MAC or id, and what an outage has done to it.
    /// Null if the node is not on the mesh (or is the gateway itself).
    ///
    /// <para>A BFS with the down nodes taken out gives the fewest-hop <b>current</b> route.
    /// If the usual route runs through a down node and a route still exists, the mesh has
    /// healed around the failure and <see cref="RouteReport.NextHop"/> says where the node
    /// should now send its packets. If no route exists, the node is stranded, and one more
    /// BFS per down node finds which repairs would reconnect it.</para>
    ///
    /// <para><b>Power-efficient routing (section 4).</b> Packets follow the minimum spanning
    /// tree. <b>Usual</b> is the node's path along the healthy MST and <b>efficient</b> its
    /// path along the live MST (down nodes excluded), so Rerouted means the route packets
    /// really use was broken, and <see cref="RouteReport.NextHop"/> follows the live MST.
    /// <b>Current</b> stays as the fewest-hop (BFS) comparison, and Dijkstra's cheapest cost
    /// shows how close the MST route comes to the best this one node could do alone.
    /// Reachability - stranded or not, and what would restore it - is still BFS.</para>
    /// </summary>
    public RouteReport? Route(string macOrId, IReadOnlySet<string> down)
    {
        var sensors = store.Sensors;
        var byId = sensors.ToDictionary(s => s.Id, StringComparer.OrdinalIgnoreCase);
        var id = TelemetryStore.KeyFor(macOrId);
        down = IgnoringCase(down);

        lock (_gate)
        {
            Sync(sensors);
            if (!_graph.Contains(id) || id == MeshGraph.GatewayId) return null;

            var node = VertexFor(id, byId);
            // Usual = the route packets take on the healthy mesh: along the healthy MST.
            var usual = RouteOf(_graph.MinimumSpanningTree(MeshGraph.GatewayId).PathFrom(id), byId);
            var failed = usual?.Hops.Where(h => down.Contains(h.Id) && h.Id != id).ToList() ?? [];

            if (down.Contains(id))
                return new RouteReport(node, RouteStatus.Down, usual, null, null, failed, null, null, []);

            var current = RouteOf(_graph.BreadthFirst(MeshGraph.GatewayId, down).PathFrom(id), byId);

            if (current is null)
            {
                // Try bringing each down node back on its own: any that reconnects this node
                // on its own is a repair worth doing first.
                var restoredBy = down
                    .Where(_graph.Contains)
                    .Where(d => _graph.BreadthFirst(MeshGraph.GatewayId, down.Where(o => o != d).ToHashSet()).Reached(id))
                    .Select(d => VertexFor(d, byId))
                    .OrderBy(v => v.Name)
                    .ToList();

                return new RouteReport(node, RouteStatus.Stranded, usual, null, null, failed, null, null, restoredBy);
            }

            // Prim's spans exactly what BFS reaches, so a node with a current route always has an efficient one.
            var efficient = RouteOf(_graph.MinimumSpanningTree(MeshGraph.GatewayId, down).PathFrom(id), byId)!;
            var cheapest = Math.Round(_graph.CheapestCosts(MeshGraph.GatewayId, down)[id], 2);

            var status = failed.Count > 0 ? RouteStatus.Rerouted : RouteStatus.Online;
            return new RouteReport(node, status, usual, current, efficient, failed, efficient.Hops[1].Id, cheapest, []);
        }
    }

    // Turns a tree's parent chain (node first, gateway last) into hops plus the links between them. Caller holds _gate.
    private MeshRoute? RouteOf(IReadOnlyList<string>? path, IReadOnlyDictionary<string, SensorRegistration> byId)
    {
        if (path is null) return null;

        var links = path.Zip(path.Skip(1), (a, b) => _graph.LinkBetween(a, b)!).ToList();
        return new MeshRoute(
            path.Select(p => VertexFor(p, byId)).ToList(),
            links,
            Math.Round(links.Sum(l => l.Cost), 2));
    }
    //----------------------------------------------------------------------------------------------------------------------//
    private MeshVertex VertexFor(string id, IReadOnlyDictionary<string, SensorRegistration> byId)
        => byId.TryGetValue(id, out var s)
            ? new MeshVertex(id, s.DisplayName, s.DeploymentPath, false, _graph.DegreeOf(id))
            : new MeshVertex(id, "Central gateway", GatewayPath, true, _graph.DegreeOf(id));

    // Vertex ids ignore case, so a caller's down set must too, whatever comparer it was built with.
    private static HashSet<string> IgnoringCase(IEnumerable<string> ids)
        => ids.Select(TelemetryStore.KeyFor).ToHashSet(StringComparer.OrdinalIgnoreCase);

    // Gateway first, then nodes grouped by where they are deployed.
    private static IEnumerable<MeshVertex> Ordered(IEnumerable<MeshVertex> vertices)
        => vertices.OrderByDescending(v => v.IsGateway).ThenBy(v => v.DeploymentPath).ThenBy(v => v.Name);
    //----------------------------------------------------------------------------------------------------------------------//
    // Caller holds _gate.
    private void Sync(IReadOnlyCollection<SensorRegistration> sensors)
    {
        _graph.AddVertex(MeshGraph.GatewayId);

        var registered = sensors.Select(s => s.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var id in _graph.Vertices.ToList())
            if (id != MeshGraph.GatewayId && !registered.Contains(id))
                _graph.RemoveVertex(id);

        foreach (var sensor in sensors)
            if (!_graph.Contains(sensor.Id))
                AutoLink(sensor, sensors);
    }
    //----------------------------------------------------------------------------------------------------------------------//
    /// <summary>
    /// Links a node nobody has reported links for to up to two nodes already in the mesh:
    /// same sub-zone first, then same zone. With neither nearby, it links straight to the
    /// gateway, so every registered node is always reachable on the graph.
    /// </summary>
    // ponytail: placement is guessed from the deployment path. Replace with the node's own
    // neighbour/RSSI report once real hardware is in the loop.
    private void AutoLink(SensorRegistration sensor, IReadOnlyCollection<SensorRegistration> sensors)
    {
        var zone = ZoneOf(sensor.DeploymentPath);

        var peers = sensors
            .Where(s => s.Id != sensor.Id && _graph.Contains(s.Id))
            .Select(s => (s.Id, Rssi:
                string.Equals(s.DeploymentPath, sensor.DeploymentPath, StringComparison.OrdinalIgnoreCase) ? SameSubZoneRssi
                : string.Equals(ZoneOf(s.DeploymentPath), zone, StringComparison.OrdinalIgnoreCase) ? SameZoneRssi
                : (int?)null))
            .Where(p => p.Rssi is not null)
            .OrderByDescending(p => p.Rssi)
            .Take(AutoLinkPeers)
            .ToList();

        if (peers.Count == 0)
        {
            _graph.Connect(sensor.Id, MeshGraph.GatewayId, GatewayFallbackRssi);
            return;
        }

        foreach (var (id, rssi) in peers) _graph.Connect(sensor.Id, id, rssi!.Value);
    }

    // "Facility A/Zone 1/Sub-Zone B" -> "Facility A/Zone 1"
    private static string ZoneOf(string path) => string.Join('/', path.Split('/').Take(2));
}
//---------------------------DDDDDDDDDDDoooooooooooooo END OF FILE DDDDDDDDDDDoooooooooooooo---------------------------//
